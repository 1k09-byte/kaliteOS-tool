using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using kaliteConfig.ProcessOptimizer.Models;
using kaliteConfig.ProcessOptimizer.Services;

namespace kaliteConfig.ProcessOptimizer.ViewModels;

public class ProcessOptimizerViewModel : INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsActive => OptimizationSessionOrchestrator.Instance.CurrentState.IsActive;
    public string ActiveGameName => OptimizationSessionOrchestrator.Instance.CurrentState.ActiveGameName ?? "None";
    public string ProfileName => OptimizationSessionOrchestrator.Instance.CurrentState.ProfileName ?? "Default";
    public int ProcessesManaged => OptimizationSessionOrchestrator.Instance.CurrentState.ProcessesManaged;
    public int ProcessesThrottled => OptimizationSessionOrchestrator.Instance.CurrentState.ProcessesThrottled;
    public int ProcessesUnchanged => OptimizationSessionOrchestrator.Instance.CurrentState.ProcessesUnchanged;

    public ObservableCollection<ManagedProcessEntry> ManagedProcesses { get; } = new();

    public ProcessOptimizerViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        OptimizationSessionOrchestrator.Instance.StateChanged += OnStateChanged;
        RefreshState();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_dispatcher == null) return;
        _dispatcher.TryEnqueue(() => RefreshState());
    }

    private void RefreshState()
    {
        OnPropertyChanged(string.Empty);

        var entries = OptimizationSessionOrchestrator.Instance.GetManagedProcesses().ToList();

        ManagedProcesses.Clear();
        foreach (var entry in entries)
        {
            ManagedProcesses.Add(entry);
        }
    }

    public void RequestEndSession()
    {
        OptimizationSessionOrchestrator.Instance.EndSession();
    }

    public void Shutdown()
    {        OptimizationSessionOrchestrator.Instance.StateChanged -= OnStateChanged;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
