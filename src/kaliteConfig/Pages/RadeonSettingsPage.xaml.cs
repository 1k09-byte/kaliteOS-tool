// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using kaliteConfig.GpuOverclock.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Threading.Tasks;

namespace kaliteConfig.Pages;

/// <summary>
/// The Radeon half of the Graphics page: the AMD driver's own 3D settings,
/// read and written through the same ADLX system the Overclock tab uses.
///
/// Every control here writes to the driver immediately and then re-reads, so
/// what is on screen is the driver's answer and not the click. That is why the
/// toggle and parameter handlers do nothing but hand the request to the
/// ViewModel - the read-back has to come back from ADLX, not from the control's
/// own IsOn.
///
/// The page is cached (NavigationCacheMode="Required"), which is what keeps the
/// ADLX sessions alive for as long as the tab exists: there is one session per
/// AMD adapter, opened once and replaced on refresh. That matches how the
/// tuning controller holds its sessions, and the app never calls
/// ADLXTerminate, so there is no shutdown ordering to get wrong.
/// </summary>
public sealed partial class RadeonSettingsPage : Page
{
    /// <summary>
    /// Non-zero while a write is in flight. ToggleSwitch.Toggled fires for ANY
    /// IsOn change, including the one the binding makes when the read-back
    /// re-asserts the driver's value, so without this the page would treat its
    /// own correction as a second user click. Nested calls increment it rather
    /// than resetting it, so a correction inside a write cannot be re-entered.
    /// </summary>
    private int _writing;

    public RadeonSettingsPage()
    {
        InitializeComponent();

        Loaded += async (s, e) =>
        {
            Vm.ExternalRefreshRequested -= OnExternalRefreshRequested;
            Vm.ExternalRefreshRequested += OnExternalRefreshRequested;

            // After first paint, not in the constructor: the vendor lookup can
            // fall back to WMI, which is far too slow to run inside
            // InitializeComponent.
            Vm.InitialiseRendererCard();
            await Vm.EnsureLoadedAsync();
        };

        Unloaded += (s, e) => Vm.ExternalRefreshRequested -= OnExternalRefreshRequested;
    }

    /// <summary>
    /// The ADLX change listener fires on a driver thread. Touching a control
    /// from there is a hard crash, so the refresh is queued onto this page's
    /// dispatcher.
    /// </summary>
    private void OnExternalRefreshRequested()
        => DispatcherQueue.TryEnqueue(() => _ = Vm.RefreshStateAsync());

    private void FeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_writing > 0) return;
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not Radeon3DSettingItem item) return;

        _writing++;
        _ = ApplyToggleAsync(toggle, item);
    }

    private async Task ApplyToggleAsync(ToggleSwitch toggle, Radeon3DSettingItem item)
    {
        try
        {
            await Vm.ApplyToggleAsync(item, toggle.IsOn);
        }
        finally
        {
            _writing--;
        }
    }

    private void Parameter_Committed(object sender, RoutedEventArgs e)
    {
        if (sender is NumberBox box && box.DataContext is Radeon3DSettingItem item)
            _ = Vm.ApplyParameterAsync(item);
    }

    /// <summary>Enter commits, so the value does not wait for the click elsewhere.</summary>
    private void Parameter_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        if (sender is NumberBox box && box.DataContext is Radeon3DSettingItem item)
            _ = Vm.ApplyParameterAsync(item);
    }
}
