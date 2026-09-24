using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using kaliteConfig.Native;
using kaliteConfig.ProcessOptimizer.Models;
using kaliteConfig.ProcessOptimizer.Services;

namespace BoostVerify;

/// <summary>
/// Runtime probe for the Game Mode lever contract after the Stage 0 redesign
/// (see Docs/GameMode.md). Spawns sacrificial spinner children, runs the REAL
/// ForegroundBoosterService and BackgroundThrottleService against them, and
/// reads back what the OS reports.
///
/// Most of what it asserts is now that Game Mode does NOT do things:
///  - the game is raised to AboveNormal, never High;
///  - the game's affinity is untouched (no per-core clamp);
///  - the game is NOT confined to a CPU-set partition;
///  - no thread of the game is rewritten;
///  - the background process KEEPS its priority class - the old build demanded
///    BelowNormal here, but a Light throttle deliberately never changed the
///    priority class, and that mismatch is what Docs/ProcessControl.md recorded
///    as BoostVerify's "known failure";
///  - the priority-boost flag is left exactly as the user had it, for both.
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
    }

    private static void Info(string text) => Console.WriteLine($"  [INFO] {text}");

    /// <summary>Everything this probe reads back about one process.</summary>
    private sealed class ProcState
    {
        public uint PriorityClass;
        public int AffinityBits;
        public bool BoostReadable;
        public bool BoostDisabled;
        public bool Eco;
        public uint[] CpuSets = Array.Empty<uint>();
        public int ThreadCount;
        public int ThreadsAtHighest;
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "child")
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

        Console.WriteLine("=== BoostVerify: what Game Mode actually does ===\n");

        bool admin = false;
        try
        {
            admin = new System.Security.Principal.WindowsPrincipal(
                System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { }
        Console.WriteLine($"Elevated: {admin}{(admin ? "" : "  (WARNING: some checks may fail without admin)")}\n");

        int systemSets = SystemCpuSetCount();
        ulong? reserved = null;
        try { reserved = new kaliteConfig.Services.ReservedCpuSetsService().GetReservedCpuMask(); } catch { }
        Info($"{Environment.ProcessorCount} logical processors, {systemSets} system CPU sets");
        Info(reserved is > 0
            ? $"kernel ReservedCpuSets = 0x{reserved.Value:X} - Game Mode must not pin anything onto these"
            : "no kernel ReservedCpuSets mask set");

        // ── spawn the sacrificial children ──
        string exe = Environment.ProcessPath ?? "dotnet";
        var game = Spawn(exe);
        var bg = Spawn(exe);
        Thread.Sleep(1500); // let the children settle

        try
        {
            ProcState gameBefore = Read(game.Id);
            ProcState bgBefore = Read(bg.Id);

            Console.WriteLine("\nApplying the REAL paths:");
            Console.WriteLine("  ForegroundBoosterService.ApplyOptimization(game)");
            Console.WriteLine("  BackgroundThrottleService.ApplyThrottle(background, Light)");
            ForegroundBoosterService.ApplyOptimization(game.Id);
            string action = BackgroundThrottleService.ApplyThrottle(bg.Id, AggressivenessLevel.Light);
            Console.WriteLine($"  throttle reported: \"{action}\"\n");

            ProcState gameAfter = Read(game.Id);
            ProcState bgAfter = Read(bg.Id);

            // ── the game ──
            Console.WriteLine("- Game process -");
            Check("priority class == AboveNormal (0x8000)", gameAfter.PriorityClass == 0x8000,
                $"read 0x{gameAfter.PriorityClass:X}");
            Check("never raised to High (0x80)", gameAfter.PriorityClass != 0x80,
                "High starves the DWM/audio/driver threads the game waits on");
            Check("Efficiency mode OFF", !gameAfter.Eco, gameAfter.Eco ? "eco is ON" : "eco is off");
            Check("affinity untouched (no core clamp)", gameAfter.AffinityBits == gameBefore.AffinityBits,
                $"before 0x{gameBefore.AffinityBits:X}, after 0x{gameAfter.AffinityBits:X}");
            Check("threads untouched", gameAfter.ThreadsAtHighest == 0,
                $"{gameAfter.ThreadsAtHighest} of {gameAfter.ThreadCount} threads at Highest");
            Check("not confined to a CPU-set partition", NotConfined(gameAfter, systemSets),
                DescribeSets(gameAfter, systemSets, gameBefore));
            Check("priority-boost flag unchanged", BoostSame(gameBefore, gameAfter),
                BoostSame(gameBefore, gameAfter) ? "left as the user had it" : "BOOST FLAG WAS REWRITTEN");

            // ── the background process ──
            Console.WriteLine("\n- Background process (Light throttle) -");
            Check("priority class still Normal (0x20)", bgAfter.PriorityClass == 0x20,
                $"read 0x{bgAfter.PriorityClass:X} (the class is never demoted at any level)");
            Check("Efficiency mode ON", bgAfter.Eco, bgAfter.Eco ? "eco is on" : "eco was NOT set");
            Check("affinity untouched", bgAfter.AffinityBits == bgBefore.AffinityBits,
                $"before 0x{bgBefore.AffinityBits:X}, after 0x{bgAfter.AffinityBits:X}");
            Check("not confined to a CPU-set partition", NotConfined(bgAfter, systemSets),
                DescribeSets(bgAfter, systemSets, bgBefore));
            Check("priority-boost flag unchanged", BoostSame(bgBefore, bgAfter),
                BoostSame(bgBefore, bgAfter) ? "left as the user had it" : "BOOST FLAG WAS REWRITTEN");

            // ── restore what the real restore path would ──
            Console.WriteLine("\n- Restore -");
            if (gameBefore.PriorityClass != 0) SetPriorityClass(game.Id, gameBefore.PriorityClass);
            SetEco(game.Id, gameBefore.Eco);
            SetEco(bg.Id, bgBefore.Eco);

            ProcState gameRestored = Read(game.Id);
            ProcState bgRestored = Read(bg.Id);
            Check("game priority restored", gameRestored.PriorityClass == gameBefore.PriorityClass,
                $"0x{gameRestored.PriorityClass:X} == 0x{gameBefore.PriorityClass:X}");
            Check("game eco restored", gameRestored.Eco == gameBefore.Eco,
                $"eco {gameRestored.Eco}");
            Check("background eco restored", bgRestored.Eco == bgBefore.Eco,
                $"eco {bgRestored.Eco}");

            VerifyContentionSampler();
        }
        finally
        {
            try { game.Kill(); } catch { }
            try { bg.Kill(); } catch { }
        }

        Console.WriteLine($"\n=== {_pass} passed, {_fail} failed ===");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// The session monitor used to open a handle to every running process once a
    /// second, for the whole session, to work out who was using the CPU. It now
    /// answers the same question from one system call, so this measures both
    /// shapes against each other and checks the numbers are real.
    /// </summary>
    private static void VerifyContentionSampler()
    {
        Console.WriteLine("\n- Contention sampler (was: one handle per process, per tick) -");

        bool ok = SystemProcessCpuReader.TryRead(out var first);
        Check("one syscall returns every process's CPU time", ok && first.Count > 5,
            ok ? $"{first.Count} processes from one NtQuerySystemInformation call" : "reader returned nothing");
        if (!ok) return;

        Check("our own PID is in the snapshot", first.ContainsKey(Environment.ProcessId),
            $"pid {Environment.ProcessId}");

        // Burn CPU on purpose, then confirm this process's own figure moved. A wrong
        // struct offset would still return a full PID list - with zero or garbage
        // times - so this is what actually proves the field offsets are right.
        long before = first.TryGetValue(Environment.ProcessId, out long b) ? b : 0;
        var spin = Stopwatch.StartNew();
        double sink = 0;
        while (spin.ElapsedMilliseconds < 300) sink += Math.Sqrt(spin.Elapsed.TotalMilliseconds);
        GC.KeepAlive(sink);
        Thread.Sleep(150); // let the kernel fold our time in
        SystemProcessCpuReader.TryRead(out var second);
        long after = second.TryGetValue(Environment.ProcessId, out long a) ? a : 0;
        double consumedMs = (after - before) / 10_000.0;
        Check("tracks real CPU time", consumedMs > 100,
            $"~300 ms of spinning registered as {consumedMs:F0} ms of CPU time");

        double newMs = TimeNewShape(20);
        double oldMs = TimeOldShape(out int opened);
        Check("cheaper than a handle per process", newMs <= oldMs,
            $"{newMs:F2} ms per sample, vs {oldMs:F2} ms for "
            + $"{opened} OpenProcess/GetProcessTimes pairs (one Process object allocated and disposed each)"+
            $" - {(oldMs > 0 ? oldMs / newMs : 0):F1}x cheaper");
    }

    /// <summary>Wall time of one sample in the new shape, averaged.</summary>
    private static double TimeNewShape(int iterations)
    {
        SystemProcessCpuReader.TryRead(out _);
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) SystemProcessCpuReader.TryRead(out _);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }

    /// <summary>Wall time of one sample the way the removed monitor took it.</summary>
    private static double TimeOldShape(out int opened)
    {
        opened = 0;
        var sw = Stopwatch.StartNew();
        Process[] procs;
        try { procs = Process.GetProcesses(); } catch { return 0; }

        foreach (var proc in procs)
        {
            try
            {
                using var handle = NativeMethods.Handles.OpenProcess(
                    NativeMethods.ProcessAccess.QueryLimitedInformation, false, (uint)proc.Id);
                if (!handle.IsInvalid
                    && NativeMethods.Times.GetProcessTimes(handle, out _, out _, out var kernel, out var user))
                {
                    _ = kernel.ToTicks() + user.ToTicks();
                    opened++;
                }
            }
            catch { }
            finally { proc.Dispose(); }
        }

        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    private static Process Spawn(string exe) => Process.Start(new ProcessStartInfo
    {
        FileName = exe,
        Arguments = "child",
        CreateNoWindow = true,
        UseShellExecute = false,
    })!;

    /// <summary>
    /// A process counts as unconfined when it has no explicit default CPU sets
    /// (inheriting the whole machine) or its sets are the entire system list.
    /// Anything in between is the partition this redesign removed.
    /// </summary>
    private static bool NotConfined(ProcState s, int systemSets)
        => s.CpuSets.Length == 0 || (systemSets > 0 && s.CpuSets.Length >= systemSets);

    private static string DescribeSets(ProcState after, int systemSets, ProcState before)
    {
        if (after.CpuSets.Length == 0) return "no explicit sets (inherits the whole machine)";
        if (systemSets > 0 && after.CpuSets.Length >= systemSets) return $"all {systemSets} system sets";
        return $"CONFINED to {after.CpuSets.Length} of {systemSets} sets "
             + $"(was {(before.CpuSets.Length == 0 ? "unrestricted" : before.CpuSets.Length + " sets")})";
    }

    private static bool BoostSame(ProcState before, ProcState after)
        => !before.BoostReadable || !after.BoostReadable || before.BoostDisabled == after.BoostDisabled;

    private static ProcState Read(int pid)
    {
        var s = new ProcState();
        try
        {
            using var h = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation | NativeMethods.ProcessAccess.QueryLimitedInformation,
                false, (uint)pid);
            if (h.IsInvalid) return s;

            s.PriorityClass = NativeMethods.Priority.GetPriorityClass(h);

            if (NativeMethods.Priority.GetProcessPriorityBoost(h, out bool disabled))
            {
                s.BoostReadable = true;
                s.BoostDisabled = disabled;
            }

            var state = new ProcessPowerThrottlingState { Version = NativeMethods.Power.Version };
            if (NativeMethods.Power.GetProcessInformation(
                    h, ProcessInformationClass.ProcessPowerThrottling,
                    ref state, NativeMethods.Power.StateSize()))
            {
                s.Eco = (state.StateMask & NativeMethods.Power.ExecutionSpeed) != 0;
            }

            s.CpuSets = ReadProcessSets(h);
        }
        catch { }

        try
        {
            using var proc = Process.GetProcessById(pid);
            s.AffinityBits = (int)proc.ProcessorAffinity;
            foreach (ProcessThread? t in proc.Threads)
            {
                if (t == null) continue;
                s.ThreadCount++;
                using var th = NativeMethods.Handles.OpenThread(
                    NativeMethods.ThreadAccess.QueryInformation, false, (uint)t.Id);
                if (th.IsInvalid) continue;
                if (NativeMethods.Priority.GetThreadPriority(th) == NativeMethods.ThreadPriorityLevel.Highest)
                    s.ThreadsAtHighest++;
            }
        }
        catch { }

        return s;
    }

    private static void SetPriorityClass(int pid, uint cls)
    {
        try
        {
            using var h = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (!h.IsInvalid) NativeMethods.Priority.SetPriorityClass(h, cls);
        }
        catch { }
    }

    private static void SetEco(int pid, bool enabled)
    {
        try
        {
            using var h = NativeMethods.Handles.OpenProcess(
                NativeMethods.ProcessAccess.SetInformation, false, (uint)pid);
            if (h.IsInvalid) return;
            var state = new ProcessPowerThrottlingState
            {
                Version = NativeMethods.Power.Version,
                ControlMask = NativeMethods.Power.ExecutionSpeed,
                StateMask = enabled ? NativeMethods.Power.ExecutionSpeed : 0,
            };
            NativeMethods.Power.SetProcessInformation(
                h, ProcessInformationClass.ProcessPowerThrottling,
                ref state, NativeMethods.Power.StateSize());
        }
        catch { }
    }

    /// <summary>The process's explicit default CPU sets; empty when it inherits the machine.</summary>
    private static uint[] ReadProcessSets(SafeProcessHandle h)
    {
        try
        {
            if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, null, 0, out uint required))
            {
                if (Marshal.GetLastWin32Error() != 122) return Array.Empty<uint>();
            }
            if (required == 0) return Array.Empty<uint>();
            uint[] ids = new uint[required];
            if (!NativeMethods.CpuSets.GetProcessDefaultCpuSets(h, ids, required, out _)) return Array.Empty<uint>();
            return ids;
        }
        catch { return Array.Empty<uint>(); }
    }

    /// <summary>How many logical processors the system reports as CPU sets.</summary>
    private static int SystemCpuSetCount()
    {
        try
        {
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(null, 0, out uint needed, IntPtr.Zero, 0))
            {
                if (Marshal.GetLastWin32Error() != 122) return 0;
            }
            if (needed == 0 || needed > 64 * 1024 * 1024) return 0;
            byte[] buffer = new byte[needed];
            if (!NativeMethods.CpuSets.GetSystemCpuSetInformation(buffer, needed, out _, IntPtr.Zero, 0))
                return 0;

            int offset = 0, count = 0, guard = 0;
            while (offset + 20 <= buffer.Length && guard++ < 2048)
            {
                uint size = BitConverter.ToUInt32(buffer, offset);
                byte type = buffer[offset + 4];
                if (size < 20 || offset + size > buffer.Length) break;
                if (type == 0) count++; // CpuSetInformation
                offset += (int)size;
            }
            return count;
        }
        catch { return 0; }
    }
}
