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
        // Calling PropertyChanged with null or empty tells the UI every property has updated.
        OnPropertyChanged(string.Empty);

        var entries = OptimizationSessionOrchestrator.Instance.GetManagedProcesses().ToList();
        
        // Highly naive but safe and simple observable refresh
        ManagedProcesses.Clear();
        foreach (var entry in entries)
        {
            ManagedProcesses.Add(entry);
        }
    }

    public void RequestEndSession()
    {
        // Immediately kills the session (user clicked "End Session Now")
        OptimizationSessionOrchestrator.Instance.EndSession();
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
