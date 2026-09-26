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
using kaliteConfig.Models;
using kaliteConfig.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace kaliteConfig.ViewModels;

public sealed partial class PowerPlansViewModel : ObservableObject
{
    private readonly PowerService _service = new();

    public ObservableCollection<PowerScheme> Schemes { get; } = new();

    [ObservableProperty]
    public partial PowerScheme? SelectedScheme { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }
    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }
    [ObservableProperty]
    public partial string LoadingStatus { get; set; } = "Loading…";

    public ObservableCollection<KernelTweakItem> KernelTweaks { get; } = new();
    private readonly WindowsSettingsService _kernelService = new();
    private bool _suppressKernelWrite;

    /// <summary>Marshals LoadingStatus updates onto the UI thread. Progress&lt;T&gt;
    /// captured no SynchronizationContext inside the async RelayCommand, so its
    /// callback fired on a worker thread and the cross-thread PropertyChanged
    /// crashed the app (COMException 0x8001010E). The DispatcherQueue is
    /// captured at construction - always on the UI thread - because
    /// GetForCurrentThread() from a worker returns null.</summary>
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

    private void ReportStatus(string status)
    {
        if (_uiDispatcher != null)
        {
            _uiDispatcher.TryEnqueue(() => LoadingStatus = status);
        }
        else
        {
            LoadingStatus = status;
        }
    }

    public PowerPlansViewModel()
    {
        // Do NOT touch HKLM here: the XAML DataContext instantiates this on the UI
        // thread, and an admin-only registry write would crash page navigation.
        // Unhide runs lazily inside LoadSchemesAsync on a background thread.
        try { _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(); }
        catch { _uiDispatcher = null; }
    }

    partial void OnSelectedSchemeChanged(PowerScheme? oldValue, PowerScheme? newValue)
    {
        if (oldValue != null)
        {
            oldValue.PropertyChanged -= Scheme_PropertyChanged;
            foreach (var sg in oldValue.Subgroups)
                foreach (var st in sg.Settings)
                    st.PropertyChanged -= Setting_PropertyChanged;
        }
        if (newValue != null)
        {
            newValue.PropertyChanged -= Scheme_PropertyChanged;
            newValue.PropertyChanged += Scheme_PropertyChanged;
            foreach (var sg in newValue.Subgroups)
                foreach (var st in sg.Settings)
                {
                    st.PropertyChanged -= Setting_PropertyChanged;
                    st.PropertyChanged += Setting_PropertyChanged;
                }
        }
    }

    private void Scheme_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is PowerScheme scheme)
        {
            if (e.PropertyName == nameof(PowerScheme.Name))
                _service.WritePlanName(scheme.Id, scheme.Name);
            else if (e.PropertyName == nameof(PowerScheme.Description))
                _service.WritePlanDescription(scheme.Id, scheme.Description);
        }
    }

    private void Setting_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not PowerSetting setting || SelectedScheme == null) return;
        try
        {
            var subgroup = SelectedScheme.Subgroups.FirstOrDefault(g => g.Settings.Contains(setting));
            if (subgroup == null) return;
            if (e.PropertyName == nameof(PowerSetting.AcValueIndex))
                _service.WriteACValue(SelectedScheme.Id, subgroup.Id, setting.Id, (uint)setting.AcValueIndex);
            else if (e.PropertyName == nameof(PowerSetting.DcValueIndex))
                _service.WriteDCValue(SelectedScheme.Id, subgroup.Id, setting.Id, (uint)setting.DcValueIndex);
        }
        catch { }
    }

    /// <summary>Manual trigger for the hidden-settings reveal. The load path
    /// also runs it, but only an admin token can write HKLM, so a visible
    /// button with an error surface beats a silent no-op.</summary>
    [RelayCommand]
    public async Task UnhideAllSettingsAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        LoadingStatus = "Unhiding all power settings…";
        try
        {
            await Task.Run(() => _service.UnhideAllSettings());
            LoadingStatus = "Enumerating power schemes…";
            var schemes = await Task.Run(() => _service.GetAllSchemes(new Progress<string>(s => ReportStatus(s))));
            Schemes.Clear();
            foreach (var s in schemes) Schemes.Add(s);
            if (SelectedScheme == null && Schemes.Count > 0)
                SelectedScheme = Schemes.FirstOrDefault(s => s.IsActive) ?? Schemes.First();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Unhide failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task LoadSchemesAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        LoadingStatus = "Loading power schemes…";
        try
        {
            // No auto-unhide: mass HKLM writes on every page load broke
            // laptop Control Panel (Modern Standby overlays). Unhide is
            // opt-in via the button (UnhideAllSettingsAsync).
            LoadingStatus = "Enumerating power schemes…";
            var status = new Progress<string>(s => ReportStatus(s));
            var schemes = await Task.Run(() => _service.GetAllSchemes(status));
            Schemes.Clear();
            foreach (var s in schemes) Schemes.Add(s);
            
            if (SelectedScheme == null && Schemes.Count > 0)
                SelectedScheme = Schemes.FirstOrDefault(s => s.IsActive) ?? Schemes.First();
            if (Schemes.Count == 0)
                ErrorMessage = "No power schemes found.";
            RefreshKernelTweaks();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to enumerate power schemes: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Creates a plan as a copy of <paramref name="baseId"/> with the given
    /// identity. Returns the new id, or null on failure (ErrorMessage is set).</summary>
    public async Task<Guid?> CreatePlanAsync(Guid baseId, string name, string description)
    {
        // Windows has no "empty plan" API - a new plan is a duplicate of a base.
        if (string.IsNullOrWhiteSpace(name)) name = "Custom plan";
        IsLoading = true;
        LoadingStatus = $"Creating \"{name}\"…";
        try
        {
            var newId = await Task.Run(() => _service.DuplicateScheme(baseId));
            await Task.Run(() =>
            {
                _service.WritePlanName(newId, name);
                _service.WritePlanDescription(newId, description ?? string.Empty);
            });
            IsLoading = false; // LoadSchemesAsync guards on IsLoading - release first
            await LoadSchemesAsync();
            SelectedScheme = Schemes.FirstOrDefault(s => s.Id == newId);
            return newId;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not create plan: {ex.Message}";
            return null;
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void RefreshKernelTweaks()
    {
        _suppressKernelWrite = true;
        try
        {
            foreach (var item in KernelTweaks)
                item.PropertyChanged -= KernelTweak_PropertyChanged;
            KernelTweaks.Clear();
            foreach (var def in WindowsSettingsService.All)
            {
                uint? raw = null;
                string loadError = string.Empty;
                try { raw = _kernelService.ReadRaw(def); }
                catch (Exception ex) { loadError = ex.Message; }
                var item = new KernelTweakItem
                {
                    Id = def.Id,
                    Name = def.Name,
                    Description = def.Description + (def.NeedsReboot ? " Takes effect after a restart." : "")
                        + (def.PerActiveScheme ? " Applies to the active power scheme." : ""),
                    IsOn = raw == def.OnValue,
                    StateText = loadError.Length > 0 ? $"Unreadable: {loadError}"
                        : raw == null ? "Not set (Windows default)"
                        : raw == def.OnValue ? "On"
                        : raw == def.OffValue ? "Off"
                        : $"Custom value ({raw})",
                };
                item.PropertyChanged += KernelTweak_PropertyChanged;
                KernelTweaks.Add(item);
            }
        }
        finally { _suppressKernelWrite = false; }
    }

    private void KernelTweak_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_suppressKernelWrite) return;
        if (sender is not KernelTweakItem item || e.PropertyName != nameof(KernelTweakItem.IsOn)) return;
        var def = WindowsSettingsService.Find(item.Id);
        if (def == null) return;
        try
        {
            _kernelService.Write(def, item.IsOn);
            item.StateText = item.IsOn ? "On" : "Off";
        }
        catch (Exception ex)
        {
            _suppressKernelWrite = true;
            try { item.IsOn = !item.IsOn; } finally { _suppressKernelWrite = false; }
            item.StateText = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ResetKernelTweak(KernelTweakItem? item)
    {
        if (item == null) return;
        var def = WindowsSettingsService.Find(item.Id);
        if (def == null) return;
        try
        {
            _kernelService.Reset(def);
            RefreshKernelTweaks();
        }
        catch (Exception ex)
        {
            item.StateText = $"Failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SetActiveScheme(PowerScheme? scheme)
    {
        if (scheme == null) return;
        try
        {
            _service.SetActiveScheme(scheme.Id);
        }
        catch (Exception ex)
        {
            // An unguarded native failure here took the whole app down the
            // moment the user pressed "Set as Active".
            ErrorMessage = $"Could not activate plan: {ex.Message}";
            return;
        }
        
        foreach (var s in Schemes)
            s.IsActive = (s == scheme);
        RefreshKernelTweaks(); // per-scheme values follow the active plan
    }
    
    [RelayCommand]
    private void DeleteScheme(PowerScheme? scheme)
    {
        if (scheme == null || scheme.IsActive) return; // Cannot delete active scheme

        try
        {
            _service.DeleteScheme(scheme.Id);
            Schemes.Remove(scheme);

            if (SelectedScheme == scheme)
                SelectedScheme = Schemes.FirstOrDefault();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Recovery for broken Control Panel
    /// ("power plan information isn't available"): restores Windows defaults.</summary>
    [RelayCommand]
    public async Task RestoreDefaultSchemesAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        LoadingStatus = "Restoring Windows default power plans…";
        try
        {
            await Task.Run(() => PowerService.RestoreDefaultSchemes());
            await LoadSchemesAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Restore failed: {ex.Message} (try running as administrator)";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DuplicateSchemeAsync(PowerScheme? scheme)
    {
        if (scheme == null) return;
        
        try
        {
            var newId = _service.DuplicateScheme(scheme.Id);
            await LoadSchemesAsync();
            SelectedScheme = Schemes.FirstOrDefault(s => s.Id == newId);
        }
        catch { }
    }

    [RelayCommand]
    private async Task ExportSchemeAsync(PowerScheme? scheme)
    {
        if (scheme == null) return;
        
        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker();
            if (App.MainWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.FileTypeChoices.Add("Power Plan", new[] { ".pow" });
            picker.SuggestedFileName = $"{scheme.Name}.pow";
            
            var file = await picker.PickSaveFileAsync();
            if (file != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg", $"/export \"{file.Path}\" {scheme.Id}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
            }
        }
        catch { }
    }

    [RelayCommand]
    private async Task ImportSchemeAsync()
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            if (App.MainWindow != null)
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            }
            picker.FileTypeFilter.Add(".pow");
            
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powercfg", $"/import \"{file.Path}\"") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                await LoadSchemesAsync(); // Refresh list to get the new guid
            }
        }
        catch { }
    }

    public void UpdateSettingAcValue(PowerSetting setting, uint value, PowerSubgroup subgroup)
    {
        if (SelectedScheme == null) return;
        _service.WriteACValue(SelectedScheme.Id, subgroup.Id, setting.Id, value);
        setting.AcValueIndex = value;
    }

    public void UpdateSettingDcValue(PowerSetting setting, uint value, PowerSubgroup subgroup)
    {
        if (SelectedScheme == null) return;
        _service.WriteDCValue(SelectedScheme.Id, subgroup.Id, setting.Id, value);
        setting.DcValueIndex = value;
    }
}
