// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.GpuOverclock.Views
{
    public sealed partial class AmdGpuSection : UserControl
    {
        public AmdGpuSection()
        {
            this.InitializeComponent();
            this.Loaded += async (s, e) =>
            {
                await Vm.EnsureLoadedAsync();
                Visibility = Vm.AdlxAvailable ? Visibility.Visible : Visibility.Collapsed;
            };
        }
    }
}
