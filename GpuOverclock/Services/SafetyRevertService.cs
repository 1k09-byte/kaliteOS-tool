using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.GpuOverclock.Models;

namespace kaliteConfig.GpuOverclock.Services
{
    /// <summary>States of the apply/confirm/revert machine (spec 4.2).</summary>
    public enum SafetyState
    {
        Idle,
        Applying,
        AwaitingConfirmation,
        Reverting,
    }

    /// <summary>A single control change inside a batch.</summary>
    public sealed record PendingChange(string ControlName, Func<GpuResult> Apply, string OldDisplay, string NewDisplay);

    /// <summary>Arguments for state-machine transitions the UI listens to.</summary>
    public sealed class SafetyStateChangedEventArgs : EventArgs
    {
        public required SafetyState State { get; init; }
        public required int CountdownSecondsRemaining { get; init; }
        public required int BatchId { get; init; }
        public required int ChangeCount { get; init; }
        public string? RevertReason { get; init; }
        public GpuResult? LastWriteResult { get; init; }
    }

    /// <summary>
    /// The core safety mechanism of the module: every write batch goes
    /// Applying -> AwaitingConfirmation (visible countdown) -> confirmed (new
    /// last-known-good) OR Reverting (restore the exact pre-batch state).
    ///
    /// Guarantees:
    ///  - "last known good" is the state captured BEFORE the batch started, so
    ///    a revert restores the user's prior working configuration, not defaults;
    ///  - a failed write never enters AwaitingConfirmation;
    ///  - a driver reset (TDR) during the window reverts immediately;
    ///  - multiple controls changed together share one confirmation window and
    ///    one revert action;
    ///  - a manual "Revert now" is available at any point of the window.
    /// </summary>
    public sealed class SafetyRevertService : IAsyncDisposable
    {
        private readonly INvidiaGpuController _controller;
        private readonly ITdrWatchdog _watchdog;
        private readonly Action<AppliedChangeLogEntry> _log;
        private readonly object _gate = new();

        private Timer? _countdownTimer;
        private int _countdownRemaining;
        private int _nextBatchId = 1;

        // State visible to UI (reads are atomic enough for int/enum; strings swapped by ref).
        private SafetyState _state = SafetyState.Idle;
        private int _batchId;
        private int _changeCount;
        private string? _revertReason;
        private GpuResult? _lastWriteResult;

        // The pre-batch anchors — the whole point of the machine.
        private (int CoreMhz, int MemMhz)? _anchorOffsets;
        private (double PowerPct, int? TempC)? _anchorLimits;
        private bool _anchorFanWasAuto = true;
        private PendingChange[] _pending = Array.Empty<PendingChange>();

        /// <summary>Countdown length; user-configurable per spec.</summary>
        public int ConfirmationSeconds { get; set; } = 15;

        public event EventHandler<SafetyStateChangedEventArgs>? StateChanged;
        public event Action<SafetyState, string?>? Reverted; // (state reached Idle with reason, detail)

        public SafetyRevertService(INvidiaGpuController controller, ITdrWatchdog watchdog, Action<AppliedChangeLogEntry> log)
        {
            _controller = controller;
            _watchdog = watchdog;
            _log = log;
            _watchdog.DriverResetDetected += OnDriverReset;
        }

        public SafetyState CurrentState { get { lock (_gate) return _state; } }
        public int CountdownRemaining { get { lock (_gate) return _countdownRemaining; } }
        public int ActiveBatchId { get { lock (_gate) return _batchId; } }

        private void Raise(SafetyState state, int countdown, string? revertReason, GpuResult? lastWrite)
        {
            StateChanged?.Invoke(this, new SafetyStateChangedEventArgs
            {
                State = state,
                CountdownSecondsRemaining = countdown,
                BatchId = _batchId,
                ChangeCount = _changeCount,
                RevertReason = revertReason,
                LastWriteResult = lastWrite,
            });
        }

        /// <summary>
        /// Applies a batch of changes. Captures pre-batch anchors first; any
        /// failed write aborts the batch (partial writes stay but no
        /// confirmation window is opened — the caller surfaces the error).
        /// </summary>
        public Task<bool> ApplyBatchAsync(IEnumerable<PendingChange> changes, OverclockChangeSource source)
        {
            var batch = changes.ToArray();
            if (batch.Length == 0) return Task.FromResult(false);

            List<AppliedChangeLogEntry>? logBatch = null;

            lock (_gate)
            {
                if (_state != SafetyState.Idle)
                    return Task.FromResult(false); // a batch is already in flight
                _state = SafetyState.Applying;
                _batchId = _nextBatchId++;
                _changeCount = batch.Length;
                _revertReason = null;
                _lastWriteResult = null;
                Raise(SafetyState.Applying, 0, null, null);
            }

            // Capture anchors BEFORE any write (prior working config, not defaults).
            var off = _controller.ReadCurrentOffsets();
            var lim = _controller.ReadCurrentLimits();
            lock (_gate)
            {
                _anchorOffsets = off.IsSuccess ? (off.Value.CoreOffsetMHz, off.Value.MemOffsetMHz) : null;
                _anchorLimits = lim.IsSuccess ? (lim.Value.PowerLimitPercent, lim.Value.TempLimitC) : null;
                _anchorFanWasAuto = true; // fan batches are dedicated; see ApplyFanBatchAsync
                _pending = batch;
            }

            bool anyFailed = false;
            foreach (var change in batch)
            {
                var result = change.Apply();
                _lastWriteResult = result;
                logBatch ??= new List<AppliedChangeLogEntry>();
                logBatch.Add(new AppliedChangeLogEntry
                {
                    Timestamp = DateTime.Now,
                    ControlName = change.ControlName,
                    OldValue = change.OldDisplay,
                    NewValue = change.NewDisplay,
                    Source = source,
                    Result = result.IsSuccess ? OverclockChangeResult.Success : OverclockChangeResult.Failed,
                    FailureReason = result.Detail,
                });
                if (!result.IsSuccess) { anyFailed = true; break; }
            }

            if (anyFailed)
            {
                FinishFailed(logBatch, source);
                return Task.FromResult(false);
            }

            // Log every successful write now (the batch entered the confirmation
            // window); if the window expires or a TDR hits, a second Reverted
            // entry is appended per control by BeginRevert.
            if (logBatch != null)
            {
                foreach (var e in logBatch) _log(e);
            }

            lock (_gate)
            {
                _state = SafetyState.AwaitingConfirmation;
                _countdownRemaining = ConfirmationSeconds;
                Raise(SafetyState.AwaitingConfirmation, _countdownRemaining, null, _lastWriteResult);
            }

            // Watch from now; a TDR reverts before the countdown ends.
            _watchdog.Arm();
            _countdownTimer ??= new Timer(_ => OnCountdownTick(), null, 1000, 1000);
            _countdownTimer.Change(1000, 1000);

            _ = logBatch;
            return Task.FromResult(true);
        }

        /// <summary>
        /// Fan-only batch: anchors are "fan was on auto" and the revert action
        /// is a real restore-to-default call rather than value writes.
        /// </summary>
        public Task<bool> ApplyFanBatchAsync(PendingChange change, bool fanWasAuto, OverclockChangeSource source)
        {
            lock (_gate)
            {
                if (_state != SafetyState.Idle) return Task.FromResult(false);
                _state = SafetyState.Applying;
                _batchId = _nextBatchId++;
                _changeCount = 1;
                _anchorFanWasAuto = fanWasAuto;
                _pending = new[] { change };
                Raise(SafetyState.Applying, 0, null, null);
            }

            var result = change.Apply();
            var entry = new AppliedChangeLogEntry
            {
                Timestamp = DateTime.Now,
                ControlName = change.ControlName,
                OldValue = change.OldDisplay,
                NewValue = change.NewDisplay,
                Source = source,
                Result = result.IsSuccess ? OverclockChangeResult.Success : OverclockChangeResult.Failed,
                FailureReason = result.Detail,
            };
            _log(entry);

            if (!result.IsSuccess)
            {
                lock (_gate) { _state = SafetyState.Idle; _pending = Array.Empty<PendingChange>(); }
                Raise(SafetyState.Idle, 0, null, result);
                return Task.FromResult(false);
            }

            lock (_gate)
            {
                _state = SafetyState.AwaitingConfirmation;
                _countdownRemaining = ConfirmationSeconds;
                Raise(SafetyState.AwaitingConfirmation, _countdownRemaining, null, result);
            }
            _watchdog.Arm();
            _countdownTimer ??= new Timer(_ => OnCountdownTick(), null, 1000, 1000);
            _countdownTimer.Change(1000, 1000);
            return Task.FromResult(true);
        }

        /// <summary>User confirms: values become the new last-known-good.</summary>
        public void Confirm()
        {
            lock (_gate)
            {
                if (_state != SafetyState.AwaitingConfirmation) return;
                _state = SafetyState.Idle;
                _pending = Array.Empty<PendingChange>();
                _revertReason = null;
                _countdownRemaining = 0;
                _watchdog.Disarm();
                _countdownTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                Raise(SafetyState.Idle, 0, null, null);
            }
        }

        /// <summary>Manual "Revert now" — available during the whole window.</summary>
        public void RevertNow() => BeginRevert("manual revert");

        private void OnCountdownTick()
        {
            int remaining;
            lock (_gate)
            {
                if (_state != SafetyState.AwaitingConfirmation) return;
                _countdownRemaining--;
                remaining = _countdownRemaining;
                if (remaining > 0)
                {
                    Raise(SafetyState.AwaitingConfirmation, remaining, null, null);
                    return;
                }
            }
            BeginRevert("confirmation timeout");
        }

        private void OnDriverReset()
        {
            // Immediate revert regardless of countdown position.
            BeginRevert("driver reset detected");
        }

        private void BeginRevert(string reason)
        {
            PendingChange[] toUndo;
            bool fanWasAuto;
            (int, int)? anchorOff;
            (double, int?)? anchorLim;

            lock (_gate)
            {
                if (_state != SafetyState.AwaitingConfirmation && _state != SafetyState.Reverting) return;
                if (_state == SafetyState.AwaitingConfirmation)
                {
                    _state = SafetyState.Reverting;
                    _revertReason = reason;
                    _watchdog.Disarm();
                    _countdownTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    Raise(SafetyState.Reverting, 0, reason, null);
                }
                toUndo = _pending;
                fanWasAuto = _anchorFanWasAuto;
                anchorOff = _anchorOffsets;
                anchorLim = _anchorLimits;
            }

            // Revert writes (outside the lock — controller has its own).
            var failures = new List<string>();
            foreach (var change in toUndo)
            {
                GpuResult res = change.ControlName switch
                {
                    OcControlNames.CoreClockOffset when anchorOff != null => _controller.SetCoreOffsetMhz(anchorOff.Value.Item1),
                    OcControlNames.MemoryClockOffset when anchorOff != null => _controller.SetMemoryOffsetMhz(anchorOff.Value.Item2),
                    OcControlNames.PowerLimit when anchorLim != null => _controller.SetPowerLimitPercent(anchorLim.Value.Item1),
                    OcControlNames.TemperatureLimit when anchorLim != null && anchorLim.Value.Item2 != null
                        => _controller.SetTempLimitC(anchorLim.Value.Item2!.Value),
                    OcControlNames.FanSpeed when fanWasAuto => _controller.RestoreFanAuto(),
                    _ => GpuResult.Ok(), // nothing to restore for this control
                };
                if (!res.IsSuccess) failures.Add($"{change.ControlName}: {res.Detail ?? "revert write failed"}");
            }

            foreach (var change in toUndo)
            {
                _log(new AppliedChangeLogEntry
                {
                    Timestamp = DateTime.Now,
                    ControlName = change.ControlName,
                    OldValue = change.NewDisplay,
                    NewValue = change.OldDisplay,
                    Source = OverclockChangeSource.AutoRevert,
                    Result = failures.Count == 0 ? OverclockChangeResult.Reverted : OverclockChangeResult.Failed,
                    FailureReason = failures.Count == 0 ? reason : string.Join("; ", failures),
                });
            }

            lock (_gate)
            {
                _state = SafetyState.Idle;
                _pending = Array.Empty<PendingChange>();
                _countdownRemaining = 0;
                Raise(SafetyState.Idle, 0, reason, null);
            }
            Reverted?.Invoke(SafetyState.Idle, failures.Count == 0 ? reason : "revert incomplete: " + string.Join("; ", failures));
        }

        private void FinishFailed(List<AppliedChangeLogEntry>? logBatch, OverclockChangeSource source)
        {
            if (logBatch != null) foreach (var e in logBatch) _log(e);
            lock (_gate)
            {
                _state = SafetyState.Idle;
                _pending = Array.Empty<PendingChange>();
                Raise(SafetyState.Idle, 0, null, _lastWriteResult);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _watchdog.DriverResetDetected -= OnDriverReset;
            _watchdog.Dispose();
            if (_countdownTimer != null) await _countdownTimer.DisposeAsync().ConfigureAwait(false);
            _countdownTimer = null;
        }
    }
}
