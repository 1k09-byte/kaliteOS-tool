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
    /// <summary>
    /// One AMD adapter, as the AMD page shows it: identity on the left, the
    /// tuning domains the driver says it exposes on the right.
    /// </summary>
    public sealed partial class AmdGpuCardItem : ObservableObject
    {
        // Plain settable properties (not init/required): the XAML compiler emits
        // strongly-typed assigners for every member bound in a DataTemplate, and
        // init-only members fail to compile in that generated file.
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

        /// <summary>"Discrete" / "Integrated" - ADLX has no other classification.</summary>
        public string KindText => IsDiscrete ? "Discrete" : "Integrated";

        public string DeviceKindText => $"{KindText} · {VramText}";

        /// <summary>
        /// The second line, pre-joined. Deliberately NOT three &lt;Run&gt; elements
        /// with bindings on them: Run.Text is not a bindable property, and a
        /// binding on it throws as soon as the template is instantiated - which
        /// is exactly the first time a detected card is shown.
        /// </summary>
        public string SubtitleText => $"{DeviceKindText}  ·  {DeviceIdText}";

        /// <summary>One line naming the domains the driver will actually accept.</summary>
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

    /// <summary>
    /// Backing model for the AMD section of the Overclock page. Detection is
    /// real: it goes through ADLX (see <see cref="AmdAdlxInterop"/>) and asks the
    /// installed AMD driver what each adapter supports. Nothing here is inferred
    /// from card names.
    /// </summary>
    public sealed partial class AmdOverclockViewModel : ObservableObject
    {
        public ObservableCollection<AmdGpuCardItem> Cards { get; } = new();

        [ObservableProperty] public partial bool IsLoading { get; set; }
        [ObservableProperty] public partial string StatusText { get; set; } = "Detecting AMD GPUs…";
        [ObservableProperty] public partial bool AdlxAvailable { get; set; }
        [ObservableProperty] public partial string DriverVersionText { get; set; } = string.Empty;
        [ObservableProperty] public partial bool HasCards { get; set; }
        [ObservableProperty] public partial bool HasTunableCard { get; set; }
        [ObservableProperty] public partial string? ErrorMessage { get; set; }

        /// <summary>
        /// Explains an empty or dead end. Kept as one nullable string (rather
        /// than several booleans the page would have to AND together) so the XAML
        /// stays a plain NullToVisibility binding.
        /// </summary>
        [ObservableProperty] public partial string? InfoNote { get; set; }

        /// <summary>Set when a tunable card exists but this build writes nothing yet.</summary>
        [ObservableProperty] public partial string? WriteNote { get; set; }

        private bool _loaded;

        /// <summary>Runs the ADLX probe once per page instance.</summary>
        public async Task EnsureLoadedAsync()
        {
            if (_loaded) return;
            _loaded = true;
            await LoadAsync();
        }

        [RelayCommand]
        private Task RefreshAsync() => LoadAsync();

        private Task LoadAsync()
        {
            try
            {
                IsLoading = true;
                ErrorMessage = null;
                StatusText = "Detecting AMD GPUs…";

                // Simulation replaces the whole section, not just adds to it: a
                // real 780M listed next to a fabricated 7900 XTX would be two
                // answers to one question.
                if (AmdGpuSimulation.IsActive)
                {
                    AdlxAvailable = true;
                    DriverVersionText = "1.4 (simulated)";
                    StatusText = "Simulation mode - no real adapter is being queried";
                    InfoNote = AmdGpuSimulation.Label +
                        ". Every value here is fabricated so the page can be reviewed without the hardware.";
                    WriteNote = null;
                    Cards.Clear();
                    Cards.Add(ToCardItem(AmdGpuSimulation.Support));
                    HasCards = true;
                    HasTunableCard = AmdGpuSimulation.Support.AnyControlSupported;
                    return Task.CompletedTask;
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
                    StatusText = "No AMD device library";
                    ErrorMessage =
                        "The AMD display driver did not provide ADLX (amdadlx64.dll). " +
                        "Install or repair the AMD display driver, then refresh.";
                    return Task.CompletedTask;
                }

                AdlxAvailable = true;
                DriverVersionText = AmdAdlxInterop.DriverVersion ?? "unknown";

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
                    ? "Detection is live for this card. Applying values is not enabled in this build yet - " +
                      "the read side is wired up, the writes are held back until they can be validated on a " +
                      "discrete Radeon card."
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
            }
            finally
            {
                IsLoading = false;
            }

            return Task.CompletedTask;
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

        /// <summary>Pulls the human part out of a PNP id, e.g. "13C0" from VEN_1002&amp;DEV_13C0.</summary>
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
