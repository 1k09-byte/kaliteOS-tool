using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace kaliteConfig.ViewModels;

public sealed partial class Nvidia3DSettingsViewModel : ObservableObject
{
    private readonly Nvidia3DSettingsService _service = new();
    [ObservableProperty] private ObservableCollection<Nvidia3DSettingRowViewModel> rows = new();
    [ObservableProperty] private ObservableCollection<Nvidia3DApplication> applications = new();
    [ObservableProperty] private Nvidia3DApplication? selectedApplication;
    [ObservableProperty] private bool isProgram;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool hasChanges;
    [ObservableProperty] private string catalogNote = "Loading driver metadata…";

    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            var snapshot = await Task.Run(() =>
            {
                var driver = _service.EnumerateDriverSettings();
                var expected = Nvidia3DSettingsCatalog.Expected;
                var missing = Nvidia3DSettingsCatalog.Missing(driver);
                var extra = Nvidia3DSettingsCatalog.ExtraVisibleCandidates(driver);
                var apps = _service.EnumerateApplications();
                return (driver, expected, missing, extra, apps);
            });
            Applications = new ObservableCollection<Nvidia3DApplication>(snapshot.apps);
            CatalogNote = $"Driver-reported IDs: {snapshot.driver.Count}; curated IDs available: {snapshot.expected.Count - snapshot.missing.Count}/{snapshot.expected.Count}; missing: {string.Join(", ", snapshot.missing.Select(x => x.Name))}; other driver IDs require NVCP cross-check.";
            await LoadRowsAsync();
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    public async Task LoadRowsAsync()
    {
        if (IsProgram && SelectedApplication is null) { Rows.Clear(); return; }
        IsBusy = true;
        try
        {
            var data = await Task.Run(() => IsProgram ? _service.ReadProgram(SelectedApplication!.Executable) : _service.ReadGlobal());
            Rows = new ObservableCollection<Nvidia3DSettingRowViewModel>(data.Select(row => new Nvidia3DSettingRowViewModel(row, Stage)));
            HasChanges = false;
        }
        catch (Exception ex) { Status = ex.Message; }
        finally { IsBusy = false; }
    }

    private void Stage(Nvidia3DRow row, uint value)
    {
        _service.Stage(row.Id, value);
        HasChanges = true;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (!HasChanges) return;
        IsBusy = true;
        try
        {
            var result = IsProgram
                ? _service.ApplyProgram(SelectedApplication!.Executable, Rows.Select(r => r.Source))
                : _service.ApplyGlobal(Rows.Select(r => r.Source), restore: false);
            Status = result.Success ? result.Message : $"Apply failed: {result.Message}";
            if (result.Success) await LoadRowsAsync();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        if (IsProgram && SelectedApplication is null) return;
        IsBusy = true;
        try
        {
            var result = IsProgram
                ? _service.ApplyProgram(SelectedApplication!.Executable, Rows.Select(r => r.Source), restore: true)
                : _service.ApplyGlobal(Rows.Select(r => r.Source), restore: true);
            Status = result.Success ? result.Message : $"Restore failed: {result.Message}";
            if (result.Success) await LoadRowsAsync();
        }
        finally { IsBusy = false; }
    }

    partial void OnIsProgramChanged(bool value) => _ = LoadRowsAsync();
    partial void OnSelectedApplicationChanged(Nvidia3DApplication? value) { if (IsProgram) _ = LoadRowsAsync(); }
}

public sealed class Nvidia3DSettingRowViewModel : ObservableObject
{
    private readonly Action<Nvidia3DRow, uint> _stage;
    private uint _value;
    public Nvidia3DRow Source { get; }
    public string Name => Source.Name;
    public uint Id => Source.Id;
    public bool HasRecognizedOptions => Options.Any(option => option.Value != uint.MaxValue);
    public string CurrentValueText => Options.FirstOrDefault(option => option.Value == Value)?.Label ??
        (Source.IsInherited ? "Use global setting" : "NVIDIA default");
    public uint DefaultValue => Source.DefaultValue;
    public string DiffText => Source.DiffersFromDefault ? "Differs from default" : "Default";
    public ObservableCollection<Nvidia3DOption> Options { get; }
    public uint Value
    {
        get => _value;
        set
        {
            if (SetProperty(ref _value, value))
            {
                OnPropertyChanged(nameof(SelectedOption));
                OnPropertyChanged(nameof(CurrentValueText));
                _stage(Source, value);
            }
        }
    }
    public Nvidia3DOption? SelectedOption
    {
        get => Options.FirstOrDefault(option => option.Value == Value);
        set { if (value is not null && Value != value.Value) Value = value.Value; }
    }
    public Nvidia3DSettingRowViewModel(Nvidia3DRow row, Action<Nvidia3DRow, uint> stage)
    {
        Source = row; _stage = stage; _value = row.IsInherited ? uint.MaxValue : row.Value;
        Options = new ObservableCollection<Nvidia3DOption>(row.Options);
        if (row.IsInherited && !Options.Any(x => x.Value == uint.MaxValue)) Options.Insert(0, new Nvidia3DOption(uint.MaxValue, "Use global setting"));
    }
}
