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
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Pages
{
    public sealed partial class WindowsSettingsHubPage : Page
    {
        private readonly Services.WindowsSettingsService _kernel = new();
        private bool _loadingKernelToggles;
        private bool _loadingSvcSplit;

        public WindowsSettingsHubPage()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) => { LoadKernelToggles(); LoadSvcSplit(); };
            
            // Host the Reserved CPU Sets panel here.
            this.ReservedCpuSetsFrame.Navigate(typeof(ReservedCpuSetsPage));
        }


        private void LoadKernelToggles()
        {
            _loadingKernelToggles = true;
            try
            {
                LoadOneKernelToggle("ThreadedDpc", ThreadedDpcToggle, ThreadedDpcCard,
                    "Runs DPCs on dedicated threads (needs restart).");
                LoadOneKernelToggle("InterruptRouting", IrqRoutingToggle, IrqRoutingCard,
                    "Stops Windows steering device interrupts across cores (active scheme).");
                LoadOneKernelToggle("TimerExpiration", TimerExpToggle, TimerExpCard,
                    "Serializes timer expiration (active scheme).");
                LoadOneKernelToggle("MmcssStatus", MmcssToggle, MmcssCard,
                    "Boosts multimedia thread priorities (needs restart).");
            }
            finally { _loadingKernelToggles = false; }
        }

        private void LoadOneKernelToggle(string id, ToggleSwitch toggle, CommunityToolkit.WinUI.Controls.SettingsCard card, string caption)
        {
            var def = Services.WindowsSettingsService.Find(id);
            if (def == null) return;
            try
            {
                var det = _kernel.Detect(def);
                toggle.IsOn = det.State == Services.WindowsSettingsService.TweakState.On;
                card.Description = det.Describe(caption)
                    + (det.State != Services.WindowsSettingsService.TweakState.WindowsDefault && _kernel.HasBackup(def)
                        ? " Right-click the toggle to restore the original value."
                        : "");
            }
            catch (Exception ex)
            {
                card.Description = caption + $" Unreadable: {ex.Message}";
            }
        }

        private void KernelToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loadingKernelToggles || sender is not ToggleSwitch toggle || toggle.Tag is not string id) return;
            var def = Services.WindowsSettingsService.Find(id);
            if (def == null) return;
            try
            {
                _kernel.Write(def, toggle.IsOn);
                LoadKernelToggles(); // re-read to confirm the write landed
            }
            catch (Exception ex)
            {
                LoadKernelToggles(); // snap back
                var card = id switch
                {
                    "InterruptRouting" => IrqRoutingCard,
                    "TimerExpiration" => TimerExpCard,
                    "ThreadedDpc" => ThreadedDpcCard,
                    "MmcssStatus" => MmcssCard,
                    _ => null,
                };
                if (card != null) card.Description += $" Write denied: {ex.Message}";
            }
        }

        /// <summary>Right-click context menu on a kernel toggle: restore the
        /// value that was present before the app first wrote it (exactly -
        /// including removing it again if Windows didn't have it set).</summary>
        private void KernelToggle_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
            var def = Services.WindowsSettingsService.Find(id);
            if (def == null || !_kernel.HasBackup(def)) return; // never written by the app

            var menu = new MenuFlyout();
            var restore = new MenuFlyoutItem { Text = "Restore original value" };
            restore.Click += (_, _) =>
            {
                try
                {
                    _kernel.ResetToOriginal(def);
                    LoadKernelToggles();
                }
                catch { }
            };
            menu.Items.Add(restore);
            menu.ShowAt(fe, e.GetPosition(fe));
        }

        private void LoadSvcSplit()
        {
            _loadingSvcSplit = true;
            try
            {
                SvcSplitBox.Items.Clear();
                uint? current = null;
                try { current = _kernel.ReadSvcSplit(); }
                catch (Exception ex) { SvcSplitCard.Description = $"Unreadable: {ex.Message}"; return; }

                int selected = -1;
                for (int i = 0; i < Services.WindowsSettingsService.SvcSplitPresets.Length; i++)
                {
                    var (value, label) = Services.WindowsSettingsService.SvcSplitPresets[i];
                    SvcSplitBox.Items.Add(label);
                    if (current == value) selected = i;
                }
                if (current == null)
                {
                    // Absent value = Windows default; select the (default) row.
                    selected = 0;
                    SvcSplitCard.Description = "Not set - Windows default (380000 KB). Takes effect after a restart.";
                }
                else if (selected < 0)
                {
                    string custom = $"Custom: {current} KB";
                    SvcSplitBox.Items.Add(custom);
                    selected = SvcSplitBox.Items.Count - 1;
                    SvcSplitCard.Description = $"Detected: {current} KB. Takes effect after a restart.";
                }
                else
                {
                    SvcSplitCard.Description = $"Detected: {current} KB. Takes effect after a restart.";
                }
                SvcSplitBox.SelectedIndex = selected;
            }
            finally { _loadingSvcSplit = false; }
        }

        private void SvcSplitBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSvcSplit || SvcSplitBox.SelectedIndex < 0) return;
            if (SvcSplitBox.SelectedIndex >= Services.WindowsSettingsService.SvcSplitPresets.Length) return; // custom row
            var (value, _) = Services.WindowsSettingsService.SvcSplitPresets[SvcSplitBox.SelectedIndex];
            try
            {
                if (value == 380000u)
                {
                    // The (default) row deletes the value rather than writing
                    // 380000, so Windows falls back to its built-in behavior
                    // even if the default constant ever changes.
                    _kernel.ResetSvcSplit();
                }
                else
                {
                    _kernel.WriteSvcSplit(value);
                }
                LoadSvcSplit(); // confirm the write landed
            }
            catch (Exception ex)
            {
                LoadSvcSplit(); // snap back
                SvcSplitCard.Description += $" Write denied: {ex.Message}";
            }
        }

        private void SvcSplitReset_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _kernel.ResetSvcSplit();
            }
            catch { }
            LoadSvcSplit();
        }
    }
}
