using System;
using Microsoft.UI.Xaml.Controls;

namespace kaliteConfig.Services.Navigation;

/// <summary>Page keys — replaces the string-Tag switch in MainWindow.</summary>
public enum PageKey
{
    Apps, Graphics, Gaming, PriorityBoosts, ThreadTuner, Snip,
    Network, Benchmark, WindowsSettings, BiosManager, PowerPlans, Settings
}

public interface INavigationService
{
    void Attach(Frame frame);
    bool Navigate(PageKey key);
    PageKey? Current { get; }
}

public sealed class NavigationService : INavigationService
{
    private Frame? _frame;
    public PageKey? Current { get; private set; }

    private static Type TypeFor(PageKey key) => key switch
    {
        PageKey.Apps => typeof(Pages.InstallerPage),
        PageKey.Graphics => typeof(Pages.GraphicsHubPage),
        PageKey.Gaming => typeof(Pages.AffinityPage),
        PageKey.PriorityBoosts => typeof(Pages.PriorityBoostsPage),
        PageKey.ThreadTuner => typeof(Pages.ThreadTunerPage),
        PageKey.Snip => typeof(Pages.SnipPage),
        PageKey.Network => typeof(Pages.NetworkPage),
        PageKey.Benchmark => typeof(Pages.BenchmarkPage),
        PageKey.WindowsSettings => typeof(Pages.WindowsSettingsHubPage),
        PageKey.BiosManager => typeof(Pages.BiosManagerPage),
        PageKey.PowerPlans => typeof(Pages.PowerPlansPage),
        _ => typeof(Pages.SettingsPage),
    };

    public static PageKey KeyForTag(string tag) => tag switch
    {
        "AppsPage" => PageKey.Apps,
        "GraphicsPage" => PageKey.Graphics,
        "GamingPage" => PageKey.Gaming,
        "PriorityBoostsPage" => PageKey.PriorityBoosts,
        "ThreadTunerPage" => PageKey.ThreadTuner,
        "SnipPage" => PageKey.Snip,
        "NetworkPage" => PageKey.Network,
        "BenchmarkPage" => PageKey.Benchmark,
        "WindowsSettingsPage" => PageKey.WindowsSettings,
        "BiosManagerPage" => PageKey.BiosManager,
        "PowerPlansPage" => PageKey.PowerPlans,
        _ => PageKey.Settings,
    };

    public void Attach(Frame frame) => _frame = frame;

    public bool Navigate(PageKey key)
    {
        if (_frame is null) return false;
        Current = key;
        return _frame.Navigate(TypeFor(key));
    }
}
