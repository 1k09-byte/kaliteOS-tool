using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.GpuOverclock.Models;
using kaliteConfig.GpuOverclock.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.GpuOverclock.ViewModels
{
    public sealed partial class AmdGpuCardItem : ObservableObject
    {
        public string Name { get; set; } = string.Empty;
        public string PnpString { get; set; } = string.Empty;
        public string DeviceIdText { get; set; } = string.Empty;
        public bool IsDiscrete { get; set; }
        public string VramText { get; set; } = string.Empty;
        public bool ManualGfx { get; set; }
        public bool ManualVram { get; set; }
        public bool ManualFan { get; set; }
        public bool ManualPower { get; set; }
        public bool AnyControlSupported { get; set; }

        public string KindText => IsDiscrete ? "Discrete" : "Integrated";

        public string DeviceKindText => $"{KindText} Â· {VramText}";

        public string SubtitleText => $"{DeviceKindText}  Â·  {DeviceIdText}";

        public string CapabilityText
        {
            get
            {
                var domains = new System.Collections.Generic.List<string>();
                if (ManualGfx) domains.Add("core/memory clock");
                if (ManualVram) domains.Add("VRAM timing");
                if (ManualFan) domains.Add("fan");
                if (ManualPower) domains.Add("power limit");
                return domains.Count == 0
                    ? "No tuning controls reported by the driver."
                    : "Tunable: " + string.Join(", ", domains) + ".";
            }
        }
    }

    public sealed partial class AmdOverclockViewModel : ObservableObject
    {
        public ObservableCollection<AmdGpuCardItem> Cards { get; } = new();

        [ObservableProperty] public partial bool IsLoading { get; set; }
        [ObservableProperty] public partial string StatusText { get; set; } = "Detecting AMD GPUsâ€¦";
        [ObservableProperty] public partial bool AdlxAvailable { get; set; }
        [ObservableProperty] public partial string DriverVersionText { get; set; } = string.Empty;
        [ObservableProperty] public partial bool HasCards { get; set; }
        [ObservableProperty] public partial bool HasTunableCard { get; set; }
        [ObservableProperty] public partial bool ShouldShow { get; set; }
        [ObservableProperty] public partial string? ErrorMessage { get; set; }

        public string EmptyTitle => AdlxAvailable ? "No AMD adapter reported" : "AMD overclocking is unavailable";

        public string EmptyDetail => AdlxAvailable
            ? "ADLX initialized, but the installed AMD driver did not report any adapters. If this machine has " +
              "an AMD card, install its display driver from the Drivers tab."
            : ErrorMessage ??
              "The AMD display driver did not provide ADLX (amdadlx64.dll). Install or repair the driver, then refresh.";

        partial void OnAdlxAvailableChanged(bool value) => OnPropertyChanged(nameof(EmptyTitle));

        partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(EmptyDetail));

        [ObservableProperty] public partial string? InfoNote { get; set; }

        [ObservableProperty] public partial string? WriteNote { get; set; }

        private bool _loaded;

        public async Task EnsureLoadedAsync()
        {
            if (_loaded) return;
            _loaded = true;
            await LoadAsync();
        }

        [RelayCommand]
        private Task RefreshAsync() => LoadAsync();

        private async Task LoadAsync()
        {
            try
            {
                IsLoading = true;
                ErrorMessage = null;
                StatusText = "Detecting AMD GPUsâ€¦";

                ShouldShow = AmdGpuSimulation.IsActive
                    || AmdAdlxInterop.IsAvailable
                    || GpuVendorPresenceService.Detect().HasAmd
                    || GpuOverclockModule.Instance.IsAmdController;

                if (AmdGpuSimulation.IsActive)
                {
                    AdlxAvailable = true;
                    DriverVersionText = "2.0 (simulated)";
                    StatusText = "Simulation mode - no real adapter is being queried";
                    InfoNote = AmdGpuSimulation.Label +
                        ". Every value here is fabricated so the page can be reviewed without the hardware.";
                    WriteNote = null;
                    Cards.Clear();
                    Cards.Add(ToCardItem(AmdGpuSimulation.Support));
                    HasCards = true;
                    HasTunableCard = AmdGpuSimulation.Support.AnyControlSupported;
                    return;
                }

                if (!AmdAdlxInterop.IsAvailable)
                {
                    Cards.Clear();
                    HasCards = false;
                    HasTunableCard = false;
                    InfoNote = null;
                    WriteNote = null;
                    AdlxAvailable = false;
                    DriverVersionText = string.Empty;
                    StatusText = "ADLX unavailable";
                    ErrorMessage = AmdAdlxInterop.LastInitializationError ??
                        "The AMD display driver did not provide ADLX (amdadlx64.dll). " +
                        "Install or repair the AMD display driver, then refresh.";
                    return;
                }

                AdlxAvailable = true;

                var windowsDriverVersion = await System.Threading.Tasks.Task.Run(
                    () => Services.DisplayDriverVersion.AmdDisplayDriver);
                DriverVersionText = windowsDriverVersion
                    ?? AmdAdlxInterop.DriverVersion ?? "unknown";

                var gpus = AmdAdlxInterop.EnumerateGpusWithSupport();
                Cards.Clear();
                foreach (var gpu in gpus)
                {
                    Cards.Add(ToCardItem(gpu));
                }

                HasCards = Cards.Count > 0;
                HasTunableCard = Cards.Any(c => c.AnyControlSupported);
                StatusText = HasTunableCard
                    ? $"{Cards.Count(c => c.AnyControlSupported)} of {Cards.Count} adapter(s) tunable"
                    : HasCards
                        ? $"{Cards.Count} adapter(s), none tunable"
                        : "No AMD adapters reported";

                WriteNote = HasTunableCard
                    ? "This adapter is tunable. The Overclocking panel above drives the same ADLX " +
                      "session: core and memory offsets, power limit, fan speed / curve, Zero RPM " +
                      "and profiles all apply live."
                    : null;

                InfoNote = !HasCards
                    ? "ADLX initialized, but the driver reported no AMD adapters."
                    : HasTunableCard
                        ? null
                        : "The installed AMD driver reports no manual tuning controls for this adapter. " +
                          "Integrated GPUs normally share system memory and expose none; a discrete Radeon " +
                          "card is required for overclocking.";
            }
            catch (Exception ex)
            {
                ErrorMessage = "AMD detection failed: " + ex.Message;
                StatusText = "Detection failed";
            }            finally
            {
                IsLoading = false;
            }
        }

        private static AmdGpuCardItem ToCardItem(AmdGpuTuningSupport gpu) => new()
        {
            Name = string.IsNullOrWhiteSpace(gpu.Name) ? "AMD GPU" : gpu.Name,
            PnpString = gpu.PnpString ?? string.Empty,
            DeviceIdText = DescribeDeviceId(gpu.PnpString),
            IsDiscrete = gpu.IsDiscrete,
            VramText = gpu.VramMb > 0 ? $"{gpu.VramMb} MB VRAM" : "shared memory",
            ManualGfx = gpu.ManualGfx,
            ManualVram = gpu.ManualVram,
            ManualFan = gpu.ManualFan,
            ManualPower = gpu.ManualPower,
            AnyControlSupported = gpu.AnyControlSupported,
        };

        internal static string DescribeDeviceId(string? pnpString)
        {
            if (string.IsNullOrEmpty(pnpString)) return "device id unknown";
            foreach (string part in pnpString.Split('&'))
            {
                if (part.StartsWith("DEV_", StringComparison.OrdinalIgnoreCase))
                {
                    string id = part.Substring(4);
                    return string.IsNullOrEmpty(id) ? "device id unknown" : "device 0x" + id.ToUpperInvariant();
                }
            }
            return "device id unknown";
        }
    }
}
