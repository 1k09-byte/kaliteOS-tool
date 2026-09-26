// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Imports external captures into BenchmarkRuns. Pure parsing (unit-tested);
/// file picking lives in the ViewModel.
/// Sources (all verified, none guessed):
/// - PresentMon console CSV (default or --v2_metrics schema) via
///   <see cref="BenchmarkPresentMonCsv"/>, grouped by ProcessID.
/// - CapFrameX record JSON: {"Info": {GameName, ProcessName, CreationDate,
///   Comment}, "Runs": [{"CaptureData": {"TimeInSeconds": [...],
///   "MsBetweenPresents": [...]}}]} (verified against
///   CapFrameX.Data.Session/Classes Session.cs/SessionRun.cs/SessionCaptureData.cs).
/// </summary>
public static class BenchmarkImportService
{
    public sealed class ImportedRun
    {
        public string Game { get; set; } = string.Empty;
        public string Exe { get; set; } = string.Empty;
        public DateTime Date { get; set; } = DateTime.Now;
        public string Comment { get; set; } = string.Empty;
        public float[] FrametimesMs { get; set; } = Array.Empty<float>();
        public double[] TimestampsMs { get; set; } = Array.Empty<double>();
    }

    /// <summary>PresentMon CSV grouped by process (largest group first).</summary>
    public static List<ImportedRun> ImportPresentMonCsv(IEnumerable<string> lines, string fileName)
    {
        var list = lines.ToList();
        string? header = list.FirstOrDefault(l =>
            !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        if (header == null) throw new InvalidDataException("Empty CSV.");
        string[] cols = header.Split(',');
        int pidCol = Array.FindIndex(cols, c => c.Trim().Equals("ProcessID", StringComparison.OrdinalIgnoreCase));
        int appCol = Array.FindIndex(cols, c => c.Trim().Equals("Application", StringComparison.OrdinalIgnoreCase));

        var groups = new Dictionary<int, List<int>>();
        if (pidCol < 0)
        {
            groups[-1] = Enumerable.Range(1, list.Count - 1).ToList();
        }
        else
        {
            for (int i = 1; i < list.Count; i++)
            {
                string line = list[i];
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                string[] parts = line.Split(',');
                if (pidCol < parts.Length && int.TryParse(parts[pidCol].Trim(),
                        System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out int pid))
                {
                    if (!groups.TryGetValue(pid, out var g)) groups[pid] = g = new List<int>();
                    g.Add(i);
                }
            }
        }

        var runs = new List<ImportedRun>();
        foreach (var kv in groups.OrderByDescending(g => g.Value.Count))
        {
            var sub = new List<string> { header };
            foreach (int i in kv.Value) sub.Add(list[i]);
            string app = "unknown";
            if (appCol >= 0)
            {
                string[] first = list[kv.Value[0]].Split(',');
                if (appCol < first.Length && first[appCol].Trim().Length > 0)
                    app = first[appCol].Trim();
            }
            var parsed = BenchmarkPresentMonCsv.ParseLines(sub, kv.Key);
            if (parsed.RowsKept == 0) continue;
            runs.Add(new ImportedRun
            {
                Game = app.Replace(".exe", ""),
                Exe = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app : app + ".exe",
                Date = DateTime.Now,
                Comment = $"imported {fileName}" + (kv.Key >= 0 ? $" (PID {kv.Key})" : ""),
                FrametimesMs = parsed.FrametimesMs,
                TimestampsMs = parsed.TimestampsMs,
            });
        }
        if (runs.Count == 0)
            throw new InvalidDataException("No usable frames: wrong process, empty file, or unrecognized columns.");
        return runs;
    }

    /// <summary>CapFrameX record JSON: one ImportedRun per Runs[] entry.</summary>
    public static List<ImportedRun> ImportCapFrameXJson(string json, string fileName)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Runs", out var runsEl)
            || runsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Not a CapFrameX record: top-level \"Runs\" array missing.");
        }

        string game = fileName, exe = fileName, comment = $"imported {fileName}";
        DateTime date = DateTime.Now;
        if (root.TryGetProperty("Info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            game = Str(info, "GameName") ?? game;
            exe = Str(info, "ProcessName") ?? exe;
            string? c = Str(info, "Comment");
            if (!string.IsNullOrWhiteSpace(c)) comment = c;
            if (info.TryGetProperty("CreationDate", out var d))
            {
                if (d.ValueKind == JsonValueKind.String
                    && DateTime.TryParse(d.GetString(), out DateTime parsedDate))
                    date = parsedDate;
            }
        }
        game = game.Replace(".exe", "");
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";

        var runs = new List<ImportedRun>();
        int n = 0;
        foreach (var runEl in runsEl.EnumerateArray())
        {
            n++;
            if (!runEl.TryGetProperty("CaptureData", out var cap)
                || !cap.TryGetProperty("MsBetweenPresents", out var msEl)
                || msEl.ValueKind != JsonValueKind.Array)
            {
                continue; // sensor-only run: skip, keep others
            }
            var frames = new List<float>();
            foreach (var v in msEl.EnumerateArray())
            {
                double ms = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
                if (!double.IsNaN(ms) && ms > 0 && ms <= 1000) frames.Add((float)ms);
            }
            if (frames.Count == 0) continue;

            double[] stamps;
            if (cap.TryGetProperty("TimeInSeconds", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            {
                var ts = new List<double>();
                foreach (var v in tEl.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.Number) ts.Add(v.GetDouble() * 1000.0);
                if (ts.Count == frames.Count)
                {
                    double t0 = ts[0];
                    stamps = ts.Select(t => t - t0).ToArray();
                }
                else stamps = Cumulative(frames);
            }
            else stamps = Cumulative(frames);

            runs.Add(new ImportedRun
            {
                Game = n == 1 ? game : $"{game} #{n}",
                Exe = exe,
                Date = date,
                Comment = comment,
                FrametimesMs = frames.ToArray(),
                TimestampsMs = stamps,
            });
        }
        if (runs.Count == 0)
            throw new InvalidDataException(
                "No frame data: Runs[] entries lack \"CaptureData\".\"MsBetweenPresents\".");
        return runs;
    }

    private static double[] Cumulative(List<float> frames)
    {
        var stamps = new double[frames.Count];
        double t = 0;
        for (int i = 0; i < frames.Count; i++) { stamps[i] = t; t += frames[i]; }
        return stamps;
    }

    private static string? Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
