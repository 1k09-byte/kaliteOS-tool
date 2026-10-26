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
        GameModeSettings settings,
        GameModeCpuPlan? cpuPlan = null,
        IReadOnlyList<uint>? isolateIds = null)
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

            using var isolateHandle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation
                | NativeMethods.ProcessAccess.SetInformation,
                false, (uint)fact.Pid);
            if (planned == BackgroundAction.ConfineCores)
            {
                if (isolateHandle.IsInvalid)
                    return decision with { Outcome = GameModeOutcome.Refused, Message = "Refused (access denied)" };
                if (cpuPlan is not { Available: true } || isolateIds is not { Count: > 0 })
                {
                    return decision with
                    {
                        Outcome = GameModeOutcome.Downgraded,
                        Planned = BackgroundAction.Efficiency,
                        Message = "Downgraded (no safe isolated CPU sets are available); efficiency mode instead",
                    };
                }
                return ApplyCpuSets(fact, decision, isolateHandle, isolateIds, cpuPlan.SystemIds);
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

            if (planned == BackgroundAction.LowerPriority) return ApplyPriority(fact, decision, handle, cpuPlan, isolateIds);
            if (planned == BackgroundAction.Sweep) return ApplySweep(fact, decision, handle, cpuPlan, isolateIds);
            return ApplyEfficiency(fact, decision, handle, cpuPlan, isolateIds);
        }
    }

    private BackgroundDecision ApplySweep(ProcessFact fact, BackgroundDecision decision, SafeProcessHandle handle, GameModeCpuPlan? cpuPlan = null, IReadOnlyList<uint>? isolateIds = null)
    {
        IntPtr raw = handle.DangerousGetHandle();
        uint originalPriority = NativeMethods.GameMode.GetPriorityClass(raw);
        var originalThrottle = new NativeMethods.GameMode.PowerThrottlingState { Version = 1 };
        bool throttleReadable = NativeMethods.GameMode.GetProcessInformation(raw, NativeMethods.GameMode.ProcessPowerThrottling, ref originalThrottle,
                Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>());
        if (originalPriority == 0 && !throttleReadable)
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (could not read the current state)" };

        bool ecoOn = throttleReadable && (originalThrottle.StateMask & NativeMethods.GameMode.PowerThrottlingExecutionSpeed) != 0;
        bool prioLow = originalPriority == NativeMethods.GameMode.BelowNormalPriorityClass;
        bool isolate = cpuPlan is { Available: true } && isolateIds is { Count: > 0 };
        if (ecoOn && (prioLow || originalPriority == 0))
        {
            if (!isolate)
            {
                var boostOnly = DisableThreadBoosts(fact.Pid);
                if (boostOnly.Count > 0)
                {
                    var onlyEntry = NewEntry(fact, decision.Planned, 0, 0, 0, 0);
                    _journal.Add(onlyEntry);
                    _journal.UpsertThreadState(fact.Pid, fact.CreationTicks, string.Join(',', boostOnly), string.Empty);
                    return decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Already efficiency + lower priority; thread boosts disabled, verified" };
                }
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already efficiency + lower priority" };
            }
        }

        var entry = NewEntry(fact, decision.Planned,
            prioLow || originalPriority == 0 ? 0 : originalPriority,
            throttleReadable ? originalThrottle.Version : 0,
            throttleReadable ? originalThrottle.ControlMask : 0,
            throttleReadable ? originalThrottle.StateMask : 0);
        _journal.Add(entry);

        bool ecoOk = ecoOn;
        if (!ecoOn && throttleReadable)
        {
            var target = new NativeMethods.GameMode.PowerThrottlingState
            {
                Version = 1,
                ControlMask = NativeMethods.GameMode.PowerThrottlingExecutionSpeed,
                StateMask = NativeMethods.GameMode.PowerThrottlingExecutionSpeed,
            };
            if (NativeMethods.GameMode.SetProcessInformation(raw, NativeMethods.GameMode.ProcessPowerThrottling, ref target,
                    Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>()))
            {
                var check = new NativeMethods.GameMode.PowerThrottlingState { Version = 1 };
                ecoOk = NativeMethods.GameMode.GetProcessInformation(raw, NativeMethods.GameMode.ProcessPowerThrottling, ref check,
                        Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>())
                    && (check.StateMask & NativeMethods.GameMode.PowerThrottlingExecutionSpeed) != 0;
            }
            if (!ecoOk) TryRestoreThrottling(raw, originalThrottle);
        }

        bool prioOk = prioLow;
        if (!prioLow && originalPriority != 0)
        {
            if (NativeMethods.GameMode.SetPriorityClass(raw, NativeMethods.GameMode.BelowNormalPriorityClass))
                prioOk = NativeMethods.GameMode.GetPriorityClass(raw) == NativeMethods.GameMode.BelowNormalPriorityClass;
            if (!prioOk) TryRestorePriority(raw, originalPriority);
        }

        if (!ecoOk && !prioOk)
        {
            _journal.Remove(fact.Pid, fact.CreationTicks);
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (neither change stuck)" };
        }

        var boostOff = DisableThreadBoosts(fact.Pid);
        _journal.UpsertThreadState(fact.Pid, fact.CreationTicks, string.Join(',', boostOff), string.Empty);

        string message = ecoOk && prioOk ? "Efficiency + lower priority, verified"
            : ecoOk ? "Efficiency mode, verified (priority would not stick)"
            : "Lower priority, verified (efficiency unsupported here)";
        if (boostOff.Count > 0) message += $"; {boostOff.Count} thread boost(s) disabled";
        var applied = decision with { Outcome = GameModeOutcome.AppliedVerified, Message = message };
        return MaybeIsolate(fact, applied, handle, cpuPlan, isolateIds);
    }

    private BackgroundDecision ApplyPriority(ProcessFact fact, BackgroundDecision decision, SafeProcessHandle handle, GameModeCpuPlan? cpuPlan = null, IReadOnlyList<uint>? isolateIds = null)
    {
        IntPtr raw = handle.DangerousGetHandle();
        uint original = NativeMethods.GameMode.GetPriorityClass(raw);
        if (original == 0)
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (could not read the current priority)" };

        bool isolate = cpuPlan is { Available: true } && isolateIds is { Count: > 0 };
        if (original == NativeMethods.GameMode.BelowNormalPriorityClass)
        {
            if (!isolate)
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already lower priority" };
            if (!TryReadCpuSets(handle, out uint[] currentSets, out bool restricted))
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already lower priority; CPU sets unreadable" };
            if (cpuPlan is not { Available: true } || isolateIds is not { Count: > 0 })
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already in that state" };
            var placement = restricted ? currentSets : cpuPlan.SystemIds;
            if (new HashSet<uint>(placement).SetEquals(isolateIds))
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already in that state" };
            var satisfied = NewEntry(fact, decision.Planned, original, 0, 0, 0);
            _journal.Add(satisfied);
            var already = decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Lower priority, verified" };
            return MaybeIsolate(fact, already, handle, cpuPlan, isolateIds);
        }

        var entry = NewEntry(fact, decision.Planned, original, 0, 0, 0);
        _journal.Add(entry);

        if (!NativeMethods.GameMode.SetPriorityClass(raw, NativeMethods.GameMode.BelowNormalPriorityClass))
        {
            _journal.Remove(fact.Pid, fact.CreationTicks);
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (Windows denied the priority change)" };
        }

        uint current = NativeMethods.GameMode.GetPriorityClass(raw);
        if (current == 0)
        {
            _journal.Remove(fact.Pid, fact.CreationTicks);
            TryRestorePriority(raw, original);
            return decision with { Outcome = GameModeOutcome.AppliedUnverified, Planned = BackgroundAction.None, Message = "Applied (couldn't verify) and reverted" };
        }

        if (current == NativeMethods.GameMode.BelowNormalPriorityClass)
        {
            var applied = decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Lower priority, verified" };
            return MaybeIsolate(fact, applied, handle, cpuPlan, isolateIds);
        }

        _journal.Remove(fact.Pid, fact.CreationTicks);
        TryRestorePriority(raw, original);
        return decision with { Outcome = GameModeOutcome.RestoreFailed, Planned = BackgroundAction.None, Message = "Priority did not stick, reverted" };
    }

    private BackgroundDecision ApplyEfficiency(ProcessFact fact, BackgroundDecision decision, SafeProcessHandle handle, GameModeCpuPlan? cpuPlan = null, IReadOnlyList<uint>? isolateIds = null)
    {
        IntPtr raw = handle.DangerousGetHandle();
        var original = new NativeMethods.GameMode.PowerThrottlingState { Version = 1 };
        bool isolate = cpuPlan is { Available: true } && isolateIds is { Count: > 0 };
        if (!NativeMethods.GameMode.GetProcessInformation(raw, NativeMethods.GameMode.ProcessPowerThrottling, ref original,
                Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>()))
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (efficiency mode not supported here)" };

        if ((original.StateMask & NativeMethods.GameMode.PowerThrottlingExecutionSpeed) != 0)
        {
            if (!isolate)
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already in efficiency mode" };
            if (!TryReadCpuSets(handle, out uint[] currentSets, out bool restricted))
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already in efficiency mode; CPU sets unreadable" };
            if (cpuPlan is not { Available: true } || isolateIds is not { Count: > 0 })
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already in that state" };
            var current = restricted ? currentSets : cpuPlan.SystemIds;
            if (new HashSet<uint>(current).SetEquals(isolateIds))
                return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already in that state" };
            var satisfied = NewEntry(fact, decision.Planned, 0, original.Version, original.ControlMask, original.StateMask);
            _journal.Add(satisfied);
            var already = decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Efficiency mode, verified" };
            return MaybeIsolate(fact, already, handle, cpuPlan, isolateIds);
        }

        var entry = NewEntry(fact, decision.Planned, 0, original.Version, original.ControlMask, original.StateMask);
        _journal.Add(entry);

        var target = new NativeMethods.GameMode.PowerThrottlingState
        {
            Version = 1,
            ControlMask = NativeMethods.GameMode.PowerThrottlingExecutionSpeed,
            StateMask = NativeMethods.GameMode.PowerThrottlingExecutionSpeed,
        };

        if (!NativeMethods.GameMode.SetProcessInformation(raw, NativeMethods.GameMode.ProcessPowerThrottling, ref target,
                Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>()))
        {
            _journal.Remove(fact.Pid, fact.CreationTicks);
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (Windows denied efficiency mode)" };
        }

        var check = new NativeMethods.GameMode.PowerThrottlingState { Version = 1 };
        bool verified = NativeMethods.GameMode.GetProcessInformation(raw, NativeMethods.GameMode.ProcessPowerThrottling, ref check,
            Marshal.SizeOf<NativeMethods.GameMode.PowerThrottlingState>())
            && (check.StateMask & NativeMethods.GameMode.PowerThrottlingExecutionSpeed) != 0;

        if (verified)
        {
            var applied = decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Efficiency mode, verified" };
            return MaybeIsolate(fact, applied, handle, cpuPlan, isolateIds);
        }

        _journal.Remove(fact.Pid, fact.CreationTicks);
        TryRestoreThrottling(raw, original);
        return decision with { Outcome = GameModeOutcome.AppliedUnverified, Planned = BackgroundAction.None, Message = "Efficiency mode applied but not verified, reverted" };
    }

    public BackgroundDecision ApplyGame(ProcessFact fact, GameModeCpuPlan? cpuPlan, bool isolateCores)
    {
        var decision = new BackgroundDecision(fact.Pid, fact.CreationTicks, fact.Name, fact.ImagePath,
            BackgroundCategory.Game, string.Empty, BackgroundAction.GameBoost, BackgroundAction.GameBoost,
            GameModeOutcome.Applicable, "Planned");

        if (!SameInstance(fact.Pid, fact.CreationTicks))
            return decision with { Outcome = GameModeOutcome.SkippedSafety, Message = "Process exited before the action ran" };

        lock (_actionGate)
        {
            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation
                | NativeMethods.ProcessAccess.SetInformation,
                false, (uint)fact.Pid);
            if (handle.IsInvalid)
                return decision with { Outcome = GameModeOutcome.Refused, Message = "Refused (access denied)" };

            IntPtr raw = handle.DangerousGetHandle();
            uint original = NativeMethods.GameMode.GetPriorityClass(raw);
            if (original == 0)
                return decision with { Outcome = GameModeOutcome.Refused, Message = "Refused (could not read the current priority)" };
            if (original == 0x00000100)
                return decision with { Outcome = GameModeOutcome.SkippedSafety, Message = "Left alone (real-time priority)" };

            var entry = NewEntry(fact, BackgroundAction.GameBoost, original, 0, 0, 0);
            if (NativeMethods.Priority.GetProcessPriorityBoost(handle, out bool boostWasDisabled))
            {
                entry.OriginalProcessBoostDisabled = boostWasDisabled;
                if (!boostWasDisabled && NativeMethods.Priority.SetProcessPriorityBoost(handle, true))
                    entry.ProcessBoostChanged = true;
            }
            _journal.Add(entry);

            var (junkBoostOff, junkLowered) = SweepGameThreads(fact);
            var boostOff = new List<uint>(junkBoostOff);
            foreach (uint tid in DisableThreadBoosts(fact.Pid))
                if (!boostOff.Contains(tid)) boostOff.Add(tid);
            string junkSummary = boostOff.Count == 0 && junkLowered.Count == 0 ? string.Empty
                : $"; {boostOff.Count} thread boost(s) off, {junkLowered.Count} junk thread(s) lowered";
            if (boostOff.Count > 0 || junkLowered.Count > 0)
                _journal.UpsertThreadState(fact.Pid, fact.CreationTicks, string.Join(',', boostOff), FormatThreadTweaks(junkLowered));

            bool boostPriority = original != NativeMethods.GameMode.AboveNormalPriorityClass
                && original != NativeMethods.GameMode.HighPriorityClass;
            bool isolate = isolateCores && cpuPlan is { Available: true } && cpuPlan.GameIds.Count > 0;
            if (!boostPriority)
            {
                if (!isolate)
                    return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Game priority already high; boost disabled" };
                if (!TryReadCpuSets(handle, out uint[] gameCurrent, out bool gameRestricted))
                    return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Game priority already high; boost disabled; CPU sets unreadable" };
                if (cpuPlan is not { Available: true })
                    return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Game priority already high; boost disabled" };
                var gameSet = gameRestricted ? gameCurrent : cpuPlan.SystemIds;
                if (new HashSet<uint>(gameSet).SetEquals(cpuPlan.GameIds))
                    return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Already in that state" };
            }

            if (boostPriority)
            {
                uint boosted = 0;
                if (NativeMethods.GameMode.SetPriorityClass(raw, NativeMethods.GameMode.AboveNormalPriorityClass))
                    boosted = NativeMethods.GameMode.GetPriorityClass(raw);
                if (boosted != NativeMethods.GameMode.AboveNormalPriorityClass)
                {
                    _journal.Remove(fact.Pid, fact.CreationTicks);
                    TryRestorePriority(raw, original);
                    return decision with { Outcome = GameModeOutcome.Refused, Message = "Refused (the game priority change did not stick)" };
                }
            }

            var applied = decision with { Outcome = GameModeOutcome.AppliedVerified, Message = (boostPriority ? "Game boosted, verified" : "Game priority already high; boost disabled") + junkSummary };
            if (!isolate)
                return applied;

            if (cpuPlan is not { Available: true })
                return applied;
            var isolation = ApplyIsolation(fact, applied, handle, cpuPlan.GameIds, cpuPlan.SystemIds, standalone: false);
            if (isolation.Outcome == GameModeOutcome.AppliedVerified)
                return applied with { Message = applied.Message + "; game cores isolated, verified" };
            if (isolation.Outcome == GameModeOutcome.AlreadyInState)
                return applied with { Message = applied.Message + "; game cores already isolated" };
            return applied with { Message = applied.Message + "; game core isolation not applied (" + isolation.Message + ")" };
        }
    }

    private BackgroundDecision ApplyCpuSets(ProcessFact fact, BackgroundDecision decision, SafeProcessHandle handle, IReadOnlyList<uint> targetIds, IReadOnlyList<uint> systemIds) =>
        ApplyIsolation(fact, decision, handle, targetIds, systemIds, standalone: true);

    private BackgroundDecision MaybeIsolate(ProcessFact fact, BackgroundDecision decision, SafeProcessHandle handle, GameModeCpuPlan? cpuPlan, IReadOnlyList<uint>? isolateIds)
    {
        if (cpuPlan is not { Available: true } || isolateIds is not { Count: > 0 })
            return decision;
        var isolation = ApplyIsolation(fact, decision, handle, isolateIds, cpuPlan.SystemIds, standalone: false);
        if (isolation.Outcome == GameModeOutcome.AppliedVerified)
            return decision with { Message = decision.Message + "; cores isolated, verified" };
        if (isolation.Outcome == GameModeOutcome.AlreadyInState)
            return decision with { Message = decision.Message + "; cores already isolated" };
        return decision with { Message = decision.Message + "; core isolation not applied (" + isolation.Message + ")" };
    }

    private BackgroundDecision ApplyIsolation(ProcessFact fact, BackgroundDecision decision, SafeProcessHandle handle, IReadOnlyList<uint> targetIds, IReadOnlyList<uint> systemIds, bool standalone)
    {
        if (!TryReadCpuSets(handle, out uint[] original, out bool restricted))
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (could not read CPU sets)" };

        var originalSet = restricted ? original : systemIds;
        if (new HashSet<uint>(originalSet).SetEquals(targetIds))
            return decision with { Outcome = GameModeOutcome.AlreadyInState, Message = "Cores already isolated" };

        if (standalone)
        {
            var entry = NewEntry(fact, decision.Planned, 0, 0, 0, 0);
            entry.CpuSetsChanged = true;
            entry.OriginalCpuSetsRestricted = restricted;
            entry.OriginalCpuSetIds = CpuSetText(original);
            entry.SystemCpuSetIds = CpuSetText(systemIds);
            _journal.Add(entry);
        }

        if (!NativeMethods.CpuSets.SetProcessDefaultCpuSets(handle, targetIds.ToArray(), (uint)targetIds.Count))
        {
            if (standalone) _journal.Remove(fact.Pid, fact.CreationTicks);
            return decision with { Outcome = GameModeOutcome.Refused, Planned = BackgroundAction.None, Message = "Refused (Windows denied CPU sets)" };
        }

        if (!TryReadCpuSets(handle, out uint[] current, out _) || !new HashSet<uint>(current).SetEquals(targetIds))
        {
            TryRestoreCpuSets(handle, original, restricted, systemIds);
            if (standalone) _journal.Remove(fact.Pid, fact.CreationTicks);
            return decision with { Outcome = GameModeOutcome.RestoreFailed, Planned = BackgroundAction.None, Message = "CPU sets did not stick, reverted" };
        }

        if (!standalone)
            _journal.UpsertCpuState(fact.Pid, fact.CreationTicks, CpuSetText(original), restricted, CpuSetText(systemIds));
        return decision with { Outcome = GameModeOutcome.AppliedVerified, Message = "Cores isolated, verified" };
    }

    private static bool TryReadCpuSets(SafeProcessHandle handle, out uint[] ids, out bool restricted)
    {
        ids = Array.Empty<uint>();
        restricted = false;
        if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(handle, null, 0, out uint required))
        {
            if (Marshal.GetLastWin32Error() != 122) return false;
        }
        if (required == 0) return true;
        var buffer = new uint[required];
        if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(handle, buffer, required, out _)) return false;
        ids = buffer;
        restricted = true;
        return true;
    }

    private static bool TryRestoreCpuSets(SafeProcessHandle handle, uint[] original, bool restricted, IReadOnlyList<uint> systemIds)
    {
        var target = restricted && original.Length > 0 ? original : systemIds.ToArray();
        if (target.Length == 0) return false;
        if (!NativeMethods.CpuSets.SetProcessDefaultCpuSets(handle, target, (uint)target.Length)) return false;
        return TryReadCpuSets(handle, out uint[] current, out _) && new HashSet<uint>(current).SetEquals(target);
    }

    private static bool TryRestoreCpuSets(SafeProcessHandle handle, GameModeJournalEntry entry)
    {
        if (!entry.CpuSetsChanged) return true;
        var original = ParseCpuSetIds(entry.OriginalCpuSetIds);
        var system = ParseCpuSetIds(entry.SystemCpuSetIds);
        return TryRestoreCpuSets(handle, original, entry.OriginalCpuSetsRestricted, system);
    }

    private static string CpuSetText(IEnumerable<uint> ids) => string.Join(',', ids);

    private static uint[] ParseCpuSetIds(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<uint>();
        var ids = new List<uint>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, out uint id)) ids.Add(id);
        return ids.ToArray();
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

        if (entry.Action == nameof(BackgroundAction.LowerPriority) || entry.Action == nameof(BackgroundAction.GameBoost))
        {
            ok &= NativeMethods.GameMode.SetPriorityClass(raw, entry.OriginalPriorityClass);
            if (ok)
            {
                uint check = NativeMethods.GameMode.GetPriorityClass(raw);
                ok &= check != 0 && check == entry.OriginalPriorityClass;
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
        else if (entry.Action == nameof(BackgroundAction.Sweep))
        {
            if (entry.OriginalPriorityClass != 0)
            {
                ok &= NativeMethods.GameMode.SetPriorityClass(raw, entry.OriginalPriorityClass);
                if (ok)
                {
                    uint check = NativeMethods.GameMode.GetPriorityClass(raw);
                    ok &= check != 0 && check == entry.OriginalPriorityClass;
                }
            }
            if (ok && entry.ThrottlingVersion != 0)
            {
                var original = new NativeMethods.GameMode.PowerThrottlingState
                {
                    Version = entry.ThrottlingVersion,
                    ControlMask = entry.ThrottlingControlMask,
                    StateMask = entry.ThrottlingStateMask,
                };
                ok &= TryRestoreThrottling(raw, original);
            }
        }

        if (entry.ProcessBoostChanged)
            ok &= NativeMethods.Priority.SetProcessPriorityBoost(handle, entry.OriginalProcessBoostDisabled);
        if (!string.IsNullOrEmpty(entry.BoostDisabledTids))
            RestoreThreadBoosts(ParseTids(entry.BoostDisabledTids));
        if (!string.IsNullOrEmpty(entry.ThreadPriorityTweaks))
            RestoreThreadPriorities(ParseThreadTweaks(entry.ThreadPriorityTweaks));

        ok &= TryRestoreCpuSets(handle, entry);

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

    private static readonly string[] GameJunkThreadHints = new[]
    {
        "telemetry", "analytic", "metric", "upload", "crash", "report",
        "watson", "feedback", "advert", "tracking", "cef",
    };

    private static List<uint> ListProcessThreads(int pid)
    {
        var tids = new List<uint>();
        IntPtr snapshot = NativeMethods.Toolhelp.CreateToolhelp32Snapshot(NativeMethods.Toolhelp.TH32CS_SNAPTHREAD, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return tids;
        try
        {
            var te = new NativeMethods.Toolhelp.THREADENTRY32 { dwSize = (uint)Marshal.SizeOf<NativeMethods.Toolhelp.THREADENTRY32>() };
            if (!NativeMethods.Toolhelp.Thread32First(snapshot, ref te)) return tids;
            do
            {
                if (te.th32OwnerProcessID == (uint)pid) tids.Add(te.th32ThreadID);
            }
            while (NativeMethods.Toolhelp.Thread32Next(snapshot, ref te));
        }
        catch
        {
        }
        finally
        {
            NativeMethods.Handles.CloseHandle(snapshot);
        }
        return tids;
    }

    private static bool TryDisableThreadBoost(uint tid)
    {
        try
        {
            using var thread = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation, false, tid);
            if (thread.IsInvalid) return false;
            if (!NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool Svetlana) || Svetlana) return false;
            if (!NativeMethods.Priority.SetThreadPriorityBoost(thread, Svetlana: true)) return false;
            return NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool check) && check;
        }
        catch
        {
            return false;
        }
    }

    private static List<uint> DisableThreadBoosts(int pid)
    {
        var disabled = new List<uint>();
        foreach (var tid in ListProcessThreads(pid))
            if (TryDisableThreadBoost(tid)) disabled.Add(tid);
        return disabled;
    }

    private static (List<uint> BoostOff, Dictionary<uint, int> Lowered) SweepGameThreads(ProcessFact fact)
    {
        var boostOff = new List<uint>();
        var lowered = new Dictionary<uint, int>();
        foreach (var tid in ListProcessThreads(fact.Pid))
        {
            string? description = null;
            try { description = NativeSnapshotService.TryGetThreadDescription(tid); }
            catch { }
            if (string.IsNullOrEmpty(description)) continue;
            if (!GameJunkThreadHints.Any(hint => description.Contains(hint, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                using var thread = NativeMethods.Handles.OpenThread(
                    NativeMethods.ThreadAccess.SetInformation | NativeMethods.ThreadAccess.QueryInformation, false, tid);
                if (thread.IsInvalid) continue;
                if (NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool Svetlana) && !Svetlana
                    && NativeMethods.Priority.SetThreadPriorityBoost(thread, Svetlana: true)
                    && NativeMethods.Priority.GetThreadPriorityBoost(thread, out bool check) && check)
                    boostOff.Add(tid);
                int current = NativeMethods.Priority.GetThreadPriority(thread);
                if (current > (int)NativeMethods.ThreadPriorityLevel.BelowNormal
                    && NativeMethods.Priority.SetThreadPriority(thread, (int)NativeMethods.ThreadPriorityLevel.BelowNormal))
                    lowered[tid] = current;
            }
            catch
            {
            }
        }
        return (boostOff, lowered);
    }

    private static string FormatThreadTweaks(Dictionary<uint, int> lowered)
        => string.Join(';', lowered.Select(pair => pair.Key + ":" + pair.Value));

    private static List<uint> ParseTids(string text)
    {
        var tids = new List<uint>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (uint.TryParse(part, out uint tid)) tids.Add(tid);
        return tids;
    }

    private static Dictionary<uint, int> ParseThreadTweaks(string text)
    {
        var tweaks = new Dictionary<uint, int>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split(':');
            if (pair.Length == 2 && uint.TryParse(pair[0], out uint tid) && int.TryParse(pair[1], out int priority))
                tweaks[tid] = priority;
        }
        return tweaks;
    }

    private static void RestoreThreadBoosts(IEnumerable<uint> tids)
    {
        foreach (var tid in tids)
        {
            try
            {
                using var thread = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.SetInformation, false, tid);
                if (thread.IsInvalid) continue;
                NativeMethods.Priority.SetThreadPriorityBoost(thread, Svetlana: false);
            }
            catch
            {
            }
        }
    }

    private static void RestoreThreadPriorities(Dictionary<uint, int> tweaks)
    {
        foreach (var pair in tweaks)
        {
            try
            {
                using var thread = NativeMethods.Handles.OpenThread(NativeMethods.ThreadAccess.SetInformation, false, pair.Key);
                if (thread.IsInvalid) continue;
                NativeMethods.Priority.SetThreadPriority(thread, pair.Value);
            }
            catch
            {
            }
        }
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
