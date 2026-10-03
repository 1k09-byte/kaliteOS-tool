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
// App settings page (persisted via ThemeService).
// Written in the Windows App SDK C# dialect. See docs/GALLERY-REFERENCE.md section 2.

using System;
using kaliteConfig.Services;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.Pages
{
    /// <summary>x:Bind helper: nullable download percent → ProgressBar value.</summary>
    public static class SettingsPageBindings
    {
        public static double UpdateProgressValue(double? percent) => percent ?? 0;
    }
    /// <summary>
    /// App settings page (persisted via ThemeService).
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        private readonly kaliteConfig.Services.StartupService _startup = new();
        private bool _syncingStartupToggle;
        // Update banner VM: only ever populated in the CONSUMER flavor (the
        // startup check below is consumer-only). In the full flavor it stays
        // inert so the XAML banner compiles but never opens.
        private readonly ViewModels.UpdateViewModel Vm = new();

        // ThemeServiceAttach restores the saved ComboBox selection during the
        // ComboBox's own Loaded, which raises SelectionChanged. Ignore those and
        // only record a Material the user actually picked.
        private static bool _appearanceReady;

        public SettingsPage()
        {
            InitializeComponent();
            // Real assembly version - never goes stale like the old hardcoded
            // "Version 1.0.0" string did.
            try
            {
                var v = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                if (v != null)
                    AboutCard.Description = $"Version {v.ToString()} - View development team and application links";
            }
            catch { }
            Loaded += SettingsPage_Loaded;

            // App theme / Material are owned and persisted by DevWinUI's ThemeService:
            // the ComboBoxes are wired through ThemeServiceAttach, which restores the
            // saved choice on load and writes a new one on selection. The Material
            // default (None = solid black surface) is only applied while the user has
            // never picked one, so record the first change here.
            backdropMode.SelectionChanged += BackdropMode_SelectionChanged;
        }

        private void BackdropMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_appearanceReady) BackdropPreference.MaterialChosen = true;
        }

        private async void SettingsPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // From here on, any Appearance selection is the user's.
            _appearanceReady = true;

            _syncingStartupToggle = true;
            try
            {
                startupToggle.IsOn = await _startup.IsEnabledAsync();
            }
            catch
            {
                startupToggle.IsOn = false;
            }
            finally
            {
                _syncingStartupToggle = false;
            }

#if CONSUMER
            // Consumer update check: fire-and-forget, non-blocking. Full flavor
            // never checks - its updates are distributed manually.
            _ = Vm.CheckForUpdateCommand.ExecuteAsync(null);
#endif

        }

        private void UpdateNow_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
            => _ = Vm.UpdateNowCommand.ExecuteAsync(null);

        private async void StartupToggle_Toggled(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            if (_syncingStartupToggle) return;
            _syncingStartupToggle = true;
            try
            {
                bool ok = await _startup.SetEnabledAsync(startupToggle.IsOn);
                if (!ok)
                {
                    // Revert: request denied (e.g. user dismissed the OS prompt).
                    startupToggle.IsOn = !startupToggle.IsOn;
                }
            }
            catch
            {
                startupToggle.IsOn = !startupToggle.IsOn;
            }
            finally
            {
                _syncingStartupToggle = false;
            }
        }

        private async void AboutCard_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            await AboutDialog.ShowAsync();
        }


    }
}
