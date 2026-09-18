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
        public partial bool RequiresPerBootReapply { get; set; }
        [ObservableProperty]
        public partial bool ApplyAtStartup { get; set; }
        
        [ObservableProperty]
        public partial bool HasError { get; set; }
        [ObservableProperty]
        public partial string ErrorText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool HasSuccess { get; set; }
        [ObservableProperty]
        public partial string SuccessText { get; set; } = string.Empty;

        public ReservedCpuSetsViewModel()
        {
            IsElevated = _service.IsElevated();
            RequiresPerBootReapply = _service.RequiresPerBootReapply();
            ApplyAtStartup = _service.GetApplyAtStartup();
            
            LoadCores();
        }

        private bool _isBulkUpdating;

        private void OnCoreSelectionChanged()
        {
            if (!_isBulkUpdating) Save();
        }

        private void LoadCores()
        {
            Cores.Clear();
            int processorCount = Environment.ProcessorCount;
            ulong? mask = _service.GetReservedCpuMask();
            ulong actualMask = mask ?? 0;

            for (int i = 0; i < processorCount; i++)
            {
                bool isReserved = (actualMask & (1UL << i)) != 0;
                Cores.Add(new CpuCoreItem(i, isReserved, OnCoreSelectionChanged));
            }
        }

        partial void OnApplyAtStartupChanged(bool value)
        {
            if (IsElevated && RequiresPerBootReapply)
            {
                try
                {
                    _service.SetApplyAtStartup(value);
                }
                catch (Exception ex)
                {
                    ShowError($"Failed to set startup key: {ex.Message}");
                }
            }
        }

        [RelayCommand]
        private void SelectAll()
        {
            _isBulkUpdating = true;
            foreach (var core in Cores)
            {
                core.IsReserved = true;
            }
            _isBulkUpdating = false;
            Save();
        }

        [RelayCommand]
        private void SelectNone()
        {
            _isBulkUpdating = true;
            foreach (var core in Cores)
            {
                core.IsReserved = false;
            }
            _isBulkUpdating = false;
            Save();
        }

        [RelayCommand]
        private void InvertSelection()
        {
            _isBulkUpdating = true;
            foreach (var core in Cores)
            {
                core.IsReserved = !core.IsReserved;
            }
            _isBulkUpdating = false;
            Save();
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
