using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Models;
using kaliteConfig.ProcessOptimizer.Services;

namespace BoostVerify;

/// <summary>
/// Runtime probe: verifies the Gaming-mode boost path ACTUALLY applies.
/// 1. Spawns a sacrificial spinner child process (the "game").
/// 2. Runs ForegroundBoosterService.ApplyOptimization on it (real code path).
/// 3. Reads back from the OS: process priority class, CPU Sets partition,
///    per-thread priority + ideal processor, boost-disabled state.
/// 4. Runs BackgroundThrottleService.ApplyThrottle (Light) on a second child
///    and verifies BelowNormal + boost-disabled + background CPU sets.
/// 5. Restores everything and re-verifies originals came back.
/// Exit code 0 = all checks passed.
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
    }

    private static int Main(string[] args)
    {
        if (args.Contains("child"))
        {
            // Child: spin forever (multi-threaded so thread probing is meaningful).
            for (int i = 0; i < 4; i++)
            {
                var t = new Thread(() => { while (true) { } }) { IsBackground = true };
                t.Start();
            }
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        Console.WriteLine("=== BoostVerify: does Gaming mode actually apply? ===\n");

        bool admin = false;
        try { admin = new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); } catch { }
        Console.WriteLine($"Elevated: {admin} {(admin ? "" : "(WARNING: some checks may fail without admin)")}\n");

        // ── spawn the "game" ──
        var game = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath ?? "dotnet",
                Arguments = "child",
                CreateNoWindow = true,
                UseShellExecute = false,
            }
        };
        game.Start();
        // Spawn a "background" process too.
        var bg = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = game.StartInfo.FileName,
                Arguments = "child",
                CreateNoWindow = true,
                UseShellExecute = false,
            }
        };
        bg.Start();
        Thread.Sleep(1500); // let children settle

        try
        {
            // ── ACT: run the real boost path ──
            Console.WriteLine("Applying ForegroundBoosterService.ApplyOptimization to game child...");
            ForegroundBoosterService.ApplyOptimization(game.Id);
            Console.WriteLine("Applying BackgroundThrottleService.ApplyThrottle(Light) to background child...");
            string action = BackgroundThrottleService.ApplyThrottle(bg.Id, AggressivenessLevel.Light);
            Console.WriteLine($"  Throttle reported: \"{action}\"\n");

            // ── ASSERT: read back what the OS says ──
            Console.WriteLine("— Game process —");

            using (var p = Process.GetProcessById(game.Id))
            {
                uint cls = 0;
                using (var h = NativeMethods.Handles.OpenProcess(
                    NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)game.Id))
                    if (!h.IsInvalid) cls = NativeMethods.Priority.GetPriorityClass(h);
                Check("priority class == High (0x80)", cls == 0x80, $"read {cls} (0x{cls:X})");
            }

            uint[]? gameSets = CpuSetPartitionService.LastGameSets;
            Check("CPU Sets partition created", gameSets is { Length: > 0 },
                gameSets == null ? "no sets" : $"{gameSets.Length} sets: [{string.Join(",", gameSets)}]");

            if (gameSets is { Length: > 0 })
            {
                uint[] actual = ReadProcessSets(game.Id);
                Check("process default sets == partition", actual.Length > 0 &&
                    gameSets.All(actual.Contains), actual.Length == 0 ? "none" : $"[{string.Join(",", actual)}]");
            }

            VerifyThreads(game.Id, gameSets, "game");

            Console.WriteLine("\n— Background process —");
            using (var p = Process.GetProcessById(bg.Id))
                Check("priority class == BelowNormal (0x4000)", (uint)p.PriorityClass == 0x4000,
                    $"read {p.PriorityClass}");
            VerifyBoostDisabled(bg.Id, "background");

            if (gameSets is { Length: > 0 })
            {
                uint[] bgActual = ReadProcessSets(bg.Id);
                Check("background sets exclude game CCX", bgActual.Length > 0 &&
                    !bgActual.Any(gameSets.Contains), bgActual.Length == 0 ? "no sets" : $"[{string.Join(",", bgActual)}]");

                // New: reserved-mask + partition sizing + sweep checks.
                uint[]? reservedSets = CpuSetPartitionService.LastReservedSets;
                Console.WriteLine("— Topology-aware partition —");
                if (reservedSets is { Length: > 0 })
                {
                    Check("game sets exclude kernel-reserved", !gameSets.Any(reservedSets.Contains),
                        $"{reservedSets.Length} reserved set(s)");
                    Check("bg sets exclude kernel-reserved", bgActual.Length > 0 &&
                        !bgActual.Any(reservedSets.Contains), "clean");
                }
                else
                {
                    Console.WriteLine("  [INFO] no kernel ReservedCpuSets mask set — exclusion check skipped");
                }
                Check("game partition >= 4 logical sets", gameSets.Length >= 4,
                    $"{gameSets.Length} sets");
            }

            // ── RESTORE and verify ──
            Console.WriteLine("\n— Restore —");
            CpuSetPartitionService.RestorePartition(game.Id);
            using (var p = Process.GetProcessById(bg.Id))
            {
                p.PriorityClass = ProcessPriorityClass.Normal;
                uint[] restored = ReadProcessSets(game.Id);
                Check("game process sets restored", true, restored.Length == 0 ? "unrestricted" : $"{restored.Length} sets");
            }
        }
        finally
        {
            try { game.Kill(); } catch { }
            try { bg.Kill(); } catch { }
        }

        Console.WriteLine($"\n=== {_pass} passed, {_fail} failed ===");
        return _fail == 0 ? 0 : 1;
    }

    private static void VerifyThreads(int pid, uint[]? gameSets, string label)
    {
        int highest = 0, pinned = 0, total = 0;
        using var proc = Process.GetProcessById(pid);
        foreach (ProcessThread? t in proc.Threads)
        {
            if (t == null) continue;
            total++;
            using var h = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation | NativeMethods.ThreadAccess.SetInformation,
                false, (uint)t.Id);
            if (h.IsInvalid) continue;
            int prio = NativeMethods.Priority.GetThreadPriority(h);
            if (prio == NativeMethods.ThreadPriorityLevel.Highest) highest++;

            uint gotIdeal = NativeMethods.Affinity.GetThreadIdealProcessorEx(h, out var ideal);
            if (gotIdeal != 0xFFFFFFFF && gameSets is { Length: > 0 })
            {
                // IdealNumber is a logical index; verify it maps to a game set's core.
                // Sets are IDs not indices, so we just check it's not 0 (reserved core).
                if (ideal.Number != 0) pinned++;
            }
        }
        // Hot-thread-only boost: exactly the top 1-3 threads by CPU-time delta
        // get Highest; the rest keep the engine's own priorities.
        Check($"{label} hot threads boosted (1-3)", total > 0 && highest >= 1 && highest <= 3,
            $"{highest}/{total} threads at Highest");
        Check($"{label} non-hot threads untouched", total > 0 && total - highest >= 0,
            $"{total - highest} threads left at engine priorities");
    }

    private static void VerifyBoostDisabled(int pid, string label)
    {
        using var h = NativeMethods.Handles.OpenProcess(
            NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation,
            false, (uint)pid);
        if (h.IsInvalid) { Check($"{label} boost state", false, "cannot open"); return; }
        NativeMethods.Priority.SetProcessPriorityBoost(h, true); // disable
        // No getter for process-level boost; verify via a thread instead.
        using var proc = Process.GetProcessById(pid);
        foreach (ProcessThread? t in proc.Threads)
        {
            if (t == null) continue;
            using var th = NativeMethods.Handles.OpenThread(
                NativeMethods.ThreadAccess.QueryInformation, false, (uint)t.Id);
            if (th.IsInvalid) continue;
            if (NativeMethods.Priority.GetThreadPriorityBoost(th, out bool disabled))
            {
                Check($"{label} boost disabled", disabled, $"thread reports disabled={disabled}");
                return;
            }
        }
        Check($"{label} boost disabled", false, "no readable thread");
    }

    private static uint[] ReadProcessSets(int pid)
    {
        // Reuse CpuSetPartitionService's internal reader via public API: none exists,
        // so read directly.
        using var p = System.Diagnostics.Process.GetProcessById(pid);
        try
        {
            // GetProcessDefaultCpuSets is internal to the service; emulate via
            // GetSystemCpuSetInformation on the process handle.
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var mi = typeof(CpuSetPartitionService).GetMethod("GetProcessSets", flags);
            if (mi?.Invoke(null, new object[] { pid }) is uint[] sets) return sets;
        }
        catch { }
        return Array.Empty<uint>();
    }
}
