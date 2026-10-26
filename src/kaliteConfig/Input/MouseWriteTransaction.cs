using System;
using System.Threading.Tasks;

namespace kaliteConfig.Input;

public sealed class MouseWritePlan
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string Setting { get; set; } = string.Empty;
    public string Original { get; set; } = string.Empty;
    public string Requested { get; set; } = string.Empty;
    public bool StoredOnDevice { get; set; }
    public Func<Task<bool>>? Write { get; set; }
    public Func<Task<string?>>? ReadBack { get; set; }
    public Func<Task<bool>>? RestoreOriginal { get; set; }
}

public sealed class MouseWriteOutcome
{
    public MouseWriteOutcome(MouseResult result, string message)
    {
        Result = result;
        Message = message;
    }

    public MouseResult Result { get; }
    public string Message { get; }
}

public interface IMouseWriteTransaction
{
    Task<MouseWriteOutcome> RunAsync(MouseWritePlan plan);
}

public sealed class MouseWriteTransaction : IMouseWriteTransaction
{
    private readonly IMouseGuardPolicy _guard;
    private readonly IMouseSnapshotService _snapshots;
    private readonly IMouseLog _log;
    private readonly object _gate = new();
    private readonly System.Collections.Generic.HashSet<string> _busy = new(StringComparer.OrdinalIgnoreCase);

    public MouseWriteTransaction(IMouseGuardPolicy guard, IMouseSnapshotService snapshots, IMouseLog log)
    {
        _guard = guard;
        _snapshots = snapshots;
        _log = log;
    }

    public async Task<MouseWriteOutcome> RunAsync(MouseWritePlan plan)
    {
        if (plan.Write is null)
            return Fail(plan, MouseResult.NotImplemented, "Not implemented in this app: no verified write path for " + plan.Setting + ".");
        lock (_gate)
        {
            if (_busy.Contains(plan.DeviceId))
                return Fail(plan, MouseResult.Blocked, "Blocked by guard: another write is already running for this mouse.");
            _busy.Add(plan.DeviceId);
        }
        try
        {
            if (!_guard.RateAllows(plan.DeviceId))
                return Fail(plan, MouseResult.Blocked, "Blocked by guard: writes are rate-limited. Wait a few seconds and try again.");
            _snapshots.CaptureIfNeeded(plan.DeviceId, plan.Setting, plan.Original);
            bool written;
            try
            {
                written = await plan.Write().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail(plan, MouseResult.Refused, "The mouse refused the change (code " + HResultText(ex) + "). The original value was kept.");
            }
            if (!written)
                return Fail(plan, MouseResult.Refused, "The mouse refused the change. The original value was kept.");
            if (plan.StoredOnDevice) _guard.NoteOnboardWrite(plan.DeviceId);
            if (plan.ReadBack is null)
            {
                _log.Add(plan.DeviceName, plan.Setting, MouseResult.AppliedNoVerify,
                    "Set " + plan.Setting + " to " + plan.Requested + ". The device can't be read back.");
                return new MouseWriteOutcome(MouseResult.AppliedNoVerify,
                    "Applied (couldn't verify): " + plan.Setting + " set to " + plan.Requested + ".");
            }
            await Task.Delay(350).ConfigureAwait(false);
            string? current = null;
            try
            {
                current = await plan.ReadBack().ConfigureAwait(false);
            }
            catch
            {
                current = null;
            }
            if (current is null)
            {
                _log.Add(plan.DeviceName, plan.Setting, MouseResult.AppliedNoVerify,
                    "Set " + plan.Setting + " to " + plan.Requested + ". Read back failed.");
                return new MouseWriteOutcome(MouseResult.AppliedNoVerify,
                    "Applied (couldn't verify): " + plan.Setting + " set to " + plan.Requested + ", but it can't be read back.");
            }
            if (string.Equals(current.Trim(), plan.Requested.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                _log.Add(plan.DeviceName, plan.Setting, MouseResult.Verified,
                    "Set " + plan.Setting + " to " + plan.Requested + ". Read back: " + current + ".");
                return new MouseWriteOutcome(MouseResult.Verified,
                    "Verified: " + plan.Setting + " is now " + current + ".");
            }
            string restoreNote = string.Empty;
            if (plan.RestoreOriginal != null)
            {
                try
                {
                    if (await plan.RestoreOriginal().ConfigureAwait(false))
                        restoreNote = " The original value was restored and confirmed.";
                    else
                        restoreNote = " The original value could not be restored.";
                }
                catch
                {
                    restoreNote = " The original value could not be restored.";
                }
            }
            _log.Add(plan.DeviceName, plan.Setting, MouseResult.Reverted,
                "Set " + plan.Setting + " to " + plan.Requested + ". Read back: " + current + "." + restoreNote);
            return new MouseWriteOutcome(MouseResult.Reverted,
                "Set " + plan.Setting + " to " + plan.Requested + ". Read back: " + current + "." + restoreNote);
        }
        finally
        {
            lock (_gate) _busy.Remove(plan.DeviceId);
        }
    }

    private MouseWriteOutcome Fail(MouseWritePlan plan, MouseResult result, string message)
    {
        _log.Add(plan.DeviceName, plan.Setting, result, message);
        return new MouseWriteOutcome(result, message);
    }

    private static string HResultText(Exception ex)
    {
        try { return "0x" + ex.HResult.ToString("X8"); }
        catch { return "unknown"; }
    }
}
