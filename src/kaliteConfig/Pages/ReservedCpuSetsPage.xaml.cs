// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.UI.Xaml.Controls;
using kaliteConfig.ViewModels;

namespace kaliteConfig.Pages
{
    public sealed partial class ReservedCpuSetsPage : Page
    {
        public ReservedCpuSetsViewModel ViewModel { get; } = new();

        public ReservedCpuSetsPage()
        {
            this.InitializeComponent();
            this.DataContext = ViewModel;
        }
    }
}
