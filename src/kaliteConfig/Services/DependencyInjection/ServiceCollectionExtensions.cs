using System;
using kaliteConfig.Services.Navigation;
using kaliteConfig.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace kaliteConfig.Services.DependencyInjection;

/// <summary>Single composition root. App builds this once; pages/VMs resolve from it.</summary>
public static class ServiceCollectionExtensions
{
    public static IServiceProvider AddKaliteServices(this IServiceCollection s)
    {
        // Core singletons (were `new` in App ctor — same lifetime, now resolvable/testable).
        s.AddSingleton<ProcessTuningService>();
        s.AddSingleton<ThreadTuningService>();
        s.AddSingleton<CpuSetService>();
        s.AddSingleton<GamingModeService>();
        s.AddSingleton<ForegroundSuspendService>(sp =>
            new ForegroundSuspendService(sp.GetRequiredService<ProcessTuningService>()));
        s.AddSingleton<ProfileWatcherService>(sp =>
            new ProfileWatcherService(
                sp.GetRequiredService<ProcessTuningService>(),
                sp.GetRequiredService<CpuSetService>(),
                sp.GetRequiredService<ThreadTuningService>()));
        s.AddSingleton<SnipService>();
        s.AddSingleton<ReservedCpuSetsService>();
        s.AddSingleton<StartupService>();
        // One InstallerService instance, exposed through its interface too.
        s.AddSingleton<InstallerService>();
        s.AddSingleton<IInstallerService>(sp => sp.GetRequiredService<InstallerService>());
        s.AddSingleton<WindhawkInstallerService>();
        s.AddSingleton<INavigationService, NavigationService>();

        // ViewModels: transient — fresh state per navigation.
        s.AddTransient<BenchmarkViewModel>();
        s.AddTransient<AffinityViewModel>();
        s.AddTransient<NetworkViewModel>();
        s.AddTransient<InstallerViewModel>();
        s.AddTransient<PowerPlansViewModel>();
        s.AddTransient<ThreadTunerViewModel>();
        s.AddTransient<SnipViewModel>();
        s.AddTransient<BiosManagerViewModel>();
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
