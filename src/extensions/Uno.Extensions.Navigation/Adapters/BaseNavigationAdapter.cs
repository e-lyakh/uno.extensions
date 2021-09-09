﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Uno.Extensions.Navigation.Controls;
using Windows.Foundation;
#if WINDOWS_UWP || UNO_UWP_COMPATIBILITY
using Windows.UI.Xaml.Controls;
using Windows.UI.Popups;
using UICommand = Windows.UI.Popups.UICommand;
#else
using Windows.UI.Popups;
using UICommand = Windows.UI.Popups.UICommand;
using Microsoft.UI.Xaml.Controls;
#endif

namespace Uno.Extensions.Navigation.Adapters
{
    public abstract class BaseNavigationAdapter<TControl> : INavigationAdapter<TControl>
    {
        public const string PreviousViewUri = "..";
        public const string MessageDialogUri = "__md__";
        public const string MessageDialogParameterContent = MessageDialogUri + "content";
        public const string MessageDialogParameterTitle = MessageDialogUri + "title";
        public const string MessageDialogParameterOptions = MessageDialogUri + "options";
        public const string MessageDialogParameterDefaultCommand = MessageDialogUri + "default";
        public const string MessageDialogParameterCancelCommand = MessageDialogUri + "cancel";
        public const string MessageDialogParameterCommands = MessageDialogUri + "commands";

        protected IInjectable<TControl> ControlWrapper { get; }

        public string Name { get; set; }

        protected INavigationMapping Mapping { get; }

        protected IServiceProvider Services { get; }

        public INavigationService Navigation { get; set; }

        protected IList<(string, NavigationContext)> NavigationContexts { get; } = new List<(string, NavigationContext)>();

        protected IList<object> OpenDialogs { get; } = new List<object>();

        public void Inject(TControl control)
        {
            ControlWrapper.Inject(control);
        }

        public BaseNavigationAdapter(
            // INavigationService navigation, // Note: Don't pass in - implement INaviationAware instead
            IServiceProvider services,
            INavigationMapping navigationMapping,
            IInjectable<TControl> control)
        {
            Services = services.CreateScope().ServiceProvider;
            Mapping = navigationMapping;
            ControlWrapper = control;
        }

        public NavigationResponse Navigate(NavigationContext context)
        {
            var request = context.Request;

            var navTask = InternalNavigate(context);

            return new NavigationResponse(request, navTask, context.CancellationSource, context.ResultCompletion.Task);
        }

        private async Task InternalNavigate(NavigationContext context)
        {
            var navBackRequired = await EndCurrentNavigationContext(context);

            if (context.CancellationToken.IsCancellationRequested)
            {
                await Task.FromCanceled(context.CancellationToken);
            }

            context = await AdapterNavigate(context, navBackRequired);

            if (context.CanCancel)
            {
                context.CancellationToken.Register(() =>
                {
                    Navigation.NavigateToPreviousView(context.Request.Sender);
                });
            }
        }

        protected abstract Task<NavigationContext> AdapterNavigate(NavigationContext context, bool navBackRequired);

        protected async Task DoForwardNavigation(NavigationContext context, Action<NavigationContext, object> adapterNavigation)
        {
            var mapping = Mapping.LookupByPath(context.Path);
            if (mapping is not null)
            {
                context = context with { Mapping = mapping };
            }

            // Push the new navigation context
            NavigationContexts.Push((context.Path, context));

            var vm = await InitializeViewModel();

            var data = context.Data;
            if (context.Path == MessageDialogUri)
            {
                var md = new MessageDialog(data[MessageDialogParameterContent] as string, data[MessageDialogParameterTitle] as string)
                {
                    Options = (MessageDialogOptions)data[MessageDialogParameterOptions],
                    DefaultCommandIndex = (uint)data[MessageDialogParameterDefaultCommand],
                    CancelCommandIndex = (uint)data[MessageDialogParameterCancelCommand]
                };
                md.Commands.AddRange((data[MessageDialogParameterCommands] as UICommand[]) ?? new UICommand[] { });
                var showTask = md.ShowAsync();
                OpenDialogs.Add(showTask);
                showTask.AsTask().ContinueWith(result =>
                {
                    if (result.Status != TaskStatus.Canceled &&
                    context.ResultCompletion.Task.Status != TaskStatus.Canceled &&
                    context.ResultCompletion.Task.Status != TaskStatus.RanToCompletion)
                    {
                        Navigation.Navigate(new NavigationRequest(md, new NavigationRoute(new Uri(PreviousViewUri, UriKind.Relative), result.Result)));
                    }
                });
            }
            else if (mapping.View?.IsSubclassOf(typeof(ContentDialog)) ?? false)
            {
                var dialog = Activator.CreateInstance(mapping.View) as ContentDialog;
                if (vm is not null)
                {
                    dialog.DataContext = vm;
                }
                if (dialog is INavigationAware navAware)
                {
                    navAware.Navigation = Navigation;
                }
                OpenDialogs.Add(dialog);
                dialog.ShowAsync().AsTask().ContinueWith(result =>
                {
                    if (result.Status != TaskStatus.Canceled &&
                    context.ResultCompletion.Task.Status != TaskStatus.Canceled &&
                    context.ResultCompletion.Task.Status != TaskStatus.RanToCompletion)
                    {
                        Navigation.Navigate(new NavigationRequest(dialog, new NavigationRoute(new Uri(PreviousViewUri, UriKind.Relative), result.Result)));
                    }
                });
            }
            else
            {
                adapterNavigation(context, vm);
                //var view = Frame.Navigate(context.Mapping.View, context.Data, vm);
                //if (view is INavigationAware navAware)
                //{
                //    navAware.Navigation = Navigation;
                //}

                //if (context.PathIsRooted)
                //{
                //    while (NavigationContexts.Count > 1)
                //    {
                //        NavigationContexts.RemoveAt(0);
                //    }

                //    Frame.ClearBackStack();
                //}

                //if (removeCurrentPageFromBackStack)
                //{
                //    NavigationContexts.RemoveAt(NavigationContexts.Count - 2);
                //    Frame.RemoveLastFromBackStack();
                //}
            }
            await ((vm as INavigationStart)?.Start(context, true) ?? Task.CompletedTask);
        }

        protected async Task<bool> EndCurrentNavigationContext(NavigationContext context)
        {
            var frameNavigationRequired = true;
            // If there's a current nav context, make sure it's stopped before
            // we proceed - this could cancel the navigation, so need to know
            // before we remove anything from backstack
            if (NavigationContexts.Count > 0)
            {
                var currentVM = await StopCurrentViewModel(context);

                if (context.IsCancelled)
                {
                    return false;
                }

                if (context.Path == PreviousViewUri)
                {
                    var responseData = context.Data.TryGetValue(string.Empty, out var response) ? response : default;

                    var previousContext = NavigationContexts.Pop().Item2;

                    if (previousContext.Path == MessageDialogUri)
                    {
                        frameNavigationRequired = false;
                        var dialog = OpenDialogs.LastOrDefault(x => x is IAsyncOperation<IUICommand>) as IAsyncOperation<IUICommand>;
                        if (dialog is not null)
                        {
                            OpenDialogs.Remove(dialog);
                            dialog.Cancel();
                        }
                    }

                    if (previousContext.Mapping?.View?.IsSubclassOf(typeof(ContentDialog)) ?? false)
                    {
                        frameNavigationRequired = false;
                        var dialog = OpenDialogs.LastOrDefault(x => x.GetType() == previousContext.Mapping.View) as ContentDialog;
                        if (dialog is not null)
                        {
                            OpenDialogs.Remove(dialog);
                            if (!(responseData is ContentDialogResult))
                            {
                                dialog.Hide();
                            }
                        }
                    }

                    if (previousContext.Request.Result is not null)
                    {
                        var completion = previousContext.ResultCompletion;
                        if (completion is not null)
                        {
                            completion.SetResult(responseData);
                        }
                    }
                }

            }

            return frameNavigationRequired;
        }

        protected async Task<object> StopCurrentViewModel(NavigationContext navigation)
        {
            var ctx = NavigationContexts.Peek();
            var path = ctx.Item1;
            var context = ctx.Item2;

            object oldVm = default;
            if (context.Mapping?.ViewModel is not null)
            {
                var services = context.Services;
                oldVm = services.GetService(context.Mapping.ViewModel);
                await ((oldVm as INavigationStop)?.Stop(navigation, false) ?? Task.CompletedTask);
            }
            return oldVm;
        }

        protected async Task<object> InitializeViewModel()
        {
            var ctx = NavigationContexts.Peek();
            var path = ctx.Item1;
            var context = ctx.Item2;

            var mapping = context.Mapping;
            object vm = default;
            if (mapping?.ViewModel is not null)
            {
                var services = context.Services;
                var dataFactor = services.GetService<ViewModelDataProvider>();
                dataFactor.Parameters = context.Data;

                vm = services.GetService(mapping.ViewModel);
                await ((vm as IInitialise)?.Initialize(context) ?? Task.CompletedTask);
            }
            return vm;
        }
    }
}
