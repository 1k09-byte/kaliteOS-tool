using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Models;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Services;
using kaliteConfig.ProcessOptimizer.Models;

namespace kaliteConfig.Services;

public enum CpuBoundState
{
    Sampling,
    Detected,
    NotDetected,
}

public enum GamingModeOutcome
{
    Activated,
    JoinedSameTarget,
    JoinedOtherTarget,
    HandedOff,
    Failed,
}

public sealed class GamingModeResult
{
    public GamingModeOutcome Outcome { get; init; } = GamingModeOutcome.Activated;
    public bool Success { get; init; }
    public int LoweredCount { get; init; }
    public int EcoCount { get; init; }
    public int FailedCount { get; init; }
    public int BoostStrippedCount { get; init; }
    public int HeldCount { get; init; }
    public string TargetBefore { get; init; } = "?";
    public string TargetAfter { get; init; } = "?";
    public string Error { get; init; } = string.Empty;

    public string Summary => Outcome switch
    {
        GamingModeOutcome.JoinedSameTarget =>
            $"already on for target {TargetAfter} · {HeldCount} holder(s) · {LoweredCount} process(es) still demoted",
        GamingModeOutcome.JoinedOtherTarget =>
            $"a session is already running for {TargetAfter} - {TargetBefore} was left as it is",
        GamingModeOutcome.Failed => $"failed: {Error}",
        _ => $"target {TargetBefore} → {TargetAfter} · {LoweredCount} background lowered · {EcoCount} in Efficiency mode" +
             (BoostStrippedCount > 0 ? $" · {BoostStrippedCount} boost disabled" : "") +
             (FailedCount > 0 ? $" · {FailedCount} skipped (no access)" : ""),
    };
}

public sealed class GamingModeService
{
    private const double ContentionPercentOfTotalCpu = 2.0;

    private sealed record RestoreEntry
    {
        public uint Priority { get; init; }
        public bool? Eco { get; init; }
        public bool? BoostDisabled { get; init; }
    }

    private readonly Dictionary<string, RestoreEntry> _restoreMap = new();

    private void Remember(string key, uint priority, bool? eco, bool? boostDisabled)
    {
        if (!_restoreMap.ContainsKey(key))
            _restoreMap[key] = new RestoreEntry { Priority = priority, Eco = eco, BoostDisabled = boostDisabled };
    }

    private readonly GameModeHoldRegistry _holds = new();

    public bool IsActive => _holds.IsHeld;

    public IReadOnlyList<GameModeHold> Holds => _holds.Holds;

    public int? SessionTargetPid => _holds.SessionTargetPid;

    public bool IsHeldBy(GameModeOwner owner) => _holds.IsHeldBy(owner);

    public bool IsHeldByOther(GameModeOwner owner) => _holds.IsHeldByOther(owner);

    public int RestorableCount => _restoreMap.Count;

    private static string NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return $"PID {pid}"; }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }

    private static string GetProcessKey(int pid)
    {
        long ticks = 0;
        try
        {
            using var p = Process.GetProcessById(pid);
            ticks = p.StartTime.Ticks;
        }
        catch { }
        return $"{pid}_{ticks}";
    }

    private static string GetProcessKey(Process p)
    {
        long ticks = 0;
        try { ticks = p.StartTime.Ticks; } catch { }
        return $"{p.Id}_{ticks}";
    }

    private static HashSet<int> BuildProtectedFamily(int targetPid, IReadOnlyCollection<int> protectedPids)
    {
        var family = new HashSet<int>();
        if (protectedPids != null)
        {
            foreach (int p in protectedPids) family.Add(p);
        }
        family.Add(targetPid);

        var parentOf = ReadParentPids();
        if (parentOf.Count == 0) return family;

        var roots = new HashSet<int>(family);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var kvp in parentOf)
            {
                if (family.Contains(kvp.Key)) continue;
                if (!roots.Contains(kvp.Value)) continue;
                family.Add(kvp.Key);
                grew = true;
            }
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (int pid in family)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                names.Add(p.ProcessName);
            }
            catch { }
        }
        if (names.Count > 0)
        {
            foreach (var kvp in parentOf)
            {
                if (family.Contains(kvp.Key)) continue;
                try
                {
                    using var p = Process.GetProcessById(kvp.Key);
                    if (names.Contains(p.ProcessName)) family.Add(kvp.Key);
                }
                catch { }
            }
        }

        return family;
    }

    private static Dictionary<int, int> ReadParentPids()
    {
        var map = new Dictionary<int, int>(512);
        try
        {
            IntPtr snapshot = NativeMethods.Toolhelp.CreateToolhelp32Snapshot(
                NativeMethods.Toolhelp.TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return map;
            try
            {
                var entry = new NativeMethods.Toolhelp.PROCESSENTRY32
                {
                    dwSize = (uint)Marshal.SizeOf<NativeMethods.Toolhelp.PROCESSENTRY32>()
                };
                if (NativeMethods.Toolhelp.Process32First(snapshot, ref entry))
                {
                    do
                    {
                        map[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                        entry.dwSize = (uint)Marshal.SizeOf<NativeMethods.Toolhelp.PROCESSENTRY32>();
                    }
                    while (NativeMethods.Toolhelp.Process32Next(snapshot, ref entry));
                }
            }
            finally
            {
                NativeMethods.Handles.CloseHandle(snapshot);
            }
        }
        catch
        {
            map.Clear();
        }
        return map;
    }

    public async Task<bool> AutoRaiseTargetAsync(int pid)
    {
        return await Task.Run(() =>
        {
            uint current = ReadPriorityClass(pid);
            if (current != NativeMethods.Priority.Normal)
            {
                return false;
            }

            Remember(GetProcessKey(pid), current, null, null);

            return TrySetPriorityClass(pid, NativeMethods.Priority.AboveNormal);
        }).ConfigureAwait(false);
    }

    public async Task<GamingModeResult> AcquireAsync(
        GameModeOwner owner,
        int targetPid,
        IReadOnlyCollection<int>? protectedPids = null,
        string? reason = null)
    {
        protectedPids ??= new[] { targetPid };

        GameModeAcquireResult decision = _holds.Acquire(
            owner, targetPid, NameOf(targetPid), reason ?? owner.ToString(), IsProcessAlive);

        switch (decision.Action)
        {
            case GameModeAcquireAction.Joined:
                return new GamingModeResult
                {
                    Outcome = GamingModeOutcome.JoinedSameTarget,
                    Success = true,
                    LoweredCount = _restoreMap.Count,
                    HeldCount = _holds.Count,
                    TargetBefore = decision.Hold.TargetName,
                    TargetAfter = decision.Hold.TargetName,
                };

            case GameModeAcquireAction.JoinedOtherTarget:
                return new GamingModeResult
                {
                    Outcome = GamingModeOutcome.JoinedOtherTarget,
                    Success = true,
                    HeldCount = _holds.Count,
                    TargetBefore = decision.Hold.TargetName,
                    TargetAfter = NameOf(decision.RunningTargetPid ?? targetPid),
                };

            case GameModeAcquireAction.HandOff:
                TearDown();
                break;
        }

        GamingModeResult result = await ActivateCoreAsync(targetPid, protectedPids).ConfigureAwait(false);

        if (!result.Success)
        {
            if (_holds.Release(owner).SessionEnded)
            {
                TearDown();
            }
            return result;
        }

        return new GamingModeResult
        {
            Outcome = decision.Action == GameModeAcquireAction.HandOff
                ? GamingModeOutcome.HandedOff
                : GamingModeOutcome.Activated,
            Success = true,
            LoweredCount = result.LoweredCount,
            EcoCount = result.EcoCount,
            BoostStrippedCount = result.BoostStrippedCount,
            FailedCount = result.FailedCount,
            HeldCount = _holds.Count,
            TargetBefore = result.TargetBefore,
            TargetAfter = result.TargetAfter,
        };
    }

    private Task<GamingModeResult> ActivateCoreAsync(int targetPid, IReadOnlyCollection<int> protectedPids)
    {
        return Task.Run(() =>
        {
            try
            {
                uint targetOriginal = ReadPriorityClass(targetPid);
                string targetBefore = ProcessTuningService.PriorityName(targetOriginal);

                bool ok = true;

                int lowered = 0;
                int ecoCount = 0;
                int boostStripped = 0;
                int failed = 0;
                int selfPid = Process.GetCurrentProcess().Id;

                var gameFamily = BuildProtectedFamily(targetPid, protectedPids);

                Dictionary<int, double> cpu = SampleCpuPercentOfTotal();

                GamingExemptionService.EnsureStarterFile();
                foreach (Process proc in Process.GetProcesses())
                {
                    int pid;
                    string name;
                    try
                    {
                        pid = proc.Id;
                        name = proc.ProcessName + ".exe";
                    }
                    catch
                    {
                        continue;
                    }
                    finally
                    {
                        try { proc.Dispose(); } catch { }
                    }

                    if (pid <= 4 || pid == selfPid) continue;
                    if (gameFamily.Contains(pid)) continue;
                    if (ProcessTuningService.IsCritical(name, pid)) continue;
                    if (ProcessTuningService.IsSelf(pid)) continue;
                    if (GamingExemptionService.IsExempt(name)) continue;

                    if (!cpu.TryGetValue(pid, out double cpuPct)) continue;
                    if (cpuPct < ContentionPercentOfTotalCpu) continue;

                    using var handle = NativeMethods.Handles.OpenProcess(
                        NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation,
                        false, (uint)pid);
                    if (handle.IsInvalid)
                    {
                        failed++;
                        continue;
                    }

                    uint original = NativeMethods.Priority.GetPriorityClass(handle);
                    if (original == 0)
                    {
                        failed++;
                        continue;
                    }
                    if (original != NativeMethods.Priority.Normal) continue;

                    bool? ecoOriginal = ReadEcoState(handle);
                    bool? boostOriginal = ReadBoostDisabled(handle);

                    Remember(GetProcessKey(pid), original, ecoOriginal, boostOriginal);

                    if (NativeMethods.Priority.SetPriorityClass(handle, NativeMethods.Priority.BelowNormal))
                    {
                        lowered++;

                        if (boostOriginal == false && TryDisableBoost(pid))
                            boostStripped++;

                        if (ecoOriginal == false && TrySetEco(handle, true))
                        {
                            ecoCount++;
                        }
                    }
                    else
                    {
                        failed++;
                    }
                }

                string gameName = "?";
                try { gameName = Process.GetProcessById(targetPid).ProcessName; } catch { }

                var exclusions = new List<string>();
                if (!string.IsNullOrWhiteSpace(gameName)) exclusions.Add(gameName);
                foreach (int p in gameFamily)
                {
                    if (p == targetPid) continue;
                    try
                    {
                        string n = Process.GetProcessById(p).ProcessName;
                        if (!string.IsNullOrWhiteSpace(n) && !exclusions.Contains(n)) exclusions.Add(n);
                    }
                    catch { }
                }

                OptimizationSessionOrchestrator.Instance.StartSession(targetPid, gameName,
                    new OptimizationProfile { Aggressiveness = AggressivenessLevel.Light, Exclusions = exclusions },
                    gameFamily);

                string targetAfter = ProcessTuningService.PriorityName(ReadPriorityClass(targetPid));

                return new GamingModeResult
                {
                    Success = ok,
                    LoweredCount = lowered,
                    EcoCount = ecoCount,
                    BoostStrippedCount = boostStripped,
                    FailedCount = failed,
                    TargetBefore = targetBefore,
                    TargetAfter = targetAfter,
                    Error = ok ? string.Empty : NativeSnapshotService.LastError("Setting priority class failed.").Message,
                };
            }
            catch (Exception ex)
            {
                return new GamingModeResult
                {
                    Outcome = GamingModeOutcome.Failed,
                    Success = false,
                    Error = ex.Message,
                };
            }
        });
    }

    public (int Reset, int Skipped) ResetAllPrioritiesToNormal()
    {
        int reset = 0, skipped = 0;
        int self = Environment.ProcessId;
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                int pid = proc.Id;
                string name;
                try { name = proc.ProcessName + ".exe"; }
                catch { skipped++; continue; }

                if (pid <= 4 || pid == self
                    || ProcessTuningService.IsCritical(name, pid)
                    || ProcessOptimizer.Services.ProtectedProcessGuard.IsProcessProtected(proc.ProcessName))
                {
                    skipped++;
                    continue;
                }

                if (TrySetPriorityClass(pid, NativeMethods.Priority.Normal)) reset++;
                else skipped++;
            }
            catch { skipped++; }
            finally { try { proc.Dispose(); } catch { } }
        }
        _restoreMap.Clear();
        _threadBoostOriginals.Clear();
        return (reset, skipped);
    }

    public bool Release(GameModeOwner owner)
    {
        GameModeReleaseResult result = _holds.Release(owner);
        if (result.SessionEnded)
        {
            TearDown();
        }
        return result.SessionEnded;
    }

    public void ReleaseAll()
    {
        _holds.Clear();
        TearDown();
    }

    private void TearDown()
    {
        OptimizationSessionOrchestrator.Instance.EndSession();

        foreach (var kvp in _restoreMap)
        {
            RestoreEntry entry = kvp.Value;
            string key = kvp.Key;

            string[] parts = key.Split('_');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int pid)) continue;

            if (GetProcessKey(pid) != key)
            {
                continue;
            }

            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (handle.IsInvalid) continue;

            NativeMethods.Priority.SetPriorityClass(handle, entry.Priority);

            if (entry.BoostDisabled.HasValue)
            {
                try
                {
                    NativeMethods.Priority.SetProcessPriorityBoost(handle, entry.BoostDisabled.Value);
                    if (entry.BoostDisabled.Value == false) RestoreThreadBoosts(pid, key);
                }
                catch { }
            }

            if (entry.Eco.HasValue)
            {
                TrySetEco(handle, entry.Eco.Value);
            }
        }

        _restoreMap.Clear();
        _threadBoostOriginals.Clear();
    }

    private readonly Dictionary<string, bool> _threadBoostOriginals = new();

    private bool TryDisableBoost(int pid)
    {
        var originals = new List<(uint Tid, bool WasDisabled)>();
        try
        {
            using var proc = Process.GetProcessById(pid);
            foreach (ProcessThread? t in proc.Threads)
            {
                if (t == null) continue;
                uint tid = (uint)t.Id;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.QueryInformation, false, tid);
                    if (hThread.IsInvalid) continue;
                    if (NativeMethods.Priority.GetThreadPriorityBoost(hThread, out bool wasDisabled))
                        originals.Add((tid, wasDisabled));
                }
                catch { }
            }
        }
        catch {  }

        bool ok = false;
        try
        {
            using var handle = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (!handle.IsInvalid)
                ok = NativeMethods.Priority.SetProcessPriorityBoost(handle, Svetlana: true);
        }
        catch { }

        try
        {
            using var proc = Process.GetProcessById(pid);
            long ticks;
            try { ticks = proc.StartTime.Ticks; } catch { return ok; }
            foreach (var (tid, wasDisabled) in originals)
            {
                string key = $"{pid}_{ticks}_{tid}";
                if (!_threadBoostOriginals.ContainsKey(key)) _threadBoostOriginals[key] = wasDisabled;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation, false, tid);
                    if (hThread.IsInvalid) continue;
                    NativeMethods.Priority.SetThreadPriorityBoost(hThread, Svetlana: true);
                }
                catch { }
            }
        }
        catch { }

        return ok;
    }

    private void RestoreThreadBoosts(int pid, string processKey)
    {
        try
        {
            string prefix = processKey + "_";
            foreach (var kvp in _threadBoostOriginals)
            {
                if (!kvp.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                if (!uint.TryParse(kvp.Key.Substring(prefix.Length), out uint tid)) continue;
                try
                {
                    using var hThread = NativeMethods.Handles.OpenThread(
                        NativeMethods.ThreadAccess.SetInformation, false, tid);
                    if (hThread.IsInvalid) continue;
                    if (NativeMethods.Priority.SetThreadPriorityBoost(hThread, kvp.Value))
                        _threadBoostOriginals.Remove(kvp.Key);
                }
                catch { }
            }
        }
        catch { }
    }

    private static Dictionary<int, double> SampleCpuPercentOfTotal(int sampleMs = 250)
    {
        var usage = new Dictionary<int, double>();
        Dictionary<int, long> first = ReadAllCpuTicks();
        if (first.Count == 0) return usage;

        var sw = Stopwatch.StartNew();
        Thread.Sleep(sampleMs);
        Dictionary<int, long> second = ReadAllCpuTicks();
        sw.Stop();

        double elapsedMs = sw.Elapsed.TotalMilliseconds;
        if (elapsedMs <= 0) return usage;

        int cpus = Math.Max(1, Environment.ProcessorCount);
        foreach (var kvp in second)
        {
            if (!first.TryGetValue(kvp.Key, out long before)) continue;
            long delta = kvp.Value - before;
            if (delta <= 0) continue;
            usage[kvp.Key] = delta / 10_000.0 / elapsedMs * 100.0 / cpus;
        }
        return usage;
    }

    private static Dictionary<int, long> ReadAllCpuTicks()
    {
        var ticks = new Dictionary<int, long>();
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return ticks; }

        foreach (Process proc in procs)
        {
            try
            {
                int pid = proc.Id;
                if (pid <= 4) continue;
                using var handle = NativeMethods.Handles.OpenProcess(
                    NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
                if (handle.IsInvalid) continue;
                if (NativeMethods.Times.GetProcessTimes(handle, out _, out _, out var kernel, out var user))
                {
                    ticks[pid] = kernel.ToTicks() + user.ToTicks();
                }
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
        return ticks;
    }

    private static uint ReadPriorityClass(int pid)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return 0;
            }

            uint cls = NativeMethods.Priority.GetPriorityClass(process);
            return cls == 0 ? 0 : cls;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TrySetPriorityClass(int pid, uint cls)
    {
        try
        {
            using var process = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (process.IsInvalid)
            {
                return false;
            }

            return NativeMethods.Priority.SetPriorityClass(process, cls);
        }
        catch
        {
            return false;
        }
    }

    private static bool? ReadBoostDisabled(SafeProcessHandle process)
    {
        try
        {
            if (process.IsInvalid) return null;
            return NativeMethods.Priority.GetProcessPriorityBoost(process, out bool boostDisabled)
                ? boostDisabled
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadEcoState(SafeProcessHandle process)
    {
        try
        {
            if (process.IsInvalid)
            {
                return null;
            }

            var state = new ProcessPowerThrottlingState { Version = NativeMethods.Power.Version };
            if (!NativeMethods.Power.GetProcessInformation(
                    process, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                return null;
            }

            return (state.StateMask & NativeMethods.Power.ExecutionSpeed) != 0;
        }
        catch
        {
            return null;
        }
    }

    private static bool TrySetEco(SafeProcessHandle process, bool enabled)
    {
        try
        {
            if (process.IsInvalid)
            {
                return false;
            }

            var state = new ProcessPowerThrottlingState
            {
                Version = NativeMethods.Power.Version,
                ControlMask = NativeMethods.Power.ExecutionSpeed,
                StateMask = enabled ? NativeMethods.Power.ExecutionSpeed : 0,
            };
            return NativeMethods.Power.SetProcessInformation(
                process, ProcessInformationClass.ProcessPowerThrottling,
                ref state, NativeMethods.Power.StateSize());
        }
        catch
        {
            return false;
        }
    }
}
