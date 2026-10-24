using System;
using kaliteConfig.Services.Navigation;
using kaliteConfig.Services.Personalize;
using kaliteConfig.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace kaliteConfig.Services.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceProvider AddKaliteServices(this IServiceCollection s)
    {
        s.AddSingleton<ProcessTuningService>();
        s.AddSingleton<ThreadTuningService>();
        s.AddSingleton<CpuSetService>();
        s.AddSingleton<GamingModeService>();
        s.AddSingleton<Gaming.GameMode.GameModeSettingsStore>();
        s.AddSingleton(sp => new Gaming.GameMode.GameModeLog(Gaming.GameMode.GameModeSettingsStore.LogPath));
        s.AddSingleton(sp => new Gaming.GameMode.GameModeEngine(
            sp.GetRequiredService<Gaming.GameMode.GameModeSettingsStore>(),
            sp.GetRequiredService<Gaming.GameMode.GameModeLog>()));
        s.AddSingleton<ForegroundSuspendService>(sp =>
            new ForegroundSuspendService(sp.GetRequiredService<ProcessTuningService>(),
                sp.GetRequiredService<Gaming.GameMode.GameModeEngine>()));
        s.AddSingleton<ProfileWatcherService>(sp =>
            new ProfileWatcherService(
                sp.GetRequiredService<ProcessTuningService>(),
                sp.GetRequiredService<CpuSetService>(),
                sp.GetRequiredService<ThreadTuningService>()));
        s.AddSingleton<SnipService>();
        s.AddSingleton<ReservedCpuSetsService>();
        s.AddSingleton<StartupService>();
        s.AddSingleton<InstallerService>();
        s.AddSingleton<IInstallerService>(sp => sp.GetRequiredService<InstallerService>());
        s.AddSingleton<WindhawkInstallerService>();
        s.AddSingleton<INavigationService, NavigationService>();

        s.AddSingleton<LiveWallpaper.IDesktopHostService, LiveWallpaper.DesktopHostService>();
        s.AddSingleton<LiveWallpaper.IWallpaperPowerMonitor, LiveWallpaper.WallpaperPowerMonitor>();
        s.AddSingleton<LiveWallpaper.ILiveLibraryService, LiveWallpaper.LiveLibraryService>();
        s.AddSingleton<LiveWallpaper.ILiveWallpaperEngine, LiveWallpaper.LiveWallpaperEngine>();
        s.AddTransient<LiveWallpaperViewModel>();

        s.AddSingleton<SnapshotStore>();
        s.AddSingleton<SettingApplier>();
        s.AddSingleton<WindowsCapabilityService>();
        s.AddSingleton<ThemeSettingsService>();
        s.AddSingleton<IThemeSettingsService>(sp => sp.GetRequiredService<ThemeSettingsService>());
        s.AddSingleton<WallpaperService>();
        s.AddSingleton<IWallpaperService>(sp => sp.GetRequiredService<WallpaperService>());
        s.AddSingleton<ICursorService>(sp => new CursorService(
            sp.GetRequiredService<SettingApplier>(),
            System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(SnapshotStore.DefaultPath())!, "cursors")));

        s.AddTransient<AffinityViewModel>();
        s.AddTransient<NetworkViewModel>();
        s.AddTransient<InstallerViewModel>();
        s.AddTransient<PersonalizeViewModel>();
        s.AddTransient<PowerPlansViewModel>();
        s.AddTransient<ThreadTunerViewModel>();
        s.AddTransient<SnipViewModel>();

        s.AddSingleton<Input.MouseCatalog>();
        s.AddSingleton<Input.IMouseClassifier, Input.MouseClassifier>();
        s.AddSingleton<Input.IVendorSoftwareDetector, Input.VendorSoftwareDetector>();
        s.AddSingleton<Input.IMouseDiscoveryService, Input.MouseDiscoveryService>();
        s.AddSingleton<Input.IMouseCapabilityProvider, Input.MouseCapabilityProvider>();
        s.AddSingleton<Input.IMouseGuardPolicy, Input.MouseGuardPolicy>();
        s.AddSingleton<Input.IMouseSnapshotService, Input.MouseSnapshotService>();
        s.AddSingleton<Input.IMouseLog, Input.MouseLog>();
        s.AddSingleton<Input.IMouseWriteTransaction, Input.MouseWriteTransaction>();
        s.AddSingleton<Input.IWindowsMouseSettingsService, Input.WindowsMouseSettingsService>();
        s.AddSingleton<Input.IMouseTestService, Input.MouseTestService>();
        s.AddSingleton<Input.IMousePrefStore, Input.MousePrefStore>();

        s.AddTransient<InputViewModel>();

        s.AddSingleton<BiosManagerViewModel>();
        s.AddTransient<GpuDriversViewModel>();
        s.AddTransient<UninstallerViewModel>();
        s.AddTransient<UpdateViewModel>();
        s.AddTransient<EtwManagerViewModel>();
        s.AddTransient<WindhawkProvisioningViewModel>();
        s.AddTransient<ReservedCpuSetsViewModel>();

        return s.BuildServiceProvider();
    }

    public static T Get<T>(this IServiceProvider sp) where T : notnull
        => (T)sp.GetService(typeof(T))!;
}
