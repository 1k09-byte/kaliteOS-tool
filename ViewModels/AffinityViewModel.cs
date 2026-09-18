using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels
{
    /// <summary>
    /// Full tuning-pass ViewModel: groups devices by category, provides
    /// Optimize / Undo / Redo / Restore / View Changes commands, and
    /// tracks every registry write in an undo stack.
    /// </summary>
    public partial class AffinityViewModel : ObservableObject
    {
        private readonly AffinityService _affinityService = new();

        public ObservableCollection<AffinityDeviceItem> GraphicsDevices { get; } = new();
        public ObservableCollection<AffinityDeviceItem> NetworkDevices { get; } = new();
        public ObservableCollection<AffinityDeviceItem> UsbDevices { get; } = new();
        public ObservableCollection<AffinityDeviceItem> AudioDevices { get; } = new();

        [ObservableProperty]
        public partial AffinityDeviceItem? SelectedDevice { get; set; }

        [ObservableProperty]
        public partial bool IsRefreshing { get; set; }

        // Group expanders for the table layout.
        [ObservableProperty]
        public partial bool IsGraphicsExpanded { get; set; } = true;

        [ObservableProperty]
        public partial bool IsNetworkExpanded { get; set; } = true;

        [ObservableProperty]
        public partial bool IsUsbExpanded { get; set; } = true;

        [ObservableProperty]
        public partial bool IsAudioExpanded { get; set; } = true;

        // ── Change tracking ─────────────────────────────────────────────────

        private readonly List<AffinityChange> _undoStack = new();
        private readonly List<AffinityChange> _redoStack = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChangeCountText))]
        public partial int ChangeCount { get; set; }

        [ObservableProperty]
        public partial bool CanUndo { get; set; }

        [ObservableProperty]
        public partial bool CanRedo { get; set; }

        public string ChangeCountText => $"View Changes ({ChangeCount})";

        private void RefreshChangeState()
        {
            ChangeCount = _undoStack.Count;
            CanUndo = _undoStack.Count > 0;
            CanRedo = _redoStack.Count > 0;
            
            // TrackedChanges for the "View Changes" dialog
            TrackedChanges.Clear();
            foreach (var c in _undoStack)
                TrackedChanges.Add(c);
        }

        private void PushChange(AffinityChange change)
        {
            _undoStack.Add(change);
            _redoStack.Clear();   // new action invalidates any stale redo
            RefreshChangeState();
        }

        // ── Dialog state ────────────────────────────────────────────────────

        /// <summary>
        /// Fills a device's dialog state from live read-only interrupt data and
        /// builds the processor-mask grid (SMT pairs), pre-checking the threads
        /// in the current assignment mask.
        /// </summary>
        public Task LoadDeviceDetailsAsync(AffinityDeviceItem item)
        {
            SelectedDevice = item;
            var info = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
            item.MsiEnabled = info.MsiSupported ?? false;
            item.MsiLimit = info.MsiLimit is > 0 ? info.MsiLimit.Value : (info.MaxMsiLimit is > 0 ? info.MaxMsiLimit.Value : 1);
            item.MsiLimitText = info.MsiLimit?.ToString() ?? (info.MaxMsiLimit?.ToString() ?? "—");
            item.DevicePolicyShort = AffinityService.DevicePolicyShort(info.DevicePolicy);
            item.DevicePriorityShort = info.DevicePriority is null
                ? "—"
                : AffinityService.DevicePriorityName(info.DevicePriority);
            item.SelectedPriority = AffinityService.DevicePriorityName(info.DevicePriority);
            item.SelectedPolicy = info.DevicePolicy is null
                ? "IrqPolicyMachineDefault"
                : AffinityService.DevicePolicyName(info.DevicePolicy);

            var topology = TopologyService.Get();
            int logical = topology.TotalLogicalCores;
            item.CoreGroups.Clear();

            // No explicit override in the registry means the device runs on
            // every logical processor — pre-check everything so the dialog
            // shows the EFFECTIVE affinity instead of "0 of N selected".
            ulong fullMask = logical >= 64 ? ulong.MaxValue : ((1UL << logical) - 1);
            ulong effectiveMask = info.AffinityMask ?? fullMask;

            int coreIndex = 0;

            void MapCores(IEnumerable<ulong> masks, string label)
            {
                // Note: masks includes core 0 if we're rendering the UI (Wait, TopologyService Detection removes reserved core 0!
                // To avoid breaking the UI indices if we omitted Core 0, I should just render all cores. Wait! TopologyService.Detect removes Core 0 from PerformanceCoreMasks!
                // So Core 0's threads won't be rendered. Let's fix that by re-adding Core 0 or handling it gracefully:
                foreach (ulong mask in masks)
                {
                    var group = new ProcessorCoreGroup { CoreIndex = coreIndex, Title = $"Core {coreIndex} ({label})" };
                    for (int t = 0; t < logical; t++)
                    {
                        if ((mask & (1UL << t)) != 0)
                        {
                            bool on = (effectiveMask & (1UL << t)) != 0;
                            var row = new ProcessorThreadItem { Index = t, IsChecked = on };
                            row.PropertyChanged += (_, e) =>
                            {
                                if (e.PropertyName == nameof(ProcessorThreadItem.IsChecked))
                                    RefreshThreadCount(item);
                            };
                            group.Threads.Add(row);
                        }
                    }
                    if (group.Threads.Count > 0)
                        item.CoreGroups.Add(group);
                    coreIndex++;
                }
            }

            // We must resurrect Core 0 for UI visual purposes if it was removed
            // Core 0 thread mask is usually (1 << 0) and possibly (1 << 1) if SMT. Let's just create a synthetic mask for missing core 0
            ulong extractedSoFar = 0;
            foreach (ulong m in topology.PerformanceCoreMasks) extractedSoFar |= m;
            foreach (ulong m in topology.EfficiencyCoreMasks) extractedSoFar |= m;

            // Find missing threads (Core 0 OS reserved threads)
            ulong totalSystemMask = (1UL << logical) - 1;
            ulong missingMask = totalSystemMask & ~extractedSoFar;

            if (missingMask != 0)
            {
                var group0 = new ProcessorCoreGroup { CoreIndex = coreIndex, Title = $"Core {coreIndex} (OS)" };
                for (int t = 0; t < logical; t++)
                {
                    if ((missingMask & (1UL << t)) != 0)
                    {
                        bool on = (effectiveMask & (1UL << t)) != 0;
                        var row = new ProcessorThreadItem { Index = t, IsChecked = on };
                        row.PropertyChanged += (_, e) =>
                        {
                            if (e.PropertyName == nameof(ProcessorThreadItem.IsChecked))
                                RefreshThreadCount(item);
                        };
                        group0.Threads.Add(row);
                    }
                }
                if (group0.Threads.Count > 0)
                {
                    item.CoreGroups.Add(group0);
                    coreIndex++;
                }
            }

            MapCores(topology.PerformanceCoreMasks, topology.IsHybrid ? "P-Core" : "Phys");
            MapCores(topology.EfficiencyCoreMasks, "E-Core");
            RefreshThreadCount(item);
            if (info.AffinityMask is null)
                item.SelectedThreadCountText += " (system default)";
            return Task.CompletedTask;
        }

        private static void RefreshThreadCount(AffinityDeviceItem item)
        {
            int total = 0, on = 0;
            foreach (var group in item.CoreGroups)
                foreach (var thread in group.Threads)
                {
                    total++;
                    if (thread.IsChecked) on++;
                }
            item.SelectedThreadCountText = $"{on} of {total} selected";
        }

        [RelayCommand]
        private void ToggleProcessorMask()
        {
            if (SelectedDevice is not null)
                SelectedDevice.IsProcessorMaskExpanded = !SelectedDevice.IsProcessorMaskExpanded;
        }

        [RelayCommand]
        private void SelectAllThreads()
        {
            if (SelectedDevice is null) return;
            foreach (var group in SelectedDevice.CoreGroups)
                foreach (var thread in group.Threads)
                    thread.IsChecked = true;
        }

        [RelayCommand]
        private void ClearThreads()
        {
            if (SelectedDevice is null) return;
            foreach (var group in SelectedDevice.CoreGroups)
                foreach (var thread in group.Threads)
                    thread.IsChecked = false;
        }

        [RelayCommand]
        private void ToggleGroup(string? group)
        {
            switch (group)
            {
                case "Graphics": IsGraphicsExpanded = !IsGraphicsExpanded; break;
                case "Network": IsNetworkExpanded = !IsNetworkExpanded; break;
                case "Usb": IsUsbExpanded = !IsUsbExpanded; break;
                case "Audio": IsAudioExpanded = !IsAudioExpanded; break;
            }
        }

        [ObservableProperty]
        public partial string StatusText { get; set; } = "Scanning for devices...";

        public System.Collections.Generic.List<string> PriorityOptions { get; } =
            new() { "Undefined", "Low", "Normal", "High" };

        public System.Collections.Generic.List<PolicyOption> PolicyOptions { get; } =
            new()
            {
                new PolicyOption("Machine Default", "IrqPolicyMachineDefault"),
                new PolicyOption("All Close Processors", "IrqPolicyAllCloseProcessors"),
                new PolicyOption("One Close Processor", "IrqPolicyOneCloseProcessor"),
                new PolicyOption("All Matching Processors", "IrqPolicyAllMatchingProcessors"),
                new PolicyOption("Specified Processors", "IrqPolicySpecifiedProcessors"),
                new PolicyOption("Spread Messages", "IrqPolicySpreadMessagesAcrossAllProcessors")
            };

        // ── Device enumeration ──────────────────────────────────────────────

        [RelayCommand]
        private async Task RefreshDevicesAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            StatusText = "Scanning for devices...";
            try
            {
                var devices = await _affinityService.EnumerateDevicesAsync();
                GraphicsDevices.Clear();
                NetworkDevices.Clear();
                UsbDevices.Clear();
                AudioDevices.Clear();
                foreach (var device in devices.OrderBy(d => d.Name))
                {
                    var info = _affinityService.GetInterruptInfo(device.DeviceInstanceId);
                    device.MsiEnabled = info.MsiSupported ?? false;
                    device.MsiLimit = info.MsiLimit is > 0 ? info.MsiLimit.Value : (info.MaxMsiLimit is > 0 ? info.MaxMsiLimit.Value : 1);
                    device.MsiLimitText = info.MsiLimit?.ToString() ?? (info.MaxMsiLimit?.ToString() ?? "—");
                    device.DevicePolicyShort = AffinityService.DevicePolicyShort(info.DevicePolicy);
                    device.DevicePriorityShort = info.DevicePriority is null
                        ? "Undefined"
                        : AffinityService.DevicePriorityName(info.DevicePriority);
                    device.AffinityText = AffinityService.AffinityMaskText(info.AffinityMask);
                    device.IrqText = info.MsiSupported == true ? "MSI" : info.MsiSupported == false ? "Line" : "—";

                    switch (device.Category)
                    {
                        case "Graphics": GraphicsDevices.Add(device); break;
                        case "Network": NetworkDevices.Add(device); break;
                        case "Usb": UsbDevices.Add(device); break;
                        case "Audio": AudioDevices.Add(device); break;
                    }
                }

                int total = devices.Count;
                StatusText = total == 0
                    ? "No tunable devices found."
                    : $"{total} device{(total == 1 ? "" : "s")} found.";
            }
            catch (Exception ex)
            {
                StatusText = $"Scan failed: {ex.Message}";
            }
            finally
            {
                IsRefreshing = false;
            }
        }

        // ── Per-device Apply (dialog) ───────────────────────────────────────

        /// <summary>
        /// Writes the dialog's staged values (MSI, policy, priority, mask) to the
        /// registry and pushes each change onto the undo stack.
        /// </summary>
        public void ApplyDeviceChanges(AffinityDeviceItem item)
        {
            var info = _affinityService.GetInterruptInfo(item.DeviceInstanceId);

            // MSI Mode
            bool oldMsi = info.MsiSupported ?? false;
            if (item.MsiEnabled != oldMsi)
            {
                if (_affinityService.SetMsiEnabled(item.DeviceInstanceId, item.MsiEnabled))
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "MsiEnabled", oldMsi, item.MsiEnabled, DateTime.Now));
            }

            // MSI Limit
            int oldLimit = (int)(info.MsiLimit ?? 1);
            int newLimit = (int)item.MsiLimit;
            if (newLimit != oldLimit)
            {
                if (_affinityService.SetMsiLimit(item.DeviceInstanceId, newLimit))
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "MessageNumberLimit", oldLimit, newLimit, DateTime.Now));
            }

            // Max MSI Limit is read-only per user instruction and cannot be overridden

            // Device Policy
            int newPolicy = PolicyNameToInt(item.SelectedPolicy);
            int? oldPolicy = info.DevicePolicy;
            if (newPolicy != (oldPolicy ?? 0))
            {
                if (_affinityService.SetDevicePolicy(item.DeviceInstanceId, newPolicy))
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "DevicePolicy", oldPolicy, newPolicy, DateTime.Now));
            }

            // Device Priority
            int newPriority = PriorityNameToInt(item.SelectedPriority);
            int? oldPriority = info.DevicePriority;
            if (newPriority != (oldPriority ?? -1))
            {
                if (_affinityService.SetDevicePriority(item.DeviceInstanceId, newPriority))
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "DevicePriority", oldPriority, newPriority, DateTime.Now));
            }

            // Affinity Mask
            ulong newMask = BuildMaskFromGroups(item);
            ulong oldMask = info.AffinityMask ?? 0;
            // No explicit override + everything still checked = "system
            // default", not a change — don't write a redundant full mask.
            bool isDefaultUnchanged = info.AffinityMask is null && newMask == RenderedMask(item);
            if (!isDefaultUnchanged && newMask != oldMask)
            {
                if (_affinityService.SetAffinityMask(item.DeviceInstanceId, newMask))
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "AffinityMask", oldMask, newMask, DateTime.Now));
            }

            // Update the table columns live
            var refreshed = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
            item.DevicePolicyShort = AffinityService.DevicePolicyShort(refreshed.DevicePolicy);
            item.DevicePriorityShort = refreshed.DevicePriority is null ? "—" : AffinityService.DevicePriorityName(refreshed.DevicePriority);
            item.AffinityText = AffinityService.AffinityMaskText(refreshed.AffinityMask);
            item.IrqText = refreshed.MsiSupported == true ? "MSI" : refreshed.MsiSupported == false ? "Line" : "—";

            // Resync the dialog checkboxes from readback so a partial/failed
            // write can never leave them lying about the applied affinity.
            ResyncGroupsFromMask(item, refreshed.AffinityMask);
        }

        private static ulong BuildMaskFromGroups(AffinityDeviceItem item)
        {
            ulong mask = 0;
            foreach (var group in item.CoreGroups)
                foreach (var thread in group.Threads)
                    if (thread.IsChecked)
                        mask |= 1UL << thread.Index;
            return mask;
        }

        /// <summary>Bitmask of every thread currently rendered in the dialog grid.</summary>
        private static ulong RenderedMask(AffinityDeviceItem item)
        {
            ulong mask = 0;
            foreach (var group in item.CoreGroups)
                foreach (var thread in group.Threads)
                    mask |= 1UL << thread.Index;
            return mask;
        }

        /// <summary>
        /// Re-checks the dialog grid from a freshly read mask (null = system
        /// default = everything on) and updates the selected-count text.
        /// </summary>
        private static void ResyncGroupsFromMask(AffinityDeviceItem item, ulong? mask)
        {
            if (item.CoreGroups.Count == 0) return;
            ulong effective = mask ?? RenderedMask(item);
            foreach (var group in item.CoreGroups)
                foreach (var thread in group.Threads)
                    thread.IsChecked = (effective & (1UL << thread.Index)) != 0;
            RefreshThreadCount(item);
            if (mask is null)
                item.SelectedThreadCountText += " (system default)";
        }

        // ── Optimize command ────────────────────────────────────────────────

        [ObservableProperty]
        public partial bool IsOptimizing { get; set; }
        
        [ObservableProperty]
        public partial bool RequiresRestart { get; set; }
        
        private readonly HashSet<string> _pendingRestartIds = new(StringComparer.OrdinalIgnoreCase);

        [RelayCommand]
        private async Task RestartPendingAsync()
        {
            if (_pendingRestartIds.Count == 0) return;
            StatusText = $"Restarting {_pendingRestartIds.Count} device(s)...";
            foreach (var id in _pendingRestartIds)
            {
                await AffinityService.RestartDeviceAsync(id);
            }
            _pendingRestartIds.Clear();
            RequiresRestart = false;
            StatusText = "Device restarts complete.";
        }
        
        public void UpdateMsiLimitInline(AffinityDeviceItem item, double newValue)
        {
            var info = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
            int oldLimit = (int)(info.MsiLimit ?? 1);
            int newLimit = (int)newValue;
            if (newLimit != oldLimit)
            {
                item.MsiLimit = newLimit;
                if (_affinityService.SetMsiLimit(item.DeviceInstanceId, newLimit))
                {
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "MessageNumberLimit", oldLimit, newLimit, DateTime.Now));
                    item.MsiLimitText = newLimit.ToString();
                }
            }
        }
        
        private static bool IsElevated()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        [RelayCommand]
        private async Task OptimizeAsync()
        {
            if (!IsElevated())
            {
                StatusText = "Optimization requires running the application as Administrator.";
                return;
            }
            if (IsOptimizing) return;
            IsOptimizing = true;
            StatusText = "Optimizing IRQ & affinity...";

            try
            {
                var topology = TopologyService.Get();
                var allDevices = GetAllDevices();
                var modifiedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                Debug.WriteLine($"[Topology] TotalLogical={topology.TotalLogicalCores}, Physical={topology.PhysicalCores}, Reserved={topology.ReservedCore}");
                
                // Available cores (Performance only) are already stripped of the OS core 0 in TopologyService
                var rawCores = new List<ulong>(topology.PerformanceCoreMasks);

                // Classify into tiers. Graphics and Network get SEPARATE physical
                // cores — NIC interrupts are frequent (especially Wi-Fi 7 under
                // load) and sharing a core with the GPU's DPCs hurts both.
                var graphicsTier = new List<AffinityDeviceItem>();
                var networkTier = new List<AffinityDeviceItem>();
                var normalTier = new List<AffinityDeviceItem>();

                foreach (var dev in allDevices)
                {
                    switch (dev.Category)
                    {
                        case "Graphics":
                            graphicsTier.Add(dev);
                            break;
                        case "Network":
                            // Prefer a dedicated core when we can afford it.
                            if (topology.PhysicalCores > 4)
                                networkTier.Add(dev);
                            else
                                normalTier.Add(dev);
                            break;
                        case "Usb":
                        case "Audio":
                            normalTier.Add(dev);
                            break;
                        // "Other" / unknown — never touched
                    }
                }

                // ─ Step 1: Enable MSI for EVERY device that reports support ─
                foreach (var dev in allDevices)
                {
                    if (!dev.IsChecked) continue;
                    var info = _affinityService.GetInterruptInfo(dev.DeviceInstanceId);
                    bool oldMsi = info.MsiSupported ?? false;
                    if (!oldMsi)
                    {
                        if (_affinityService.SetMsiEnabled(dev.DeviceInstanceId, true))
                        {
                            PushChange(new AffinityChange(dev.DeviceInstanceId, dev.Name, "MsiEnabled", false, true, DateTime.Now));
                            modifiedIds.Add(dev.DeviceInstanceId);
                        }
                    }
                }

                // ─ Step 2: Topology-Aware Affinity assignment ─
                
                // Group available performance cores by their Core Complex (CCX / L3 Cache)
                var ccxGroups = new Dictionary<ulong, List<ulong>>();
                foreach (var ccxMask in topology.CoreComplexMasks)
                    ccxGroups[ccxMask] = new List<ulong>();

                foreach (var coreMask in rawCores)
                {
                    bool mapped = false;
                    foreach (var ccxMask in topology.CoreComplexMasks)
                    {
                        if ((coreMask & ccxMask) == coreMask)
                        {
                            ccxGroups[ccxMask].Add(coreMask);
                            mapped = true;
                            break;
                        }
                    }
                    if (!mapped)
                    {
                        // Fallback grouping if CCX masking failed for this core
                        if (!ccxGroups.ContainsKey(0)) ccxGroups[0] = new List<ulong>();
                        ccxGroups[0].Add(coreMask);
                    }
                }
                
                // Sort CCX groups by size descending
                var validCcxs = ccxGroups.Values.Where(g => g.Count > 0).OrderByDescending(g => g.Count).ToList();
                if (validCcxs.Count == 0) validCcxs.Add(rawCores); // Fallback

                ulong gpuMask = 0;
                ulong nicMask = 0;
                ulong peripheralMask = 0;

                int coresPerGroup = topology.PhysicalCores > 8 ? 2 : 1;

                ulong BuildMask(List<ulong> pool, int requestedCores)
                {
                    ulong m = 0;
                    for (int i = 0; i < requestedCores && pool.Count > 0; i++)
                    {
                        m |= pool[^1];
                        pool.RemoveAt(pool.Count - 1);
                    }
                    return m;
                }

                if (validCcxs.Count >= 2)
                {
                    // Multi-CCX (e.g. Ryzen 9, Threadripper, multi-socket)
                    // GPU on the largest CCX; NIC and peripherals on the next CCX
                    // but on DIFFERENT physical cores.
                    gpuMask = BuildMask(validCcxs[0], coresPerGroup);
                    nicMask = BuildMask(validCcxs[1], coresPerGroup);
                    peripheralMask = BuildMask(validCcxs[1], coresPerGroup);
                }
                else
                {
                    // Single CCX / unified L3: each tier gets its own physical
                    // core (or pair of threads), taken from opposite ends of the
                    // pool so GPU/NIC/peripherals never share a physical core.
                    var pool = validCcxs[0];
                    gpuMask = BuildMask(pool, coresPerGroup);
                    nicMask = BuildMask(pool, coresPerGroup);
                    peripheralMask = BuildMask(pool, coresPerGroup);
                }

                // If any mask failed to build because we ran out of cores, fallback to just spreading
                if (gpuMask == 0 && rawCores.Count > 0) gpuMask = rawCores[^1];
                if (nicMask == 0 && rawCores.Count > 0) nicMask = rawCores[0];
                if (peripheralMask == 0 && rawCores.Count > 0) peripheralMask = rawCores[0];

                void ApplyTier(IEnumerable<AffinityDeviceItem> devices, ulong mask, int priority)
                {
                    foreach (var dev in devices)
                    {
                        if (!dev.IsChecked) continue;
                        var info = _affinityService.GetInterruptInfo(dev.DeviceInstanceId);

                        ulong oldMask = info.AffinityMask ?? 0;
                        if (mask != 0 && oldMask != mask && _affinityService.SetAffinityMask(dev.DeviceInstanceId, mask))
                        {
                            PushChange(new AffinityChange(dev.DeviceInstanceId, dev.Name, "AffinityMask", oldMask, mask, DateTime.Now));
                            modifiedIds.Add(dev.DeviceInstanceId);
                        }

                        // Set policy to SpecifiedProcessors (4)
                        int oldPolicy = info.DevicePolicy ?? 0;
                        if (oldPolicy != 4 && _affinityService.SetDevicePolicy(dev.DeviceInstanceId, 4))
                        {
                            PushChange(new AffinityChange(dev.DeviceInstanceId, dev.Name, "DevicePolicy", oldPolicy, 4, DateTime.Now));
                            modifiedIds.Add(dev.DeviceInstanceId);
                        }

                        // Set priority
                        int oldPrio = info.DevicePriority ?? -1;
                        if (oldPrio != priority)
                        {
                            if (priority == -1)
                            {
                                // Clear Priority explicitly back to Undefined
                                if (_affinityService.SetDevicePriority(dev.DeviceInstanceId, -1)) // Under the hood SetDevicePriority deleting registry key for -1 is how AffinityService handles it, no it doesn't so we explicitly call clear!
                                {
                                }
                                _affinityService.ClearAffinityPolicy(dev.DeviceInstanceId, "DevicePriority");
                                PushChange(new AffinityChange(dev.DeviceInstanceId, dev.Name, "DevicePriority", oldPrio == -1 ? null : (object)oldPrio, null, DateTime.Now));
                                modifiedIds.Add(dev.DeviceInstanceId);
                            }
                            else if (_affinityService.SetDevicePriority(dev.DeviceInstanceId, priority))
                            {
                                PushChange(new AffinityChange(dev.DeviceInstanceId, dev.Name, "DevicePriority", oldPrio == -1 ? null : (object)oldPrio, priority, DateTime.Now));
                                modifiedIds.Add(dev.DeviceInstanceId);
                            }
                        }
                    }
                }
                ApplyTier(graphicsTier, gpuMask, -1);        // GPU alone on its core(s)
                ApplyTier(networkTier, nicMask, -1);         // NICs on a different core
                ApplyTier(normalTier, peripheralMask, -1);   // USB/audio on a third

                if (modifiedIds.Count > 0)
                {
                    StatusText = $"Restarting {modifiedIds.Count} modified device(s)...";
                    foreach (var id in modifiedIds)
                    {
                        await AffinityService.RestartDeviceAsync(id);
                    }
                }

                await RefreshDevicesAsync();
                StatusText = $"Optimization complete — {_undoStack.Count} change(s) applied and restarted.";
            }
            catch (Exception ex)
            {
                StatusText = $"Optimization failed: {ex.Message}";
            }
            finally
            {
                IsOptimizing = false;
            }
        }
        // ── Undo / Redo / Restore commands ──────────────────────────────────

        [RelayCommand]
        private async Task UndoAsync()
        {
            if (!IsElevated()) return;
            if (_undoStack.Count == 0) return;
            var change = _undoStack[^1];
            _undoStack.RemoveAt(_undoStack.Count - 1);

            RevertChange(change);
            _redoStack.Add(change);
            RefreshChangeState();
            
            _pendingRestartIds.Add(change.DeviceId);
            RequiresRestart = true;
            
            // Prompt user for restart, skip auto restart
            await RefreshDevicesAsync();
            StatusText = "Undo applied. Restart required.";
        }

        [RelayCommand]
        private async Task RedoAsync()
        {
            if (!IsElevated()) return;
            if (_redoStack.Count == 0) return;
            var change = _redoStack[^1];
            _redoStack.RemoveAt(_redoStack.Count - 1);

            ApplyChange(change);
            _undoStack.Add(change);
            RefreshChangeState();

            _pendingRestartIds.Add(change.DeviceId);
            RequiresRestart = true;

            // Prompt user for restart, skip auto restart
            await RefreshDevicesAsync();
            StatusText = "Redo applied. Restart required.";
        }

        [RelayCommand]
        private async Task RestoreAsync()
        {
            if (!IsElevated()) return;
            if (_undoStack.Count == 0) return;
            StatusText = "Restoring original values...";
            var modifiedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Revert in reverse order
            for (int i = _undoStack.Count - 1; i >= 0; i--)
            {
                var change = _undoStack[i];
                RevertChange(change);
                modifiedIds.Add(change.DeviceId);
            }
            _undoStack.Clear();
            _redoStack.Clear();
            RefreshChangeState();

            foreach (var id in modifiedIds)
                _pendingRestartIds.Add(id);
            
            if (_pendingRestartIds.Count > 0)
                RequiresRestart = true;

            await RefreshDevicesAsync();
            StatusText = "All changes restored. Restart required.";
        }

        // ── View Changes ────────────────────────────────────────────────────

        public ObservableCollection<AffinityChange> TrackedChanges { get; } = new();

        [RelayCommand]
        private void ViewChanges()
        {
            // Removed: TrackedChanges is now synced directly inside RefreshChangeState
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private void RevertChange(AffinityChange change)
        {
            switch (change.PropertyName)
            {
                case "MsiEnabled":
                    _affinityService.SetMsiEnabled(change.DeviceId, change.OldValue is true);
                    break;
                case "DevicePolicy":
                    if (change.OldValue is int dp)
                        _affinityService.SetDevicePolicy(change.DeviceId, dp);
                    else
                        _affinityService.ClearAffinityPolicy(change.DeviceId, "DevicePolicy");
                    break;
                case "DevicePriority":
                    if (change.OldValue is int dpr)
                        _affinityService.SetDevicePriority(change.DeviceId, dpr);
                    else
                        _affinityService.ClearAffinityPolicy(change.DeviceId, "DevicePriority");
                    break;
                case "AffinityMask":
                    ulong oldMask = change.OldValue is ulong m ? m : 0;
                    _affinityService.SetAffinityMask(change.DeviceId, oldMask);
                    break;
            }
        }

        private void ApplyChange(AffinityChange change)
        {
            switch (change.PropertyName)
            {
                case "MsiEnabled":
                    _affinityService.SetMsiEnabled(change.DeviceId, change.NewValue is true);
                    break;
                case "DevicePolicy":
                    if (change.NewValue is int dp)
                        _affinityService.SetDevicePolicy(change.DeviceId, dp);
                    break;
                case "DevicePriority":
                    if (change.NewValue is int dpr)
                        _affinityService.SetDevicePriority(change.DeviceId, dpr);
                    break;
                case "AffinityMask":
                    ulong newMask = change.NewValue is ulong m ? m : 0;
                    _affinityService.SetAffinityMask(change.DeviceId, newMask);
                    break;
            }
        }

        private List<AffinityDeviceItem> GetAllDevices()
        {
            var all = new List<AffinityDeviceItem>();
            all.AddRange(GraphicsDevices);
            all.AddRange(NetworkDevices);
            all.AddRange(UsbDevices);
            all.AddRange(AudioDevices);
            return all;
        }

        private static int PolicyNameToInt(string name) => name switch
        {
            "IrqPolicyMachineDefault" => 0,
            "IrqPolicyAllCloseProcessors" => 1,
            "IrqPolicyOneCloseProcessor" => 2,
            "IrqPolicyAllMatchingProcessors" => 3,
            "IrqPolicySpecifiedProcessors" => 4,
            "IrqPolicySpreadMessagesAcrossAllProcessors" => 5,
            _ => 0
        };

        private static int PriorityNameToInt(string name) => name switch
        {
            "Low" => 0,
            "Normal" => 1,
            "High" => 2,
            _ => -1 // "Undefined" maps to no write
        };
    }
}
