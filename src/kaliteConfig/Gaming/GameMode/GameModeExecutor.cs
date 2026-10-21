using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using kaliteConfig.Native;

namespace kaliteConfig.Gaming.GameMode;

public sealed class GameModeExecutor
{
    private readonly GameModeJournal _journal;
    private readonly GameModeLog _log;
    private readonly object _actionGate = new();

    public GameModeExecutor(GameModeJournal journal, GameModeLog log)
    {
        _journal = journal;
        _log = log;
    }

    public GameModeJournal Journal => _journal;

    public BackgroundDecision Apply(
        ProcessFact fact,
        BackgroundAction requested,
        GameModeOutcome initial,
        BackgroundAction planned,
        GameModeSettings settings)
    {
        var decision = new BackgroundDecision(fact.Pid, fact.CreationTicks, fact.Name, fact.ImagePath,
            BackgroundCategory.UserAdded, string.Empty, requested, planned, initial,
            planned == requested ? "Planned" : "Downgraded to a safer action");

        if (initial == GameModeOutcome.SkippedProtected || initial == GameModeOutcome.SkippedSafety || initial == GameModeOutcome.Refused)
            return decision;

        if (!SameInstance(fact.Pid, fact.CreationTicks))
            return decision with { Outcome = GameModeOutcome.SkippedSafety, Message = "Process exited before the action ran" };

        lock (_actionGate)
        {
            if (planned == BackgroundAction.None)
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Left alone by its rule" };

            if (planned == BackgroundAction.ConfineCores)
            {
                return decision with
                {
                    Outcome = GameModeOutcome.Downgraded,
                    Planned = BackgroundAction.Efficiency,
                    Message = "Downgraded (reserved-core confinement is not enabled on this build)",
                };
            }

            if (planned == BackgroundAction.Suspend)
            {
                return decision with
                {
                    Outcome = GameModeOutcome.Downgraded,
                    Planned = BackgroundAction.Efficiency,
                    Message = "Downgraded (suspend is disabled on this build; audio and anti-cheat safety cannot be verified here)",
                };
            }

            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation
                | NativeMethods.ProcessAccess.SetInformation,
                false, (uint)fact.Pid);

            if (handle.IsInvalid)
            {
                var denied = new BackgroundDecision(fact.Pid, fact.CreationTicks, fact.Name, fact.ImagePath,
                    decision.Category, decision.CatalogId, requested, planned,
                    GameModeOutcome.Refused, "Refused (access denied)");
                return denied with { Planned = BackgroundAction.None };
            }

            IntPtr raw = handle.DangerousGetHandle();
            if (planned == BackgroundAction.LowerPriority) return ApplyPriority(fact, decision, raw);
            return ApplyEfficiency(fact, decision, raw);
        }
    }

    private BackgroundDecision ApplyPriority(ProcessFact fact, BackgroundDecision decision, IntPtr handle)
    {
        if (!NativeMethods.GameMode.GetPriorityClass(handle, out uint original))
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (could not read the current priority)" };

        var entry = NewEntry(fact, decision.Planned, original, 0, 0, 0);
        _journal.Add(entry);

        if (!NativeMethods.GameMode.SetPriorityClass(handle, NativeMethods.GameMode.BelowNormalPriorityClass))
        {
            _journal.Remove(fact.Pid, fact.CreationTicks);
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (Windows denied the priority change)" };
        }

        if (!NativeMethods.GameMode.GetPriorityClass(handle, out uint current))
        {
            _journal.Remove(fact.Pid, fact.CreationTicks);
            TryRestorePriority(handle, original);
            return decision with { Outcome = GameModeOutcome.AppliedUnverified, Planned = BackgroundAction.None, Message = "Applied (couldn't verify) and reverted" };
        }

        if (current == NativeMethods.GameMode.BelowNormalPriorityClass)
            return decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Lower priority, verified" };

        _journal.Remove(fact.Pid, fact.CreationTicks);
        TryRestorePriority(handle, original);
        return decision with { Outcome = GameModeOutcome.RestoreFailed, Planned = BackgroundAction.None, Message = "Priority did not stick, reverted" };
    }

    private BackgroundDecision ApplyEfficiency(ProcessFact fact, BackgroundDecision decision, IntPtr handle)
    {
        var original = new NativeMethods.GameMode.PowerThrottlingState { Version = 1 };
        if (!NativeMethods.GameMode.GetProcessInformation(handle, NativeMethods.GameMode.ProcessPowerThrottling, ref original,
                Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>()))
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (efficiency mode not supported here)" };

        var entry = NewEntry(fact, decision.Planned, 0, original.Version, original.ControlMask, original.StateMask);
        _journal.Add(entry);

        var target = new NativeMethods.GameMode.PowerThrottlingState
        {
            Version = 1,
            ControlMask = NativeMethods.GameMode.PowerThrottlingExecutionSpeed,
            StateMask = NativeMethods.GameMode.PowerThrottlingExecutionSpeed,
        };

        if (!NativeMethods.GameMode.SetProcessInformation(handle, NativeMethods.GameMode.ProcessPowerThrottling, ref target,
                Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>()))
        {
            _journal.Remove(fact.Pid, fact.CreationTicks);
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (Windows denied efficiency mode)" };
        }

        var check = new NativeMethods.GameMode.PowerThrottlingState { Version = 1 };
        bool verified = NativeMethods.GameMode.GetProcessInformation(handle, NativeMethods.GameMode.ProcessPowerThrottling, ref check,
            Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>())
            && (check.StateMask & NativeMethods.GameMode.PowerThrottlingExecutionSpeed) != 0;

        if (verified)
            return decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Efficiency mode, verified" };

        _journal.Remove(fact.Pid, fact.CreationTicks);
        TryRestoreThrottling(handle, original);
        return decision with { Outcome = GameModeOutcome.AppliedUnverified, Planned = BackgroundAction.None, Message = "Efficiency mode applied but not verified, reverted" };
    }

    private static GameModeJournalEntry NewEntry(ProcessFact fact, BackgroundAction action, uint priority, uint version, uint control, uint state) => new()
    {
        Pid = fact.Pid,
        CreationTicks = fact.CreationTicks,
        Name = fact.Name,
        ImagePath = fact.ImagePath ?? string.Empty,
        Action = action.ToString(),
        OriginalPriorityClass = priority,
        ThrottlingVersion = version,
        ThrottlingControlMask = control,
        ThrottlingStateMask = state,
    };

    public GameModeRestoreSummary RestoreAll(string reason)
    {
        int restored = 0;
        int failed = 0;
        foreach (var entry in _journal.Snapshot())
        {
            if (RestoreEntry(entry, out string message)) restored++;
            else
            {
                failed++;
                _log.Add(entry.Name, "Game Mode", "Restore", "Could not restore", message);
            }
        }
        if (restored > 0)
            _log.Add("-", "Game Mode", "Restore", "Restored and verified", $"Restored {restored} process(es): {reason}");
        return new GameModeRestoreSummary(restored, failed);
    }

    public bool RestoreEntry(GameModeJournalEntry entry, out string message)
    {
        message = string.Empty;
        if (!SameInstance(entry.Pid, entry.CreationTicks))
        {
            _journal.Remove(entry.Pid, entry.CreationTicks);
            message = $"{entry.Name} is no longer running; nothing to restore";
            return true;
        }

        using var handle = NativeMethods.Handles.OpenProcess(
            NativeMethods.ProcessAccess.QueryLimitedInformation
            | NativeMethods.ProcessAccess.SetInformation,
            false, (uint)entry.Pid);

        if (handle.IsInvalid)
        {
            message = $"{entry.Name}: could not restore (access denied)";
            return false;
        }

        IntPtr raw = handle.DangerousGetHandle();
        bool ok = true;

        if (entry.Action == nameof(BackgroundAction.LowerPriority))
        {
            ok &= NativeMethods.GameMode.SetPriorityClass(raw, entry.OriginalPriorityClass);
            if (ok)
            {
                ok &= NativeMethods.GameMode.GetPriorityClass(raw, out uint check);
                ok &= !ok || check == entry.OriginalPriorityClass;
            }
        }
        else if (entry.Action == nameof(BackgroundAction.Efficiency))
        {
            var original = new NativeMethods.GameMode.PowerThrottlingState
            {
                Version = entry.ThrottlingVersion == 0 ? 1 : entry.ThrottlingVersion,
                ControlMask = entry.ThrottlingControlMask,
                StateMask = entry.ThrottlingStateMask,
            };
            ok &= TryRestoreThrottling(raw, original);
        }

        if (ok)
        {
            _journal.Remove(entry.Pid, entry.CreationTicks);
            message = $"{entry.Name}: original state restored";
            return true;
        }

        message = $"{entry.Name}: could not restore, kept in the journal";
        return false;
    }

    private static bool TryRestorePriority(IntPtr handle, uint original) =>
        NativeMethods.GameMode.SetPriorityClass(handle, original);

    private static bool TryRestoreThrottling(IntPtr handle, NativeMethods.GameMode.PowerThrottlingState original)
    {
        var state = original;
        if (!NativeMethods.GameMode.SetProcessInformation(handle, NativeMethods.GameMode.ProcessPowerThrottling, ref state,
                Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>()))
            return false;
        var check = new NativeMethods.GameMode.PowerThrottlingState { Version = state.Version };
        if (!NativeMethods.GameMode.GetProcessInformation(handle, NativeMethods.GameMode.ProcessPowerThrottling, ref check,
                Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>()))
            return false;
        return check.ControlMask == original.ControlMask && check.StateMask == original.StateMask;
    }

    public static bool SameInstance(int pid, long ticks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.Ticks == ticks;
        }
        catch
        {
            return false;
        }
    }

    public static bool NtSuspendAvailable()
    {
        try
        {
            return NativeMethods.GameMode.NtSuspendProcess(IntPtr.Zero) == -1073741816
                || NativeMethods.GameMode.NtSuspendProcess(IntPtr.Zero) != 0;
        }
        catch
        {
            return false;
        }
    }
}
