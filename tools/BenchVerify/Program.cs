using System;
using System.Collections.Generic;
using System.Linq;
using kaliteConfig.Models;
using kaliteConfig.Services;

namespace BenchVerify
{
    /// <summary>
    /// Phase-1 stats verification. Hand-computed expectations, no fakes:
    /// the REAL BenchmarkStatsService. Exit code 0 = all pass.
    /// Usage: dotnet run --project tools/BenchVerify
    /// </summary>
    internal static class Program
    {
        private static int _failures;

        private static void Check(bool ok, string what, string detail = "")
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}" + (detail.Length > 0 ? $" ({detail})" : ""));
            if (!ok) _failures++;
        }

        private static bool Near(double a, double b, double tol = 0.05) => Math.Abs(a - b) <= tol;

        private static int Main()
        {
            Console.WriteLine("=== BenchVerify Phase-1 (REAL BenchmarkStatsService) ===");

            // Dataset A: 90 x 16.666ms (60fps) + 10 x 33.333ms (30fps), no trim.
            // total = 1833.33ms, avg = 100/1.83333 = 54.545fps.
            var a = new List<float>();
            for (int i = 0; i < 90; i++) a.Add(16.666f);
            for (int i = 0; i < 10; i++) a.Add(33.333f);
            var sa = BenchmarkStatsService.Compute(a, trimStartFraction: 0, trimEndFraction: 0);
            Check(sa.FrameCount == 100, "A frame count", sa.FrameCount.ToString());
            Check(Near(sa.AverageFps, 54.545, 0.1), "A average = frames/totalTime", sa.AverageFps.ToString("F3"));
            Check(Near(sa.MedianFps, 60.0), "A median", sa.MedianFps.ToString("F3"));
            Check(Near(sa.MinFps, 30.0, 0.1), "A min", sa.MinFps.ToString("F3"));
            Check(Near(sa.MaxFps, 60.0, 0.1), "A max", sa.MaxFps.ToString("F3"));
            Check(Near(sa.P1Fps, 30.0, 0.2), "A P1", sa.P1Fps.ToString("F3"));
            Check(Near(sa.P02Fps, 30.0, 0.2), "A P0.2", sa.P02Fps.ToString("F3"));
            Check(Near(sa.Low1CountFps, 30.0, 0.2), "A 1% low (count)", sa.Low1CountFps.ToString("F3"));
            Check(Near(sa.Low1TimeFps, 30.0, 0.2), "A 1% low (time-weighted)", sa.Low1TimeFps.ToString("F3"));
            Check(Near(sa.Low01TimeFps, 30.0, 0.2), "A 0.1% low (time-weighted)", sa.Low01TimeFps.ToString("F3"));
            Check(sa.StutterCount == 0, "A no stutter (33ms < 2.5x median)", sa.StutterCount.ToString());
            Check(Near(sa.LowFpsTimeMs, 0.0), "A no low-FPS time", sa.LowFpsTimeMs.ToString("F1"));

            // Dataset B: 60 x 16.666 + 1 x 60ms spike + 60 x 16.666 -> exactly 1 stutter.
            var b = new List<float>();
            for (int i = 0; i < 60; i++) b.Add(16.666f);
            b.Add(60.0f);
            for (int i = 0; i < 60; i++) b.Add(16.666f);
            var sb = BenchmarkStatsService.Compute(b, trimStartFraction: 0, trimEndFraction: 0);
            Check(sb.StutterCount == 1, "B one stutter event", sb.StutterCount.ToString());
            if (sb.StutterEvents.Count == 1)
            {
                Check(Near(sb.StutterEvents[0].TimestampMs, 1000.0, 2.0), "B stutter timestamp ~1000ms", sb.StutterEvents[0].TimestampMs.ToString("F1"));
                Check(Near(sb.StutterEvents[0].Severity, 3.6, 0.2), "B stutter severity ~3.6x", sb.StutterEvents[0].Severity.ToString("F2"));
            }
            else Check(false, "B stutter event list has 1 entry", sb.StutterEvents.Count.ToString());
            Check(Near(sb.LowFpsTimeMs, 60.0, 0.5), "B low-FPS time = 60ms", sb.LowFpsTimeMs.ToString("F1"));

            // Edge: empty.
            var se = BenchmarkStatsService.Compute(new List<float>(), 0, 0);
            Check(se.FrameCount == 0 && se.StutterCount == 0, "empty input", $"frames={se.FrameCount}");

            // Edge: single frame 16.666ms -> everything 60fps, no stutter.
            var s1 = BenchmarkStatsService.Compute(new List<float> { 16.666f }, 0, 0);
            Check(s1.FrameCount == 1, "1-frame count");
            Check(Near(s1.AverageFps, 60.0, 0.1), "1-frame average", s1.AverageFps.ToString("F3"));
            Check(Near(s1.P1Fps, 60.0, 0.1) && Near(s1.Low1CountFps, 60.0, 0.1), "1-frame P1 + low", $"P1={s1.P1Fps:F2}");
            Check(s1.StutterCount == 0, "1-frame no stutter");

            // Edge: all equal 10 x 16.666 -> avg/P1/lows all 60, no stutter.
            var eq = Enumerable.Repeat(16.666f, 10).ToList();
            var sq = BenchmarkStatsService.Compute(eq, 0, 0);
            Check(Near(sq.AverageFps, 60.0, 0.1), "equal average", sq.AverageFps.ToString("F3"));
            Check(Near(sq.P1Fps, 60.0, 0.1) && Near(sq.Low1TimeFps, 60.0, 0.1), "equal P1 + time low");
            Check(sq.StutterCount == 0, "equal no stutter");

            // Windowing: trim 5% head off dataset A drops 5 frames -> 95 frames.
            var sw = BenchmarkStatsService.Compute(a, trimStartFraction: 0.05, trimEndFraction: 0);
            Check(sw.FrameCount == 95, "5% trim", sw.FrameCount.ToString());
            Check(Near(sa.ArithmeticAvgFps, 57.0, 0.1), "A arithmetic avg (mean of FPS)", sa.ArithmeticAvgFps.ToString("F3"));
            Check(Near(sa.HarmonicAvgFps, sa.AverageFps, 0.001), "A harmonic avg == frames/time", sa.HarmonicAvgFps.ToString("F3"));
            Check(Near(sa.Low01CountFps, 30.0, 0.2), "A 0.1% low (count)", sa.Low01CountFps.ToString("F3"));
            // Count-based lows survive isolated giant stalls that define time-weighted lows.
            var stallFrames = new List<float> { 900f };
            for (int i = 0; i < 999; i++) stallFrames.Add(16.666f);
            var stallStats = BenchmarkStatsService.Compute(stallFrames, 0, 0);
            Check(stallStats.Low1TimeFps < 5, "stall defines time-weighted low", stallStats.Low1TimeFps.ToString("F1"));
            Check(stallStats.Low1CountFps > 50, "count-based 1% low survives stall", stallStats.Low1CountFps.ToString("F1"));
            Check(Near(stallStats.Low01CountFps, 1.1, 0.2), "0.1% of 1000 frames is 1 frame (limit)", stallStats.Low01CountFps.ToString("F1"));
            var bigFrames = new List<float>();
            for (int i = 0; i < 3; i++) bigFrames.Add(900f);
            for (int i = 0; i < 20000; i++) bigFrames.Add(16.666f);
            var bigStats = BenchmarkStatsService.Compute(bigFrames, 0, 0);
            Check(bigStats.Low01CountFps > 40, "0.1% low robust at real run sizes", bigStats.Low01CountFps.ToString("F1"));

            VerifyCsvParsing();
            VerifySensorStats();
            VerifyImport();
            VerifyWindowing();
            VerifyViewerStats();

            Console.WriteLine();
            Console.WriteLine(_failures == 0 ? "=== ALL CHECKS PASSED ===" : $"=== {_failures} CHECK(S) FAILED ===");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// CSV tests use the REAL header line captured from PresentMon 2.5.1
        /// (default schema smoke run) - header lookup, never index.
        /// </summary>
        private static void VerifyCsvParsing()
        {
            Console.WriteLine("--- CSV parsing (PresentMon 2.5.1 default schema) ---");
            string header = "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,TimeInMs,MsBetweenSimulationStart,MsBetweenPresents,MsBetweenDisplayChange,MsInPresentAPI,MsRenderPresentLatency,MsUntilDisplayed,CPUStartTimeInMs,MsBetweenAppStart,MsCPUBusy,MsCPUWait,MsGPULatency,MsGPUTime,MsGPUBusy,MsGPUWait,MsAnimationError,AnimationTime,MsFlipDelay,MsAllInputToPhotonLatency,MsClickToPhotonLatency";
            var lines = new[]
            {
                header,
                "Game.exe,1234,0x1,DXGI,1,0,1,Composed: Flip,7.2,NA,16.666,NA,0.05,0.3,NA,4.1,16.6,16.6,0.05,3.2,0.18,0.15,0.02,NA,NA,NA,NA,NA",
                "Other.exe,9999,0x2,DXGI,1,0,1,Composed: Flip,23.9,NA,16.666,NA,0.05,0.3,NA,20.8,16.6,16.6,0.05,3.2,0.18,0.15,0.02,NA,NA,NA,NA,NA",
                "Game.exe,1234,0x1,DXGI,1,0,1,Composed: Flip,40.5,NA,NA,NA,0.05,0.3,NA,37.4,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA,NA",
                "Game.exe,1234,0x1,DXGI,1,0,1,Composed: Flip,57.2,NA,33.333,NA,0.05,0.3,NA,54.1,33.3,33.3,0.05,3.2,0.18,0.15,0.02,NA,NA,NA,NA,NA",
            };
            var p = BenchmarkPresentMonCsv.ParseLines(lines, 1234);
            Check(p.HadProcessIdColumn, "CSV PID column detected");
            Check(p.RowsTotal == 4, "CSV rows total", p.RowsTotal.ToString());
            Check(p.RowsKept == 2, "CSV rows kept (2 game frames)", p.RowsKept.ToString());
            Check(p.RowsSkippedOtherPid == 1, "CSV other-PID skipped", p.RowsSkippedOtherPid.ToString());
            Check(p.RowsSkippedBadValue == 1, "CSV NA frametime skipped", p.RowsSkippedBadValue.ToString());
            Check(p.FrametimesMs.Length == 2
                && Math.Abs(p.FrametimesMs[0] - 16.666) < 0.01
                && Math.Abs(p.FrametimesMs[1] - 33.333) < 0.01, "CSV frametime values");

            // Fallback schema (--v2_metrics): FrameTime column, no MsBetweenPresents.
            var v2 = new[]
            {
                "Application,ProcessID,SwapChainAddress,PresentRuntime,SyncInterval,PresentFlags,AllowsTearing,PresentMode,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPULatency,GPUTime,GPUBusy,GPUWait,DisplayLatency,DisplayedTime,AnimationError,AnimationTime,MsFlipDelay,AllInputToPhotonLatency,ClickToPhotonLatency",
                "Game.exe,1234,0x1,DXGI,1,0,1,Composed: Flip,1000,16.7,16.7,0,0,0.2,0.2,0,0,0,NA,NA,NA,NA,NA",
            };
            var p2 = BenchmarkPresentMonCsv.ParseLines(v2, 1234);
            Check(p2.RowsKept == 1 && Math.Abs(p2.FrametimesMs[0] - 16.7) < 0.01, "CSV v2 FrameTime fallback");

            // End-to-end: parsed CSV frames feed the stats engine.
            var stats = BenchmarkStatsService.Compute(p.FrametimesMs.ToList(), 0, 0);
            Check(stats.FrameCount == 2, "CSV->stats frame count");
            Check(Math.Abs(stats.AverageFps - 40.0) < 0.5, "CSV->stats average ~40fps", stats.AverageFps.ToString("F2"));
        }

        private static void VerifySensorStats()
        {
            Console.WriteLine("--- sensor stats (proportional join) ---");
            DateTime t0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var samples = new List<SensorSample>
            {
                new() { Timestamp = t0.AddMilliseconds(0), GpuTempC = 60, GpuPowerW = 200, GpuUtilPct = 90, CpuPct = 30, ThrottleFlag = false },
                new() { Timestamp = t0.AddMilliseconds(500), GpuTempC = 70, GpuPowerW = 220, GpuUtilPct = 95, CpuPct = 40, ThrottleFlag = false },
                new() { Timestamp = t0.AddMilliseconds(1000), GpuTempC = 84, GpuPowerW = 250, GpuUtilPct = 99, CpuPct = 50, ThrottleFlag = true },
                new() { Timestamp = t0.AddMilliseconds(1500), GpuTempC = 80, GpuPowerW = null, GpuUtilPct = null, CpuPct = null, ThrottleFlag = false },
            };
            var sum = BenchmarkSensorStats.Summarize(samples);
            Check(sum.GpuTempC.HasData && Math.Abs(sum.GpuTempC.Avg - 73.5) < 0.01, "sensor temp avg", sum.GpuTempC.Avg.ToString("F2"));
            Check(Math.Abs(sum.GpuTempC.Min - 60) < 0.01 && Math.Abs(sum.GpuTempC.Max - 84) < 0.01, "sensor temp min/max");
            Check(Math.Abs(sum.GpuPowerW.Avg - 223.333) < 0.01, "sensor power avg skips null", sum.GpuPowerW.Avg.ToString("F3"));
            Check(!BenchmarkSensorStats.Summarize(new List<SensorSample>()).GpuTempC.HasData, "sensor empty = no data");
            Check(sum.ThrottleMarks == 1, "sensor throttle count", sum.ThrottleMarks.ToString());
            Check(sum.ThrottleFracs.Count == 1 && Math.Abs(sum.ThrottleFracs[0] - 1000.0 / 1500.0) < 0.01, "sensor throttle frac", sum.ThrottleFracs[0].ToString("F3"));

            var at0 = BenchmarkSensorStats.NearestAt(samples, 0.0);
            var atMid = BenchmarkSensorStats.NearestAt(samples, 0.5);
            var at1 = BenchmarkSensorStats.NearestAt(samples, 1.0);
            Check(at0?.GpuTempC == 60, "join frac 0 -> first");
            Check(atMid?.GpuTempC == 84, "join frac 0.5 -> round(1.5)=2", atMid?.GpuTempC.ToString() ?? "?");
            Check(at1?.GpuTempC == 80, "join frac 1 -> last");
            Check(BenchmarkSensorStats.NearestAt(new List<SensorSample>(), 0.5) == null, "join empty -> null");

            var series = BenchmarkSensorStats.GpuUtilSeries(samples, 4);
            Check(series.Count == 4 && Math.Abs(series[0] - 90) < 0.01
                && Math.Abs(series[2] - 99) < 0.01 && double.IsNaN(series[3]),
                "util series resample (null -> NaN gap)", string.Join(",", series));
        }

        private static void VerifyWindowing()
        {
            Console.WriteLine("--- windowing (warmup/cooldown trim) ---");
            // 10 s run at 60fps + one 900 ms stall at the head: untrimmed,
            // the stall alone defines the time-weighted 1% low (~1.1 FPS).
            var frames = new List<float>();
            var stamps = new List<double>();
            frames.Add(900f); stamps.Add(0);
            for (int i = 1; i < 600; i++) { frames.Add(16.666f); stamps.Add(i * 16.666); }
            var raw = BenchmarkStatsService.Compute(frames, 0, 0);
            Check(raw.Low1TimeFps < 5, "untrimmed stall defines 1% low", raw.Low1TimeFps.ToString("F1"));
            var win = BenchmarkStatsService.WindowByTime(frames, stamps);
            Check(win.Count == 418, "head 2s + tail 1s trimmed", win.Count.ToString());
            var trimmed = BenchmarkStatsService.Compute(win, 0, 0);
            Check(trimmed.Low1TimeFps > 50, "trimmed 1% low sane", trimmed.Low1TimeFps.ToString("F1"));
            var fallback = BenchmarkStatsService.WindowByTime(frames, null);
            Check(fallback.Count == frames.Count, "null stamps -> untrimmed");
            var short_ = BenchmarkStatsService.WindowByTime(
                new List<float> { 16.6f }, new List<double> { 0 });
            Check(short_.Count == 1, "short run never emptied");
        }

        private static void VerifyViewerStats()
        {
            Console.WriteLine("--- viewer stats (percentiles, MA, pie, variance) ---");
            var frames = new List<float>();
            for (int i = 0; i < 90; i++) frames.Add(16.666f);
            for (int i = 0; i < 10; i++) frames.Add(33.333f);
            var sum = BenchmarkStatsService.SummarizeFrametimes(frames);
            Check(Near(sum.P1, 16.666, 0.01), "frametime P1", sum.P1.ToString("F3"));
            Check(Near(sum.Avg, 18.333, 0.05), "frametime avg", sum.Avg.ToString("F3"));
            Check(Near(sum.P99, 33.333, 0.05), "frametime P99", sum.P99.ToString("F3"));
            Check(Near(sum.HighAvg1, 33.333, 0.05), "1% high avg", sum.HighAvg1.ToString("F3"));
            Check(sum.Frames == 100, "frametime frame count");

            var stamps = new List<double>();
            for (int i = 0; i < 100; i++) stamps.Add(i * 10.0);
            var ma = BenchmarkStatsService.MovingAverage(frames, stamps, 500);
            Check(ma.Count == 100, "MA length");
            Check(ma.All(v => v > 15 && v < 35), "MA bounded");
            Check(Near(ma[^1], frames.Average(f => (double)f), 5.0), "MA converges", ma[^1].ToString("F2"));

            var bd = BenchmarkStatsService.Breakdown(frames);
            Check(Near(bd.TotalMs, bd.SmoothMs + bd.LowOnlyMs + bd.StutterMs, 0.5), "breakdown partitions total",
                $"{bd.SmoothMs:F0}+{bd.LowOnlyMs:F0}+{bd.StutterMs:F0}={bd.TotalMs:F0}");
            var spike = new List<float> { 16.6f, 16.6f, 200f, 16.6f };
            var bd2 = BenchmarkStatsService.Breakdown(spike);
            Check(bd2.StutterMs > 100, "spike counted as stutter", bd2.StutterMs.ToString("F0"));

            var bands = BenchmarkStatsService.VarianceBands(new List<float> { 10f, 10.5f, 14f, 30f });
            Check(bands.Under1 == 1 && bands.Under5 == 1 && bands.Under10 == 0 && bands.Over10 == 1,
                "variance bands", $"{bands.Under1}/{bands.Under5}/{bands.Under10}/{bands.Over10}");

            var th = BenchmarkStatsService.ThresholdCounts(new List<float> { 50f, 10f, 5f });
            Check(th.Count == 4 && th[0].Count == 1 && th[1].Count == 1 && th[2].Count == 2 && th[3].Count == 3,
                "threshold counts", string.Join(",", th.Select(t => t.Count)));
        }

        private static void VerifyImport()
        {
            Console.WriteLine("--- import (verified CX schema) ---");
            string cxJson = "{"
                + "\"Info\": {\"GameName\": \"TestGame\", \"ProcessName\": \"TestGame.exe\","
                + " \"CreationDate\": \"2026-09-01T12:00:00\", \"Comment\": \"hi\"},"
                + "\"Runs\": ["
                + " {\"CaptureData\": {\"TimeInSeconds\": [0.0, 0.016, 0.05],"
                + "  \"MsBetweenPresents\": [16.0, 16.0, 34.0]}},"
                + " {\"SensorData\": {}},"
                + " {\"CaptureData\": {\"TimeInSeconds\": [0.0, 0.02],"
                + "  \"MsBetweenPresents\": [20.0, 20.0]}}"
                + "]}";
            var cx = BenchmarkImportService.ImportCapFrameXJson(cxJson, "cx.json");
            Check(cx.Count == 2, "CX runs parsed (sensor-only skipped)", cx.Count.ToString());
            Check(cx[0].Game == "TestGame" && cx[0].Exe == "TestGame.exe", "CX game/exe from Info");
            Check(cx[0].FrametimesMs.Length == 3, "CX frames", cx[0].FrametimesMs.Length.ToString());
            Check(Math.Abs(cx[0].TimestampsMs[2] - 50.0) < 0.5, "CX timestamps s->ms relative",
                cx[0].TimestampsMs[2].ToString("F1"));
            Check(cx[0].Comment == "hi", "CX comment from Info");
            Check(cx[1].Game == "TestGame #3", "CX multi-run naming (position-kept)", cx[1].Game);

            bool threw = false;
            try { BenchmarkImportService.ImportCapFrameXJson("{\"foo\": 1}", "x.json"); }
            catch { threw = true; }
            Check(threw, "CX rejects non-record JSON");

            string header = "Application,ProcessID,MsBetweenPresents,TimeInMs";
            var csvLines = new[]
            {
                "//GameName=CXGame",
                "//ProcessName=CXGame.exe",
                header,
                "CXGame.exe,111,16.666,7.2",
                "CXGame.exe,111,33.333,23.9",
                "Other.exe,222,16.666,5.0",
            };
            var groups = BenchmarkImportService.ImportPresentMonCsv(csvLines, "pm.csv");
            Check(groups.Count == 2, "CSV groups by PID (// headers skipped)", groups.Count.ToString());
            Check(groups[0].Exe == "CXGame.exe" && groups[0].FrametimesMs.Length == 2,
                "CSV largest group first");
        }
    }
}
