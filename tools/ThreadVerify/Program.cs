using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using kaliteConfig.Native;
using kaliteConfig.Services;

namespace ThreadVerify;

/// <summary>
/// Runtime probe for the thread tuner's ideal-processor control, using the REAL
/// <see cref="ThreadTuningService"/> against a real thread of a sacrificial
/// child process.
///
/// It exists because the control reported success no matter what the kernel
/// did: SetThreadIdealProcessorEx returns BOOL, the return value was ignored,
/// and SetThreadIdealProcessorEx rejects a processor the thread's affinity mask
/// excludes (error 31) - so "Apply ideal processor" could do nothing at all and
/// still say "Ideal processor set to CPU 10". GetThreadIdealProcessorEx was
/// declared with the wrong return type too, so a failed read reported a bogus
/// processor instead of failing.
///
/// Exit code 0 = every check passed.
/// </summary>
internal static class Program
{
    private static int _pass, _fail;

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}: {detail}");
    }

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("child"))
        {
            for (int i = 0; i < 4; i++)
            {
                var t = new Thread(() => { while (true) { } }) { IsBackground = true };
                t.Start();
            }
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        Console.WriteLine("=== ThreadVerify: does the ideal-processor control tell the truth? ===\n");

        int cpus = Environment.ProcessorCount;
        Console.WriteLine($"Logical processors: {cpus}");
        if (cpus < 5)
        {
            Console.WriteLine("Need at least 5 logical processors for the affinity-vs-ideal check - skipping.");
            return 0;
        }

        var child = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? "dotnet",
            Arguments = "child",
        })!;
        Thread.Sleep(1500); // let the child's threads start

        var tuner = new ThreadTuningService();
        try
        {
            var thread = child.Threads.Cast<ProcessThread>().First();

            // ---- 1. read path: a failed read must fail, not invent a CPU ----
            var (group, number) = await tuner.GetIdealProcessorAsync((uint)thread.Id);
            Console.WriteLine($"\n-- thread {thread.Id}: ideal reported as group {group} CPU {number}");
            Check("read returns an in-range processor",
                group == 0 && number < cpus,
                $"group {group} CPU {number} (of {cpus})");

            // ---- 2. success path: apply a CPU inside the affinity mask ----
            var (_, mask) = await tuner.GetAffinityStateAsync((uint)thread.Id);
            int inside = FirstCpuIn(mask);
            await tuner.SetIdealProcessorAsync((uint)thread.Id, 0, (byte)inside);
            var afterInside = await tuner.GetIdealProcessorAsync((uint)thread.Id);
            Check("apply inside the affinity mask sticks",
                afterInside is (0, var n1) && n1 == inside,
                $"asked CPU {inside}, kernel reports group {afterInside.Group} CPU {afterInside.Number}");

            // ---- 3. the bug: a CPU the affinity mask excludes -------------------
            // Narrow the thread to the lowest few CPUs, then ask for one outside
            // that mask. The kernel refuses (error 31); the control must say so.
            int keep = 4;
            ulong narrowed = 0;
            for (int i = 0; i < keep; i++) narrowed |= 1UL << i;
            await tuner.SetAffinityAsync((uint)thread.Id, null, narrowed);
            int outside = cpus - 1;

            string message = "(no error thrown - the failure was swallowed)";
            bool threw = false;
            try
            {
                await tuner.SetIdealProcessorAsync((uint)thread.Id, 0, (byte)outside);
            }
            catch (Exception ex)
            {
                threw = true;
                message = ex.Message;
            }
            Check("apply outside the affinity mask is reported, not silently ignored",
                threw, threw ? message : "SetIdealProcessorAsync returned success for an impossible CPU");
            Check("rejection names the affinity mask and the CPUs that are allowed",
                threw && message.Contains("affinity mask", StringComparison.OrdinalIgnoreCase)
                      && message.Contains("1", StringComparison.Ordinal),
                message);

            // ---- 4. read-back after the failed write is unchanged --------------
            var afterOutside = await tuner.GetIdealProcessorAsync((uint)thread.Id);
            Check("failed write left the live value untouched",
                afterOutside.Number != outside,
                $"still group {afterOutside.Group} CPU {afterOutside.Number}");

            // ---- 5. with a wide mask some CPU must still apply end to end -------
            ulong widened = 0;
            for (int i = 0; i < cpus; i++) widened |= 1UL << i;
            await tuner.SetAffinityAsync((uint)thread.Id, null, widened);

            int appliedOverall = -1;
            for (int cpu = 0; cpu < cpus; cpu++)
            {
                try
                {
                    await tuner.SetIdealProcessorAsync((uint)thread.Id, 0, (byte)cpu);
                    var back = await tuner.GetIdealProcessorAsync((uint)thread.Id);
                    if (back.Number == cpu) { appliedOverall = cpu; break; }
                }
                catch { /* try the next CPU */ }
            }
            Check("a CPU the thread is allowed to use applies and reads back",
                appliedOverall >= 0,
                appliedOverall >= 0 ? $"CPU {appliedOverall}" : "no CPU was accepted");

            // ---- 6. reserved CPU-set partition CPUs are refused WITH that reason --
            // This machine reserves 0xFC00 (CPUs 10-15) for the CPU-set partition;
            // threads outside it can never take those as their ideal processor.
            ulong? reserved = new ReservedCpuSetsService().GetReservedCpuMask();
            if (reserved is { } reservedMask && reservedMask != 0)
            {
                int reservedCpu = FirstCpuIn(reservedMask);
                string reservedMessage = "(no error thrown)";
                bool refused = false;
                try
                {
                    await tuner.SetIdealProcessorAsync((uint)thread.Id, 0, (byte)reservedCpu);
                }
                catch (Exception ex) { refused = true; reservedMessage = ex.Message; }
                Check($"reserved CPU {reservedCpu} (mask 0x{reservedMask:X}) is refused with the reservation explained",
                    refused && reservedMessage.Contains("reserved CPU-set partition", StringComparison.Ordinal),
                    reservedMessage);
            }
            else
            {
                Console.WriteLine("  [SKIP] no reserved CPU-set mask on this machine - reservation check not applicable");
            }
        }
        finally
        {
            try { child.Kill(); } catch { }
        }

        Console.WriteLine($"\n=== {_pass} passed, {_fail} failed ===");
        return _fail == 0 ? 0 : 1;
    }

    private static int FirstCpuIn(ulong mask)
    {
        for (int i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0) return i;
        }
        return 0;
    }
}
