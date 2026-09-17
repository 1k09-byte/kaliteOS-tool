using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation.Collections;

namespace kaliteConfig.GpuOverclock.Views
{
    /// <summary>
    /// Overclock section embedded at the bottom of the Graphics page. Exposes
    /// the ViewModel so the host page can re-detect (RefreshAsync) when the
    /// GPU changes, and Teardown on unload.
    /// </summary>
    public sealed partial class OverclockSection : UserControl
    {
        public OverclockViewModel Vm { get; }

        public OverclockSection()
        {
            this.InitializeComponent();
            Vm = new OverclockViewModel();
            this.Loaded += async (_, _) => await Vm.InitializeAsync();
            this.Unloaded += (_, _) => Vm.Teardown();
        }

        // ---------------- x:Bind visibility helpers ----------------

        public static Visibility NotSupportedVis(bool isSupported)
            => isSupported ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility TelemetryVis(bool telemetryAvailable)
            => telemetryAvailable ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility TelemetryErrorVis(string errorText)
            => string.IsNullOrEmpty(errorText) ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility HotspotVis(string hotspotText)
            => string.IsNullOrEmpty(hotspotText) ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility StatusVis(string statusText)
            => string.IsNullOrEmpty(statusText) ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility BoolToVis(bool value)
            => value ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility TextToVis(string text)
            => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility SupportedVis(bool isSupported)
            => isSupported ? Visibility.Visible : Visibility.Collapsed;

        // Risk gate covers the card until accepted (inverse of RiskAccepted).
        public static Visibility NotRiskAcceptedVis(bool riskAccepted)
            => riskAccepted ? Visibility.Collapsed : Visibility.Visible;

        public static Visibility FanStaticVis(GpuFanMode mode)
            => mode == GpuFanMode.Static ? Visibility.Visible : Visibility.Collapsed;

        public static Visibility FanCurveVis(GpuFanMode mode)
            => mode == GpuFanMode.Curve ? Visibility.Visible : Visibility.Collapsed;

        // ---------------- slider commit (debounce on release, not per-tick) ----------------

        // The commit handlers already track user edits in the ViewModel (the
        // commit methods mirror the value into CoreOffsetValue etc., and the
        // x:Bind feeds it back). ValueChanged fires again for that programmatic
        // update — swallowing that echo prevents the revert resync / profile
        // apply slider moves from being re-queued as fresh user writes.
        private void CoreOffsetSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return; // programmatic update, not a user edit
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.CoreOffsetValue != s.Value)
                Vm.OnCoreOffsetCommitted(s.Value);
        }

        private void MemOffsetSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.MemOffsetValue != s.Value)
                Vm.OnMemOffsetCommitted(s.Value);
        }

        private void PowerLimitSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.PowerLimitValue != s.Value)
                Vm.OnPowerLimitCommitted(s.Value);
        }

        private void TempLimitSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (Vm.IsBusy) return;
            if (sender is Slider s && Vm is { IsSupported: true } && Vm.TempLimitValue != s.Value)
                Vm.OnTempLimitCommitted(s.Value);
        }

        private void FanStaticSlider_ValueChanged(object sender, RoutedEventArgs e)
        {
            if (sender is Slider s && Vm.FanStaticPercent != s.Value)
                Vm.FanStaticPercent = (int)s.Value;
        }

        private void StartupReapply_Toggled(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleSwitch ts && Vm.StartupReapplyEnabled != ts.IsOn)
                Vm.ToggleStartupReapplyCommand.Execute(null);
        }

        private void ApplyProfile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is OverclockProfile p)
                _ = Vm.ApplyProfileAsync(p);
        }

        private void DeleteProfile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is OverclockProfile p)
                Vm.DeleteProfileCommand.Execute(p);
        }
    }
}
