// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Services;
using System;
using System.Collections.ObjectModel;

namespace kaliteConfig.ViewModels
{
    public partial class ReservedCpuSetsViewModel : ObservableObject
    {
        private readonly ReservedCpuSetsService _service = new();

        public ObservableCollection<CpuCoreItem> Cores { get; } = new();

        [ObservableProperty]
        public partial bool IsElevated { get; set; }
        public bool IsNotElevated => !IsElevated;
        

        [ObservableProperty]
        public partial bool HasError { get; set; }
        [ObservableProperty]
        public partial string ErrorText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasSuccess { get; set; }
        [ObservableProperty]
        public partial string SuccessText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasPendingChanges { get; set; }

        private ulong _savedMask;

        public ReservedCpuSetsViewModel()
        {
            IsElevated = _service.IsElevated();

            LoadCores();

            // Construction fires the change callbacks above; nothing is dirty yet.
            HasPendingChanges = false;
        }

        // Core ticks only stage changes - nothing is written until Apply is
        // clicked. The button enables only while the staged selection differs
        // from the saved mask, so reverting to the saved state greys it out.
        private void MarkDirty() => HasPendingChanges = CurrentMask() != VisibleSavedMask();

        private ulong CurrentMask()
        {
            ulong mask = 0;
            foreach (var core in Cores)
            {
                if (core.IsReserved)
                    mask |= (1UL << core.Index);
            }
            return mask;
        }

        private ulong VisibleSavedMask()
        {
            if (Cores.Count >= 64) return _savedMask;
            ulong visible = Cores.Count == 0 ? 0 : (1UL << Cores.Count) - 1;
            return _savedMask & visible;
        }

        private void LoadCores()
        {
            Cores.Clear();
            int processorCount = Environment.ProcessorCount;
            ulong? mask = _service.GetReservedCpuMask();
            ulong actualMask = mask ?? 0;
            _savedMask = actualMask;

            for (int i = 0; i < processorCount; i++)
            {
                bool isReserved = (actualMask & (1UL << i)) != 0;
                Cores.Add(new CpuCoreItem(i, isReserved, MarkDirty));
            }
        }

        [RelayCommand]
        private void Save()
        {
            HasError = false;
            HasSuccess = false;

            if (!IsElevated)
            {
                ShowError("Administrator permissions are required to save these changes. Please restart the application as Administrator.");
                return;
            }

            ulong newMask = 0;
            int reservedCount = 0;

            foreach (var core in Cores)
            {
                if (core.IsReserved)
                {
                    newMask |= (1UL << core.Index);
                    reservedCount++;
                }
            }

            if (reservedCount == Cores.Count)
            {
                ShowError("You cannot reserve all logical processors. At least one core must remain unreserved for the kernel scheduler to function.");
                return;
            }

            try
            {
                _service.SetReservedCpuMask(newMask);
                // Startup reapply is always on: the saved reservation is
                // re-asserted automatically when the app starts.
                _service.SetApplyAtStartup(true);
                _savedMask = newMask;
                HasPendingChanges = false;
                ShowSuccess("CPU reservations saved successfully. You must restart your computer for these changes to take effect.");
            }
            catch (Exception ex)
            {
                ShowError($"Failed to save reserved CPU sets: {ex.Message}");
            }
        }

        private void ShowError(string message)
        {
            HasError = true;
            ErrorText = message;
            HasSuccess = false;
        }

        private void ShowSuccess(string message)
        {
            HasSuccess = true;
            SuccessText = message;
            HasError = false;
        }
    }

    public partial class CpuCoreItem : ObservableObject
    {
        private readonly Action _onChanged;

        public int Index { get; }
        public string Name => $"CPU {Index}";

        [ObservableProperty]
        public partial bool IsReserved { get; set; }

        public CpuCoreItem(int index, bool isReserved, Action onChanged)
        {
            Index = index;
            IsReserved = isReserved; // Fires before _onChanged is attached
            _onChanged = onChanged;
        }


        partial void OnIsReservedChanged(bool value)
        {
            _onChanged?.Invoke();
        }
    }
}
