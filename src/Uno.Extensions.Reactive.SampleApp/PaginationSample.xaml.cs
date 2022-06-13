﻿using System;
using System.Linq;
using Windows.UI.Xaml.Controls;

namespace Uno.Extensions.Reactive.SampleApp;

public sealed partial class PaginationSample : Page
{
	public PaginationSample()
	{
		this.InitializeComponent();

		DataContext = new PaginationSampleViewModel.BindablePaginationSampleViewModel();
	}
}
