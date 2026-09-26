// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;

namespace kaliteConfig.Models
{
    public partial class ExtensionItem : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        
        // Null means not supported on this engine
        public string? ChromiumExtensionId { get; set; } 
        public string? FirefoxAddonSlug { get; set; }

        [ObservableProperty]
        public partial bool IsSelected { get; set; } = true;

        [ObservableProperty]
        public partial bool IsAvailable { get; set; } = true;
    }
}
