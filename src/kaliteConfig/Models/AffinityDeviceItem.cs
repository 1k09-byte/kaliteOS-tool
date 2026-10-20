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
using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models
{
    public partial class AffinityDeviceItem : ObservableObject
    {
        [ObservableProperty]
        public partial string Name { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string Category { get; set; } = string.Empty;

        [ObservableProperty]
        public partial string DeviceInstanceId { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsChecked { get; set; } = true;

        [ObservableProperty]
        public partial string IrqText { get; set; } = "-";

        [ObservableProperty]
        public partial string AffinityText { get; set; } = "-";

        [ObservableProperty]
        public partial string RssText { get; set; } = string.Empty;

        public bool IsNetwork => Category == "Network";

        [ObservableProperty]
        public partial bool IsVisible { get; set; } = true;

        [ObservableProperty]
        public partial bool MsiEnabled { get; set; }

        [ObservableProperty]
        public partial double MsiLimit { get; set; }

        [ObservableProperty]
        public partial double MaxMsiLimit { get; set; }

        public double EffectiveMaxMsiLimit => MaxMsiLimit > 0 ? MaxMsiLimit : 2048;

        public string MaxMsiLimitText => MaxMsiLimit > 0 ? $"{MaxMsiLimit:0}" : string.Empty;

        [ObservableProperty]
        public partial string MsiLimitText { get; set; } = "-";

        [ObservableProperty]
        public partial string DevicePolicyShort { get; set; } = "-";

        [ObservableProperty]
        public partial string DevicePriorityShort { get; set; } = "Undefined";

        [ObservableProperty]
        public partial string SelectedPriority { get; set; } = "Undefined";

        [ObservableProperty]
        public partial string SelectedPolicy { get; set; } = "IrqPolicyMachineDefault";

        [ObservableProperty]
        public partial string SelectedThreadCountText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsProcessorMaskExpanded { get; set; }

        public System.Collections.ObjectModel.ObservableCollection<ProcessorCoreGroup> CoreGroups { get; } = new();

        public string CategoryGlyph => Category switch
        {
            "Graphics" => "G",
            "Network" => "N",
            "Usb" => "U",
            "Audio" => "A",
            _ => "?"
        };

        public string CategoryLabel => Category switch
        {
            "Graphics" => "Graphics card",
            "Network" => "Network adapter",
            "Usb" => "USB controller",
            "Audio" => "Audio controller",
            _ => Category
        };

        public string MsiEnabledText => MsiEnabled ? "On" : "Off";

        partial void OnMsiEnabledChanged(bool value)
        {
            OnPropertyChanged(nameof(MsiEnabledText));
        }

        partial void OnMaxMsiLimitChanged(double value)
        {
            OnPropertyChanged(nameof(EffectiveMaxMsiLimit));
            OnPropertyChanged(nameof(MaxMsiLimitText));
        }
    }

    public partial class ProcessorThreadItem : ObservableObject
    {
        public int Index { get; set; }

        public string Label => $"Thread {Index}";

        [ObservableProperty]
        public partial bool IsChecked { get; set; }
    }

    public sealed record PolicyOption(string Label, string Value);

    public partial class ProcessorCoreGroup : ObservableObject
    {
        public int CoreIndex { get; set; }

        [ObservableProperty]
        public partial string Title { get; set; } = string.Empty;

        public System.Collections.ObjectModel.ObservableCollection<ProcessorThreadItem> Threads { get; } = new();
    }
}
