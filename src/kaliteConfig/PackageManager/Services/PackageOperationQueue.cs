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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

public sealed class PackageOperationQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<PackageOperationStatus, CancellationTokenSource> _active = new();
    private readonly List<PackageOperationStatus> _history = new();
    private const int HistoryLimit = 50;

    public event Action<PackageOperationStatus>? OperationChanged;

    public IReadOnlyList<PackageOperationStatus> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    public async Task<PackageOperationResult> EnqueueAsync(
        string kind,
        PackageInfo package,
        Func<IProgress<PackageOperationProgress>, CancellationToken, Task<PackageOperationResult>> run,
        CancellationToken ct = default)
    {
        var record = new PackageOperationStatus
        {
            Kind = kind,
            PackageId = package.Id,
            DisplayName = package.DisplayName,
        };
        var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock) _active[record] = opCts;
        Publish(record);

        await _gate.WaitAsync(ct);
        try
        {
            return await ExecuteAsync(record, package, run, opCts.Token);
        }
        finally
        {
            _gate.Release();
            lock (_lock) _active.Remove(record);
        }
    }

    private async Task<PackageOperationResult> ExecuteAsync(
        PackageOperationStatus record,
        PackageInfo package,
        Func<IProgress<PackageOperationProgress>, CancellationToken, Task<PackageOperationResult>> run,
        CancellationToken ct)
    {
        SetState(record, PackageOperationState.Running, "Starting…", package);
        var progress = new Progress<PackageOperationProgress>(p =>
        {
            record.Percent = p.Percent;
            SetState(record, PackageOperationState.Running, p.Message, package, raiseOnly: true);
        });
        PackageOperationResult result;
        try
        {
            result = await run(progress, ct);
        }
        catch (OperationCanceledException)
        {
            result = PackageOperationResult.Fail("Cancelled.");
        }
        catch (Exception ex)
        {
            result = PackageOperationResult.Fail(ex.Message);
        }
        SetState(record,
            ct.IsCancellationRequested && !result.Success ? PackageOperationState.Cancelled
            : result.Success ? PackageOperationState.Succeeded : PackageOperationState.Failed,
            result.Message, package);
        lock (_lock)
        {
            _history.Add(record);
            while (_history.Count > HistoryLimit) _history.RemoveAt(0);
        }
        return result;
    }

    private void SetState(
        PackageOperationStatus record, PackageOperationState state, string message,
        PackageInfo package, bool raiseOnly = false)
    {
        record.State = state;
        record.Message = message;
        record.UpdatedAt = DateTime.Now;
        if (!raiseOnly && (state == PackageOperationState.Running || state == PackageOperationState.Queued))
            package.ActiveOperation = record;
        else if (state is PackageOperationState.Succeeded or PackageOperationState.Failed or PackageOperationState.Cancelled)
        {
            if (ReferenceEquals(package.ActiveOperation, record))
                package.ActiveOperation = null;
        }
        Publish(record);
    }

    private void Publish(PackageOperationStatus record)
    {
        try { OperationChanged?.Invoke(record); } catch { }
    }

    public void Cancel(PackageOperationStatus record)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(record, out var cts))
            {
                try { cts.Cancel(); } catch { }
            }
        }
    }
}
