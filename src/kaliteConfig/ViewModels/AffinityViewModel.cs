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
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels
{
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

        [ObservableProperty]
        public partial bool IsGraphicsExpanded { get; set; } = true;

        [ObservableProperty]
        public partial bool IsNetworkExpanded { get; set; } = true;

        [ObservableProperty]
        public partial bool IsUsbExpanded { get; set; } = true;

        [ObservableProperty]
        public partial bool IsAudioExpanded { get; set; } = true;

        private readonly List<AffinityChange> _undoStack = new();
        private readonly List<AffinityChange> _redoStack = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ChangeCountText))]
        [NotifyPropertyChangedFor(nameof(ChangeCountNumberText))]
        public partial int ChangeCount { get; set; }

        [ObservableProperty]
        public partial bool CanUndo { get; set; }

        [ObservableProperty]
        public partial bool CanRedo { get; set; }

        [ObservableProperty]
        public partial bool HasNonDefaultDevices { get; set; }

        partial void OnHasNonDefaultDevicesChanged(bool value)
            => OnPropertyChanged(nameof(CanRestoreDefaults));

        public string ChangeCountText => $"View Changes ({ChangeCount})";

        public string ChangeCountNumberText => ChangeCount.ToString();

        // Headline numbers for the summary strip. Recomputed on every rescan so the
        // page can show what was found without a second pass over the device list.
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(DeviceCountText))]
        public partial int DeviceCount { get; set; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(MsiEnabledCountText))]
        public partial int MsiEnabledCount { get; set; }

        public string DeviceCountText => DeviceCount.ToString();
        public string MsiEnabledCountText => MsiEnabledCount.ToString();

        private void RefreshChangeState()
        {
            ChangeCount = _undoStack.Count;
            CanUndo = _undoStack.Count > 0;
            CanRedo = _redoStack.Count > 0;
            OnPropertyChanged(nameof(CanRestoreDefaults));
            RefreshRestoreAvailability();

            TrackedChanges.Clear();
            foreach (var c in _undoStack)
                TrackedChanges.Add(c);
        }

        private void PushChange(AffinityChange change)
        {
            _undoStack.Add(change);
            _redoStack.Clear();
            RefreshChangeState();
        }

        public Task LoadDeviceDetailsAsync(AffinityDeviceItem item)
        {
            if (!_affinityService.DeviceExists(item.DeviceInstanceId))
            {
                StatusText = $"{item.Name} is no longer present - the device was restarted or re-enumerated. Rescanning???";
                _ = RefreshDevicesCommand.ExecuteAsync(null);
                throw new InvalidOperationException(
                    $"{item.Name} is no longer present. It was restarted or re-enumerated (this happens after a GPU restart or driver install). " +
                    "The device list has been rescanned - pick the device again.");
            }

            SelectedDevice = item;
            var info = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
            item.MsiEnabled = info.MsiSupported ?? false;
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

            ulong fullMask = logical >= 64 ? ulong.MaxValue : ((1UL << logical) - 1);
            ulong effectiveMask = info.AffinityMask ?? fullMask;

            int coreIndex = 0;

            void MapCores(IEnumerable<ulong> masks)
            {
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

            ulong extractedSoFar = 0;
            foreach (ulong m in topology.PerformanceCoreMasks) extractedSoFar |= m;
            foreach (ulong m in topology.EfficiencyCoreMasks) extractedSoFar |= m;

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

        [RelayCommand]
        private async Task RefreshDevicesAsync()
        {
            if (IsRefreshing) return;
            IsRefreshing = true;
            StatusText = "Scanning for devices...";
            try
            {
                var devices = await _affinityService.EnumerateDevicesAsync();

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
                DeviceCount = total;
                MsiEnabledCount = devices.Count(d => d.MsiEnabled);
                StatusText = total == 0
                    ? "No tunable devices found."
                    : $"{total} device{(total == 1 ? "" : "s")} found."
                      + (wasPresent && SelectedDevice is null ? " The previously selected device is gone." : "");

                RefreshRestoreAvailability();
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

        public bool IsDevicePresent(AffinityDeviceItem? item)
            => item is not null && _affinityService.DeviceExists(item.DeviceInstanceId);

        public void ApplyDeviceChanges(AffinityDeviceItem item)
        {
            var info = _affinityService.GetInterruptInfo(item.DeviceInstanceId);

            bool oldMsi = info.MsiSupported ?? false;
            if (item.MsiEnabled != oldMsi)
            {
                if (_affinityService.SetMsiEnabled(item.DeviceInstanceId, item.MsiEnabled))
                    PushChange(new AffinityChange(item.DeviceInstanceId, item.Name, "MsiEnabled", oldMsi, item.MsiEnabled, DateTime.Now));
            }

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

            if (policyTouched && newPolicy != 4)
                _affinityService.ClearAffinityPolicy(item.DeviceInstanceId, "AssignmentSetOverride");

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

            ulong newMask = BuildMaskFromGroups(item);
            ulong oldMask = info.AffinityMask ?? 0;
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

            if ((maskTouched || policyTouched) && item.Category == "Network")
                _affinityService.SetRSS(item.DeviceInstanceId, newPolicy == 4 ? newMask : 0);

            var refreshed = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
            item.DevicePolicyShort = AffinityService.DevicePolicyShort(refreshed.DevicePolicy);
            item.DevicePriorityShort = refreshed.DevicePriority is null ? "-" : AffinityService.DevicePriorityName(refreshed.DevicePriority);
            item.AffinityText = AffinityService.AffinityMaskText(refreshed.AffinityMask);
            item.IrqText = refreshed.MsiSupported == true ? "MSI" : refreshed.MsiSupported == false ? "Line" : "-";

            ResyncGroupsFromMask(item, refreshed.AffinityMask);

            RefreshRestoreAvailability();
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

        private static ulong RenderedMask(AffinityDeviceItem item)
        {
            ulong mask = 0;
            foreach (var group in item.CoreGroups)
                foreach (var thread in group.Threads)
                    mask |= 1UL << thread.Index;
            return mask;
        }

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

        private async Task RestartAfterWriteAsync(IEnumerable<string> deviceIds, string whatWasDone)
        {
            var ids = deviceIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0)
            {
                await RefreshDevicesAsync();
                return;
            }

            StatusText = ids.Count == 1
                ? $"{whatWasDone} - restarting device..."
                : $"{whatWasDone} - restarting {ids.Count} devices...";

            await RestartDevicesQuiescedAsync(ids, "affinity apply");

            await RefreshDevicesAsync();
            StatusText = ids.Count == 1
                ? $"{whatWasDone} - device restarted."
                : $"{whatWasDone} - {ids.Count} devices restarted.";
        }

        public async Task RestartDeviceAfterApplyAsync(AffinityDeviceItem item)
        {
            string name = item?.Name ?? "device";
            await RestartAfterWriteAsync(new[] { item!.DeviceInstanceId }, $"Applied to {name}");
        }

        private static async Task RestartDevicesQuiescedAsync(
            IEnumerable<string> deviceIds, string reason)
        {
            using (HardwareQuiesceGate.Hold(reason))
            {
                foreach (var id in deviceIds)
                {
                    await AffinityService.RestartDeviceAsync(id);
                }
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

            await RestartAfterWriteAsync(new[] { change.DeviceId }, "Undo applied");
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

            await RestartAfterWriteAsync(new[] { change.DeviceId }, "Redo applied");
        }

        [RelayCommand]
        private async Task RestoreAsync()
        {
            if (!IsElevated())
            {
                StatusText = "Restore needs administrator rights - every one of these writes is under HKLM.";
                return;
            }
            if (_undoStack.Count == 0 && !CanRestoreDefaults) return;

            var all = AllDevices().ToList();
            if (all.Count == 0)
            {
                StatusText = "No devices to restore.";
                return;
            }

            StatusText = "Restoring defaults on every device...";

            var modifiedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = _undoStack.Count - 1; i >= 0; i--)
            {
                var change = _undoStack[i];
                RevertChange(change);
                modifiedIds.Add(change.DeviceId);
            }
            _undoStack.Clear();
            _redoStack.Clear();
            RefreshChangeState();

            int reset = 0, failed = 0;
            foreach (var item in all)
            {
                if (RestoreDeviceDefaults(item.DeviceInstanceId)) reset++;
                else failed++;
                modifiedIds.Add(item.DeviceInstanceId);
            }

            var toRestart = modifiedIds.ToList();

            string done = failed > 0
                ? $"Restored defaults on {reset} device(s); {failed} could not be written (access denied)."
                : $"Restored OS defaults on all {reset} device(s).";

            if (toRestart.Count > 0)
            {
                StatusText = toRestart.Count == 1
                    ? $"{done} Restarting device..."
                    : $"{done} Restarting {toRestart.Count} devices...";
                await RestartDevicesQuiescedAsync(toRestart, "restore defaults");
            }

            await RefreshDevicesAsync();
            StatusText = toRestart.Count switch
            {
                0 => done,
                1 => $"{done} Device restarted.",
                _ => $"{done} {toRestart.Count} devices restarted.",
            };
        }

        private IEnumerable<AffinityDeviceItem> AllDevices()
            => GraphicsDevices.Concat(NetworkDevices).Concat(UsbDevices).Concat(AudioDevices);

        private bool RestoreDeviceDefaults(string deviceInstanceId)
        {
            try
            {
                if (!_affinityService.SetDevicePolicy(deviceInstanceId, 0)) return false;
                if (!_affinityService.ClearAffinityPolicy(deviceInstanceId, "DevicePriority")) return false;
                if (!_affinityService.ClearAffinityPolicy(deviceInstanceId, "AssignmentSetOverride")) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool CanRestoreDefaults => HasNonDefaultDevices || _undoStack.Count > 0;

        private void RefreshRestoreAvailability()
        {
            try
            {
                foreach (var item in AllDevices())
                {
                    var info = _affinityService.GetInterruptInfo(item.DeviceInstanceId);
                    if (info.DevicePolicy is not null && info.DevicePolicy != 0) { HasNonDefaultDevices = true; return; }
                    if (info.DevicePriority is not null && info.DevicePriority != 0) { HasNonDefaultDevices = true; return; }
                    if (info.AffinityMask is not null) { HasNonDefaultDevices = true; return; }
                }
                HasNonDefaultDevices = false;
            }
            catch
            {
            }
        }

        public ObservableCollection<AffinityChange> TrackedChanges { get; } = new();

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

        private static int PriorityNameToInt(string name) => name switch
        {
            "Low" => 1,
            "Normal" => 2,
            "High" => 3,
            _ => 0
        };
    }
}

