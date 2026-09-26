// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using kaliteConfig.Models;

namespace kaliteConfig.Services;

/// <summary>
/// Persists benchmark runs under %LocalAppData%/kaliteConfig/benchmarks/:
/// &lt;id&gt;.json (meta + system info + comment) + &lt;id&gt;.frames.gz
/// (float32 frametimes + float64 timestamps). All IO off the UI thread.
/// </summary>
public sealed class BenchmarkStoreService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string StoreDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "kaliteConfig", "benchmarks");

    private sealed class RunMeta
    {
        public Guid Id { get; set; }
        public string Game { get; set; } = string.Empty;
        public string Exe { get; set; } = string.Empty;
        public DateTime Date { get; set; }
        public double DurationSec { get; set; }
        public string Comment { get; set; } = string.Empty;
        public string ColorHex { get; set; } = "#4CC2FF";
        public BenchmarkSystemInfo SystemInfo { get; set; } = new();
        public int FrameCount { get; set; }
        public List<SensorSample> Sensors { get; set; } = new();
    }

    public async Task SaveAsync(BenchmarkRun run)
    {
        string dir = StoreDir;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(dir);
            var meta = new RunMeta
            {
                Id = run.Id,
                Game = run.Game,
                Exe = run.Exe,
                Date = run.Date,
                DurationSec = run.DurationSec,
                Comment = run.Comment,
                ColorHex = run.ColorHex,
                SystemInfo = run.SystemInfo,
                FrameCount = run.FrametimesMs.Length,
                Sensors = run.Sensors ?? new List<SensorSample>(),
            };
            File.WriteAllText(Path.Combine(dir, run.Id + ".json"),
                JsonSerializer.Serialize(meta, JsonOpts));
            using var fs = File.Create(Path.Combine(dir, run.Id + ".frames.gz"));
            using var gz = new GZipStream(fs, CompressionLevel.Optimal);
            using var bw = new BinaryWriter(gz);
            bw.Write(run.FrametimesMs.Length);
            foreach (float f in run.FrametimesMs) bw.Write(f);
            double[] stamps = run.TimestampsMs ?? Array.Empty<double>();
            bw.Write(stamps.Length);
            foreach (double t in stamps) bw.Write(t);
        }).ConfigureAwait(false);
    }

    public async Task<List<BenchmarkRun>> ListAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<BenchmarkRun>();
            string dir = StoreDir;
            if (!Directory.Exists(dir)) return list;
            foreach (string metaPath in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<RunMeta>(File.ReadAllText(metaPath));
                    if (meta == null) continue;
                    list.Add(new BenchmarkRun
                    {
                        Id = meta.Id,
                        Game = meta.Game,
                        Exe = meta.Exe,
                        Date = meta.Date,
                        DurationSec = meta.DurationSec,
                        Comment = meta.Comment,
                        ColorHex = meta.ColorHex,
                        SystemInfo = meta.SystemInfo ?? new BenchmarkSystemInfo(),
                    });
                }
                catch { }
            }
            return list.OrderByDescending(r => r.Date).ToList();
        }).ConfigureAwait(false);
    }

    public async Task<BenchmarkRun?> LoadAsync(Guid id)
    {
        return await Task.Run(() =>
        {
            string dir = StoreDir;
            string metaPath = Path.Combine(dir, id + ".json");
            string framesPath = Path.Combine(dir, id + ".frames.gz");
            if (!File.Exists(metaPath)) return null;
            var meta = JsonSerializer.Deserialize<RunMeta>(File.ReadAllText(metaPath));
            if (meta == null) return null;
            var run = new BenchmarkRun
            {
                Id = meta.Id,
                Game = meta.Game,
                Exe = meta.Exe,
                Date = meta.Date,
                DurationSec = meta.DurationSec,
                Comment = meta.Comment,
                ColorHex = meta.ColorHex,
                SystemInfo = meta.SystemInfo ?? new BenchmarkSystemInfo(),
                Sensors = meta.Sensors ?? new List<SensorSample>(),
            };
            if (File.Exists(framesPath))
            {
                using var fs = File.OpenRead(framesPath);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var br = new BinaryReader(gz);
                int n = br.ReadInt32();
                if (n < 0 || n > 10_000_000) throw new InvalidDataException("Frame count out of range.");
                var frames = new float[n];
                for (int i = 0; i < n; i++) frames[i] = br.ReadSingle();
                run.FrametimesMs = frames;
                try
                {
                    int m = br.ReadInt32();
                    if (m >= 0 && m <= 10_000_000)
                    {
                        var stamps = new double[m];
                        for (int i = 0; i < m; i++) stamps[i] = br.ReadDouble();
                        run.TimestampsMs = stamps;
                    }
                }
                catch (EndOfStreamException) { }
            }
            return run;
        }).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id)
    {
        await Task.Run(() =>
        {
            string dir = StoreDir;
            try { File.Delete(Path.Combine(dir, id + ".json")); } catch { }
            try { File.Delete(Path.Combine(dir, id + ".frames.gz")); } catch { }
        }).ConfigureAwait(false);
    }

    public async Task RenameAsync(Guid id, string comment)
    {
        await Task.Run(() =>
        {
            string metaPath = Path.Combine(StoreDir, id + ".json");
            if (!File.Exists(metaPath)) return;
            var meta = JsonSerializer.Deserialize<RunMeta>(File.ReadAllText(metaPath));
            if (meta == null) return;
            meta.Comment = comment ?? string.Empty;
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOpts));
        }).ConfigureAwait(false);
    }

    /// <summary>Updates only the meta JSON (never touches frames).</summary>
    public async Task UpdateColorAsync(Guid id, string colorHex)
    {
        await Task.Run(() =>
        {
            string metaPath = Path.Combine(StoreDir, id + ".json");
            if (!File.Exists(metaPath)) return;
            var meta = JsonSerializer.Deserialize<RunMeta>(File.ReadAllText(metaPath));
            if (meta == null) return;
            meta.ColorHex = colorHex;
            File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, JsonOpts));
        }).ConfigureAwait(false);
    }
}
