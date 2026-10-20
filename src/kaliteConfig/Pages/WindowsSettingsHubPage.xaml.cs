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

            this.ReservedCpuSetsFrame.Navigate(typeof(ReservedCpuSetsPage));
        }

        private void LoadKernelToggles()
        {
            _loadingKernelToggles = true;
            try
            {
                LoadOneKernelToggle("TimerExpiration", TimerExpToggle, TimerExpCard,
                    "Serializes timer expiration (active scheme).");
                LoadOneKernelToggle("MmcssStatus", MmcssToggle, MmcssCard,
                    "Boosts multimedia thread priorities (needs restart). OFF stops the MMCSS service and sets System Responsiveness to 100; ON sets it to 20.");
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
                card.Description = det.Describe(caption);
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
                LoadKernelToggles();
            }
            catch (Exception ex)
            {
                LoadKernelToggles();
                var card = id switch
                {
                    "TimerExpiration" => TimerExpCard,
                    "MmcssStatus" => MmcssCard,
                    _ => null,
                };
                if (card != null) card.Description += $" Write denied: {ex.Message}";
            }
        }

        private void KernelToggle_RightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
            var def = Services.WindowsSettingsService.Find(id);
            if (def == null || !_kernel.HasBackup(def)) return;

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
            if (SvcSplitBox.SelectedIndex >= Services.WindowsSettingsService.SvcSplitPresets.Length) return;
            var (value, _) = Services.WindowsSettingsService.SvcSplitPresets[SvcSplitBox.SelectedIndex];
            try
            {
                if (value == 380000u)
                {
                    _kernel.ResetSvcSplit();
                }
                else
                {
                    _kernel.WriteSvcSplit(value);
                }
                LoadSvcSplit();
            }
            catch (Exception ex)
            {
                LoadSvcSplit();
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
