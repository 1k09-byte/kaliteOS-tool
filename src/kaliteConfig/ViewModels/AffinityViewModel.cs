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
using kaliteConfig.GpuOverclock;
using kaliteConfig.GpuOverclock.Services;
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
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

        // -- Change tracking -------------------------------------------------

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

        // -- Dialog state ----------------------------------------------------

        /// <summary>
        /// Fills a device's dialog state from live read-only interrupt data and
        /// builds the processor-mask grid (SMT pairs), pre-checking the threads
        /// in the current assignment mask.
        /// </summary>
        public Task LoadDeviceDetailsAsync(AffinityDeviceItem item)
        {
            // A GPU restart / driver install re-enumerates the adapter, so the row
            // can outlive the device it points at. Failing loudly beats showing a
            // dialog full of empty values that writes interrupt settings nowhere.
            if (!_affinityService.DeviceExists(item.DeviceInstanceId))
            {
                StatusText = $"{item.Name} is no longer present - the device was restarted or re-enumerated. Rescanning…";
                _ = RefreshDevicesCommand.ExecuteAsync(null);
                throw new InvalidOperationException(
                    $"{item.Name} is no longer present. It was restarted or re-enumerated (this happens after a GPU restart or driver install). " +
                    "The device list has been rescanned - pick the device again.");
            }

            SelectedDevice = item;
            var info = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
            item.MsiEnabled = info.MsiSupported ?? false;
            // MSI Limit shows only the explicit value (Auto when absent) - the
            // hardware max lives in MaxMsiLimit and is only the edit cap.
            item.MsiLimit = info.MsiLimit ?? 0;
            item.MsiLimitText = info.MsiLimit?.ToString() ?? "Auto";
            item.MaxMsiLimit = info.MaxMsiLimit ?? 0;
            item.DevicePolicyShort = AffinityService.DevicePolicyShort(info.DevicePolicy);
            item.DevicePriorityShort = AffinityService.DevicePriorityName(info.DevicePriority);
            item.SelectedPriority = AffinityService.DevicePriorityName(info.DevicePriority);
            item.SelectedPolicy = info.DevicePolicy is null
                ? "IrqPolicyMachineDefault"
                : AffinityService.DevicePolicyName(info.DevicePolicy);

            var topology = TopologyService.Get();
            int logical = topology.TotalLogicalCores;
            item.CoreGroups.Clear();

            // No explicit override in the registry means the device runs on
            // every logical processor - pre-check everything so the dialog
            // shows the EFFECTIVE affinity instead of "0 of N selected".
            ulong fullMask = logical >= 64 ? ulong.MaxValue : ((1UL << logical) - 1);
            ulong effectiveMask = info.AffinityMask ?? fullMask;

            int coreIndex = 0;

            void MapCores(IEnumerable<ulong> masks)
            {
                // Note: masks includes core 0 if we're rendering the UI (Wait, TopologyService Detection removes reserved core 0!
                // To avoid breaking the UI indices if we omitted Core 0, I should just render all cores. Wait! TopologyService.Detect removes Core 0 from PerformanceCoreMasks!
                // So Core 0's threads won't be rendered. Let's fix that by re-adding Core 0 or handling it gracefully:
                foreach (ulong mask in masks)
                {
                    var group = new ProcessorCoreGroup { CoreIndex = coreIndex, Title = $"Core {coreIndex}" };
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
                var group0 = new ProcessorCoreGroup { CoreIndex = coreIndex, Title = $"Core {coreIndex}" };
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

            MapCores(topology.PerformanceCoreMasks);
            MapCores(topology.EfficiencyCoreMasks);
            RefreshThreadCount(item);
            if (info.AffinityMask is null)
                item.SelectedThreadCountText += " (system default)";
            SnapshotDialogState(item);
            return Task.CompletedTask;
        }

        // The dialog binds two-way straight into the row item, so Cancel must
        // restore the values captured when the dialog opened - otherwise the
        // table keeps showing edits that were never written.
        private AffinityDeviceItem? _dialogItem;
        private bool _dialogMsi;
        private double _dialogLimit;
        private string _dialogPolicy = "IrqPolicyMachineDefault";
        private string _dialogPriority = "Undefined";
        private ulong _dialogMask;

        private void SnapshotDialogState(AffinityDeviceItem item)
        {
            _dialogItem = item;
            _dialogMsi = item.MsiEnabled;
            _dialogLimit = item.MsiLimit;
            _dialogPolicy = item.SelectedPolicy;
            _dialogPriority = item.SelectedPriority;
            _dialogMask = BuildMaskFromGroups(item);
        }

        public void DiscardDialogChanges()
        {
            if (SelectedDevice is null || !ReferenceEquals(SelectedDevice, _dialogItem)) return;
            var item = SelectedDevice;
            item.MsiEnabled = _dialogMsi;
            item.MsiLimit = _dialogLimit;
            item.SelectedPolicy = _dialogPolicy;
            item.SelectedPriority = _dialogPriority;
            foreach (var group in item.CoreGroups)
                foreach (var thread in group.Threads)
                    thread.IsChecked = (_dialogMask & (1UL << thread.Index)) != 0;
            RefreshThreadCount(item);
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

        // Dropdown shows the raw policy names, exactly like the reference tool.
        public System.Collections.Generic.List<PolicyOption> PolicyOptions { get; } =
            new()
            {
                new PolicyOption("IrqPolicyMachineDefault", "IrqPolicyMachineDefault"),
                new PolicyOption("IrqPolicyAllCloseProcessors", "IrqPolicyAllCloseProcessors"),
                new PolicyOption("IrqPolicyOneCloseProcessor", "IrqPolicyOneCloseProcessor"),
                new PolicyOption("IrqPolicyAllProcessorsInMachine", "IrqPolicyAllProcessorsInMachine"),
                new PolicyOption("IrqPolicySpecifiedProcessors", "IrqPolicySpecifiedProcessors"),
                new PolicyOption("IrqPolicySpreadMessagesAcrossAllProcessors", "IrqPolicySpreadMessagesAcrossAllProcessors")
            };

        // -- Device enumeration ----------------------------------------------

        [RelayCommand]
        private async Task RefreshDevicesAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            StatusText = "Scanning for devices...";
            try
            {
                var devices = await _affinityService.EnumerateDevicesAsync();

                // Keep the selected device across a rescan when it is still
                // present - a GPU restart must not silently drop the dialog's
                // device (or leave it pointing at a list the user can no longer
                // see). The row objects are new after every scan, so match by
                // instance id.
                string? selectedId = SelectedDevice?.DeviceInstanceId;
                bool wasPresent = selectedId is not null
                    && devices.Any(d => string.Equals(d.DeviceInstanceId, selectedId, StringComparison.OrdinalIgnoreCase));

                GraphicsDevices.Clear();
                NetworkDevices.Clear();
                UsbDevices.Clear();
                AudioDevices.Clear();
                foreach (var device in devices.OrderBy(d => d.Name))
                {
                    var info = _affinityService.GetInterruptInfo(device.DeviceInstanceId);
                    device.MsiEnabled = info.MsiSupported ?? false;
                    device.MsiLimit = info.MsiLimit ?? 0;
                    device.MsiLimitText = info.MsiLimit?.ToString() ?? "Auto";
                    device.MaxMsiLimit = info.MaxMsiLimit ?? 0;
                    device.DevicePolicyShort = AffinityService.DevicePolicyShort(info.DevicePolicy);
                    device.DevicePriorityShort = AffinityService.DevicePriorityName(info.DevicePriority);
                    device.AffinityText = AffinityService.AffinityMaskText(info.AffinityMask);
                    device.IrqText = info.MsiSupported == true ? "MSI" : info.MsiSupported == false ? "Line" : "-";

                    switch (device.Category)
                    {
                        case "Graphics": GraphicsDevices.Add(device); break;
                        case "Network": NetworkDevices.Add(device); break;
                        case "Usb": UsbDevices.Add(device); break;
                        case "Audio": AudioDevices.Add(device); break;
                    }
                }

                if (selectedId is not null)
                {
                    SelectedDevice = GraphicsDevices.Concat(NetworkDevices).Concat(UsbDevices).Concat(AudioDevices)
                        .FirstOrDefault(d => string.Equals(d.DeviceInstanceId, selectedId, StringComparison.OrdinalIgnoreCase));
                    if (SelectedDevice is null && wasPresent)
                    {
                        SelectedDevice = null;
                    }
                }

                int total = devices.Count;
                StatusText = total == 0
                    ? "No tunable devices found."
                    : $"{total} device{(total == 1 ? "" : "s")} found."
                      + (wasPresent && SelectedDevice is null ? " The previously selected device is gone." : "");
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

        /// <summary>True while the device behind a row is still enumerated.</summary>
        public bool IsDevicePresent(AffinityDeviceItem? item)
            => item is not null && _affinityService.DeviceExists(item.DeviceInstanceId);

        // -- Per-device Apply (dialog) ---------------------------------------

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

            // MSI Limit - 0 (Auto) deletes the value, like the reference tool.
            int oldLimit = (int)(info.MsiLimit ?? 0);
            int newLimit = (int)item.MsiLimit;
            if (newLimit != oldLimit)
            {
                bool ok = newLimit == 0
                    ? _affinityService.ClearMsiValue(item.DeviceInstanceId, "MessageNumberLimit")
                    : _affinityService.SetMsiLimit(item.DeviceInstanceId, newLimit);
                if (ok)
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "MessageNumberLimit", oldLimit, newLimit, DateTime.Now));
            }

            // Max MSI Limit is read-only per user instruction and cannot be overridden

            // Device Policy
            int newPolicy = PolicyNameToInt(item.SelectedPolicy);
            int? oldPolicy = info.DevicePolicy;
            bool policyTouched = false;
            if (newPolicy != (oldPolicy ?? 0))
            {
                if (_affinityService.SetDevicePolicy(item.DeviceInstanceId, newPolicy))
                {
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "DevicePolicy", oldPolicy, newPolicy, DateTime.Now));
                    policyTouched = true;
                }
            }

            // A non-Specified policy must not keep a stale override behind -
            // the reference tool deletes it in that case.
            if (policyTouched && newPolicy != 4)
                _affinityService.ClearAffinityPolicy(item.DeviceInstanceId, "AssignmentSetOverride");

            // Device Priority - Undefined (0) deletes the value, like the reference.
            int newPriority = PriorityNameToInt(item.SelectedPriority);
            int oldPriority = info.DevicePriority ?? 0;
            if (newPriority != oldPriority)
            {
                bool ok = newPriority == 0
                    ? _affinityService.ClearAffinityPolicy(item.DeviceInstanceId, "DevicePriority")
                    : _affinityService.SetDevicePriority(item.DeviceInstanceId, newPriority);
                if (ok)
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "DevicePriority",
                        oldPriority == 0 ? null : (object)oldPriority, newPriority == 0 ? null : (object)newPriority, DateTime.Now));
            }

            // Affinity Mask
            ulong newMask = BuildMaskFromGroups(item);
            ulong oldMask = info.AffinityMask ?? 0;
            // No explicit override + everything still checked = "system
            // default", not a change - don't write a redundant full mask.
            bool isDefaultUnchanged = info.AffinityMask is null && newMask == RenderedMask(item);
            bool maskTouched = false;
            if (!isDefaultUnchanged && newMask != oldMask)
            {
                if (_affinityService.SetAffinityMask(item.DeviceInstanceId, newMask))
                {
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "AffinityMask", oldMask, newMask, DateTime.Now));
                    maskTouched = true;
                }
            }

            // NIC RSS follows the effective mask, like the reference tool.
            if ((maskTouched || policyTouched) && item.Category == "Network")
                _affinityService.SetRSS(item.DeviceInstanceId, newPolicy == 4 ? newMask : 0);

            // Update the table columns live
            var refreshed = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
            item.DevicePolicyShort = AffinityService.DevicePolicyShort(refreshed.DevicePolicy);
            item.DevicePriorityShort = refreshed.DevicePriority is null ? "-" : AffinityService.DevicePriorityName(refreshed.DevicePriority);
            item.AffinityText = AffinityService.AffinityMaskText(refreshed.AffinityMask);
            item.IrqText = refreshed.MsiSupported == true ? "MSI" : refreshed.MsiSupported == false ? "Line" : "-";

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

        // -- Optimize command ------------------------------------------------

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
            await RestartDevicesQuiescedAsync(_pendingRestartIds, "affinity pending restarts");
            _pendingRestartIds.Clear();
            RequiresRestart = false;
            StatusText = "Device restarts complete.";
        }

        /// <summary>
        /// Restarts devices with all native GPU access held off: a telemetry
        /// or fan tick landing mid-restart can fault INSIDE nvapi64/nvml
        /// (0xc0000005) where no managed catch can contain it - the exact
        /// crash seen after Optimize restarts the GPU. Settles PnP, then
        /// forces fresh native handles before anyone calls in again.
        /// </summary>
        private static async Task RestartDevicesQuiescedAsync(
            IEnumerable<string> deviceIds, string reason)
        {
            using (HardwareQuiesceGate.Hold(reason))
            {
                foreach (var id in deviceIds)
                {
                    await AffinityService.RestartDeviceAsync(id);
                }
                // Let PnP re-enumeration (especially a restarted GPU) settle
                // before any native call goes near it again.
                await Task.Delay(TimeSpan.FromSeconds(4));
                GpuOverclockModule.Instance.Controller.InvalidateGpu();
                NvmlBridge.Reset();
            }
        }
        
        private static bool IsElevated()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        /// <summary>Isolates the lowest set bit (first thread of a core mask).</summary>
        private static ulong LowestSetBit(ulong mask) => mask & (ulong)(-(long)mask);

        /// <summary>Isolates the highest set bit (last thread of a core mask).</summary>
        private static ulong HighestSetBit(ulong mask) => 1UL << BitOperations.Log2(mask);

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
                
                // Group devices by type for the AutoOS affinity layout below.
                // "Other" / unknown - never touched.
                var graphicsTier = new List<AffinityDeviceItem>();
                var networkTier = new List<AffinityDeviceItem>();
                var usbTier = new List<AffinityDeviceItem>();
                var audioTier = new List<AffinityDeviceItem>();

                foreach (var dev in allDevices)
                {
                    switch (dev.Category)
                    {
                        case "Graphics":
                            graphicsTier.Add(dev);
                            break;
                        case "Network":
                            networkTier.Add(dev);
                            break;
                        case "Usb":
                            usbTier.Add(dev);
                            break;
                        case "Audio":
                            audioTier.Add(dev);
                            break;
                    }
                }

                // AutoOS affinity layout over ALL P-cores (core 0 included,
                // exactly like AutoOS). Last P-core -> NIC, second-last -> USB,
                // third + fourth-last -> GPU, fifth-last -> audio. With exactly
                // 4 P-cores the last core is split by thread.
                var pCores = new List<ulong>(topology.AllPerformanceCoreMasks);
                if (pCores.Count < 4)
                {
                    StatusText = "Optimization needs at least 4 performance cores - no changes made.";
                    return;
                }

                ulong nicMask, xhciMask, gpuMask, audioMask;
                if (pCores.Count == 4)
                {
                    audioMask = pCores[0];
                    gpuMask = pCores[1] | pCores[2];
                    xhciMask = LowestSetBit(pCores[3]);
                    nicMask = HighestSetBit(pCores[3]);
                }
                else
                {
                    nicMask = pCores[^1];
                    xhciMask = pCores[^2];
                    gpuMask = pCores[^3] | pCores[^4];
                    audioMask = pCores[^5];
                }

                // - Step 1: Enable MSI for EVERY device that reports support.
                // Like the reference, enabling MSI also pins the limit to the
                // device maximum (Auto became meaningless once MSI is forced on).
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
                            int wantLimit = (int)(info.MaxMsiLimit is > 0 ? info.MaxMsiLimit.Value : 1);
                            int oldLimit = (int)(info.MsiLimit ?? 0);
                            if (wantLimit != oldLimit && _affinityService.SetMsiLimit(dev.DeviceInstanceId, wantLimit))
                            {
                                PushChange(new AffinityChange(dev.DeviceInstanceId, dev.Name, "MessageNumberLimit", oldLimit, wantLimit, DateTime.Now));
                                dev.MsiLimit = wantLimit;
                                dev.MsiLimitText = wantLimit.ToString();
                            }
                        }
                    }
                }
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

                        // Set priority - Undefined (0) deletes the value, like
                        // the reference tool.
                        int oldPrio = info.DevicePriority ?? 0;
                        if (oldPrio != priority)
                        {
                            bool ok = priority == 0
                                ? _affinityService.ClearAffinityPolicy(dev.DeviceInstanceId, "DevicePriority")
                                : _affinityService.SetDevicePriority(dev.DeviceInstanceId, priority);
                            if (ok)
                            {
                                PushChange(new AffinityChange(dev.DeviceInstanceId, dev.Name, "DevicePriority",
                                    oldPrio == 0 ? null : (object)oldPrio, priority == 0 ? null : (object)priority, DateTime.Now));
                                modifiedIds.Add(dev.DeviceInstanceId);
                            }
                        }

                        // NIC RSS follows the effective mask, like the reference.
                        if (dev.Category == "Network")
                            _affinityService.SetRSS(dev.DeviceInstanceId, mask);
                    }
                }
                ApplyTier(graphicsTier, gpuMask, 0);   // GPU on its own core(s)
                ApplyTier(networkTier, nicMask, 0);    // NIC on the last P-core
                ApplyTier(usbTier, xhciMask, 0);       // USB on the second-last P-core
                ApplyTier(audioTier, audioMask, 0);    // Audio on the fifth-last P-core

                if (modifiedIds.Count > 0)
                {
                    StatusText = $"Restarting {modifiedIds.Count} modified device(s)...";
                    await RestartDevicesQuiescedAsync(modifiedIds, "affinity optimize restart");
                }

                await RefreshDevicesAsync();
                StatusText = $"Optimization complete - {_undoStack.Count} change(s) applied and restarted.";
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
        // -- Undo / Redo / Restore commands ----------------------------------

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

        // -- View Changes ----------------------------------------------------

        public ObservableCollection<AffinityChange> TrackedChanges { get; } = new();

        // -- Helpers ---------------------------------------------------------

        private void RevertChange(AffinityChange change)
        {
            switch (change.PropertyName)
            {
                case "MsiEnabled":
                    _affinityService.SetMsiEnabled(change.DeviceId, change.OldValue is true);
                    break;
                case "MessageNumberLimit":
                    if (change.OldValue is int oldLim)
                    {
                        if (oldLim == 0)
                            _affinityService.ClearMsiValue(change.DeviceId, "MessageNumberLimit");
                        else
                            _affinityService.SetMsiLimit(change.DeviceId, oldLim);
                    }
                    break;
                case "DevicePolicy":
                    if (change.OldValue is int dp)
                        _affinityService.SetDevicePolicy(change.DeviceId, dp);
                    else
                        _affinityService.ClearAffinityPolicy(change.DeviceId, "DevicePolicy");
                    break;
                case "DevicePriority":
                    if (change.OldValue is int dpr && dpr != 0)
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
                case "MessageNumberLimit":
                    if (change.NewValue is int newLim)
                    {
                        if (newLim == 0)
                            _affinityService.ClearMsiValue(change.DeviceId, "MessageNumberLimit");
                        else
                            _affinityService.SetMsiLimit(change.DeviceId, newLim);
                    }
                    break;
                case "DevicePolicy":
                    if (change.NewValue is int dp)
                        _affinityService.SetDevicePolicy(change.DeviceId, dp);
                    break;
                case "DevicePriority":
                    if (change.NewValue is int dpr && dpr != 0)
                        _affinityService.SetDevicePriority(change.DeviceId, dpr);
                    else
                        _affinityService.ClearAffinityPolicy(change.DeviceId, "DevicePriority");
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
            "IrqPolicyAllProcessorsInMachine" => 3,
            "IrqPolicySpecifiedProcessors" => 4,
            "IrqPolicySpreadMessagesAcrossAllProcessors" => 5,
            _ => 0
        };

        // Reference numbering: 0 = Undefined, 1 = Low, 2 = Normal, 3 = High.
        private static int PriorityNameToInt(string name) => name switch
        {
            "Low" => 1,
            "Normal" => 2,
            "High" => 3,
            _ => 0 // "Undefined" deletes the value (OS default)
        };
    }
}
