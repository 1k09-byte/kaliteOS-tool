using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.PackageManager.Models;

namespace kaliteConfig.PackageManager.Services;

/// <summary>
/// Serialized package-operation execution: installers love to reboot, lock
/// files, and pop their own UI, so operations run strictly one at a time
/// (bounded concurrency of one - a deliberate safety choice, not a
/// limitation). Bulk callers get per-item results and continue past
/// individual failures; nothing aborts-on-first-failure.
/// </summary>
public sealed class PackageOperationQueue
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private readonly Dictionary<PackageOperationStatus, CancellationTokenSource> _active = new();
    private readonly List<PackageOperationStatus> _history = new();
    private const int HistoryLimit = 50;

    /// <summary>Raised (any thread) when any operation record changes.</summary>
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

        // NOTE: no ConfigureAwait(false) anywhere in this method - every
        // continuation below touches PackageInfo bindings and must resume on
        // the UI thread (RPC_E_WRONG_THREAD otherwise). The backend itself
        // stays off-thread internally.
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

    /// <summary>Cancels a running operation (best-effort; backends observe the token).</summary>
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
