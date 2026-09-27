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
    /// <summary>
    /// The AMD half of the overclock page: every installed AMD adapter and the
    /// tuning domains its driver reports. It is a section rather than a page
    /// because overclocking is one subject - the NVIDIA panel above it is
    /// simply the half of the hardware that answers NVAPI.
    ///
    /// The whole control collapses when the machine has no AMD driver at all:
    /// without ADLX there is nothing true to say, and a permanent "no AMD
    /// adapter" card on every NVIDIA machine is noise.
    /// </summary>
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
