using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Pages
{
    public sealed partial class WindowsSettingsHubPage : Page
    {
        private readonly Services.KernelTuningService _kernel = new();
        private readonly ViewModels.WindhawkProvisioningViewModel Vm = new();
        private bool _loadingWin32PS;
        private bool _loadingKernelToggles;
        private bool _loadingSvcSplit;

        public WindowsSettingsHubPage()
        {
            this.InitializeComponent();
            this.Loaded += (_, _) => { LoadWin32PS(); LoadKernelToggles(); LoadSvcSplit(); Vm.RefreshDetection(); };
        }

        /// <summary>
        /// Real Windhawk provisioning: confirm → upgrade/install the pinned
        /// 2.0 alpha when needed → import the bundled KaliteOS settings via
        /// windhawk-cli → update mods. Progress surfaces in the page's InfoBar
        /// through the shared ViewModel.
        /// </summary>
        private async void WindhawkInstall_Click(object sender, RoutedEventArgs e)
        {
            if (!Vm.CanRun) return;

            var confirm = new ContentDialog
            {
                Title = "Install Windhawk & import settings",
                Content = Vm.IsInstalled
                    ? "Windhawk will be upgraded to the 2.0 alpha (silent install) and the bundled KaliteOS settings imported. Windhawk restarts during import. Continue?"
                    : "Windhawk 2.0 (alpha 5) will be installed silently and the bundled KaliteOS settings imported. Continue?",
                PrimaryButtonText = "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = this.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            await Vm.RunProvisioningAsync();
        }

        private void PowerPlans_Click(object sender, RoutedEventArgs e)
            => Frame.Navigate(typeof(PowerPlansPage));

        private void Uninstaller_Click(object sender, RoutedEventArgs e)
            => Frame.Navigate(typeof(UninstallerPage));

        private void LoadWin32PS()
        {
            _loadingWin32PS = true;
            try
            {
                Win32PSBox.Items.Clear();
                uint? current = null;
                try { current = _kernel.ReadWin32PS(); }
                catch (Exception ex) { Win32PSCard.Description = $"Unreadable: {ex.Message}"; }
                int selected = -1;
                for (int i = 0; i < Services.KernelTuningService.Win32PSPresets.Length; i++)
                {
                    var (value, label) = Services.KernelTuningService.Win32PSPresets[i];
                    Win32PSBox.Items.Add(label);
                    if (current == value) selected = i;
                }
                if (current == null)
                {
                    Win32PSCard.Description = "Not set — Windows default behavior. Takes effect after a restart.";
                }
                else if (selected < 0)
                {
                    string custom = $"Custom value: {current} (0x{current:X})";
                    Win32PSBox.Items.Add(custom);
                    selected = Win32PSBox.Items.Count - 1;
                    Win32PSCard.Description = $"Detected: {current} (0x{current:X}). Takes effect after a restart.";
                }
                else
                {
                    Win32PSCard.Description = $"Detected: {current} (0x{current:X}). Takes effect after a restart.";
                }
                Win32PSBox.SelectedIndex = selected;
            }
            finally { _loadingWin32PS = false; }
        }

        private void Win32PSBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingWin32PS || Win32PSBox.SelectedIndex < 0) return;
            if (Win32PSBox.SelectedIndex >= Services.KernelTuningService.Win32PSPresets.Length) return; // custom row
            var (value, label) = Services.KernelTuningService.Win32PSPresets[Win32PSBox.SelectedIndex];
            try
            {
                _kernel.WriteWin32PS(value);
                Win32PSCard.Description = $"Detected: {value} (0x{value:X}) — applied, takes effect after a restart.";
            }
            catch (Exception ex)
            {
                Win32PSCard.Description = $"Write denied: {ex.Message}";
                LoadWin32PS(); // snap back to the real value
            }
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
            }
            finally { _loadingKernelToggles = false; }
        }

        private void LoadOneKernelToggle(string id, ToggleSwitch toggle, CommunityToolkit.WinUI.Controls.SettingsCard card, string caption)
        {
            var def = Services.KernelTuningService.Find(id);
            if (def == null) return;
            try
            {
                var det = _kernel.Detect(def);
                toggle.IsOn = det.State == Services.KernelTuningService.TweakState.On;
                card.Description = det.Describe(caption)
                    + (det.State != Services.KernelTuningService.TweakState.WindowsDefault && _kernel.HasBackup(def)
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
            var def = Services.KernelTuningService.Find(id);
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
                    _ => null,
                };
                if (card != null) card.Description += $" Write denied: {ex.Message}";
            }
        }

        /// <summary>Right-click context menu on a kernel toggle: restore the
        /// value that was present before the app first wrote it (exactly —
        /// including removing it again if Windows didn't have it set).</summary>
        private void KernelToggle_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
            var def = Services.KernelTuningService.Find(id);
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
                for (int i = 0; i < Services.KernelTuningService.SvcSplitPresets.Length; i++)
                {
                    var (value, label) = Services.KernelTuningService.SvcSplitPresets[i];
                    SvcSplitBox.Items.Add(label);
                    if (current == value) selected = i;
                }
                if (current == null)
                {
                    // Absent value = Windows default; select the (default) row.
                    selected = 0;
                    SvcSplitCard.Description = "Not set — Windows default (380000 KB). Takes effect after a restart.";
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
            if (SvcSplitBox.SelectedIndex >= Services.KernelTuningService.SvcSplitPresets.Length) return; // custom row
            var (value, _) = Services.KernelTuningService.SvcSplitPresets[SvcSplitBox.SelectedIndex];
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
