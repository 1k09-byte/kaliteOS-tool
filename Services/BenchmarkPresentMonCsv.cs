using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace kaliteConfig.Services;

/// <summary>
/// Parses PresentMon 2.5.1 console CSV output (default schema, see
/// Assets/PresentMon/VERSION.txt). Pure logic: header lookup, never index.
/// Primary frametime column MsBetweenPresents, fallback FrameTime
/// (--v2_metrics schema). No UI, no IO beyond the given lines — unit-tested.
/// </summary>
public static class BenchmarkPresentMonCsv
{
    public sealed class ParsedCapture
    {
        public float[] FrametimesMs { get; init; } = Array.Empty<float>();
        public double[] TimestampsMs { get; init; } = Array.Empty<double>();
        public int RowsTotal { get; init; }
        public int RowsKept { get; init; }
        public int RowsSkippedOtherPid { get; init; }
        public int RowsSkippedBadValue { get; init; }
        public bool HadProcessIdColumn { get; init; }
    }

    public static ParsedCapture ParseLines(IEnumerable<string> lines, int targetPid)
    {
        string[]? presetHeader = _lastLiveHeader;
        string[]? header = presetHeader;
        var frames = new List<float>();
        var stamps = new List<double>();
        int rows = 0, kept = 0, otherPid = 0, bad = 0;
        bool hadPidCol = presetHeader != null && MapHeader(presetHeader).ContainsKey("ProcessID");

        foreach (string raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            // CapFrameX record CSVs carry `//Key=Value` info headers (verified
            // in FileRecordInfo.cs): skip them, first real line is the header.
            if (raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            string[] cols = SplitCsv(raw);
            if (header == null)
            {
                header = cols;
                _lastLiveHeader = header; // cache for incremental tail parses
                var map = MapHeader(header);
                hadPidCol = map.ContainsKey("ProcessID");
                continue;
            }
            if (header == null) continue;
            rows++;

            var map2 = MapHeader(header);
            if (map2.TryGetValue("ProcessID", out int pidCol) && pidCol < cols.Length)
            {
                if (int.TryParse(cols[pidCol].Trim(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int rowPid))
                {
                    if (rowPid != targetPid) { otherPid++; continue; }
                }
            }

            string? ftRaw = GetCol(cols, map2, "MsBetweenPresents")
                ?? GetCol(cols, map2, "FrameTime");
            if (!TryMs(ftRaw, out double ft) || ft <= 0 || ft > 1000)
            {
                bad++;
                continue;
            }

            string? tsRaw = GetCol(cols, map2, "TimeInMs")
                ?? GetCol(cols, map2, "CPUStartTimeInMs")
                ?? GetCol(cols, map2, "CPUStartTime");
            double ts = stamps.Count == 0 ? 0 : stamps[^1] + ft;
            if (TryMs(tsRaw, out double parsed)) ts = parsed;
            else
            {
                // OCAT/CapFrameX variant: TimeInSeconds (verified key).
                string? secRaw = GetCol(cols, map2, "TimeInSeconds");
                if (TryMs(secRaw, out double sec)) ts = sec * 1000.0;
            }

            frames.Add((float)ft);
            stamps.Add(ts);
            kept++;
        }

        return new ParsedCapture
        {
            FrametimesMs = frames.ToArray(),
            TimestampsMs = stamps.ToArray(),
            RowsTotal = rows,
            RowsKept = kept,
            RowsSkippedOtherPid = otherPid,
            RowsSkippedBadValue = bad,
            HadProcessIdColumn = hadPidCol,
        };
    }

    private static string[]? _lastLiveHeader;

    public static ParsedCapture ParseFile(string csvPath, int targetPid)
    {
        _lastLiveHeader = null; // full parse: re-read the header from the file
        return ParseLines(File.ReadLines(csvPath), targetPid);
    }

    /// <summary>
    /// Incremental live-tail parse: reads only the bytes appended since the
    /// last call. The 1 Hz live graph used to re-parse the whole CSV every
    /// tick (O(n²) over a 60 s capture — ~50k rows re-read by the end),
    /// which itself perturbed the machine being measured.
    /// </summary>
    public static (ParsedCapture Capture, long LastOffset) ParseFileTail(
        string csvPath, int targetPid, long lastOffset)
    {
        try
        {
            using var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= lastOffset)
                return (new ParsedCapture(), lastOffset);
            fs.Seek(lastOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            var capture = ParseLines(ReadRemainingLines(reader), targetPid);
            long newOffset = fs.Position;
            return (capture, newOffset);
        }
        catch { return (new ParsedCapture(), lastOffset); }
    }

    private static IEnumerable<string> ReadRemainingLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }

    private static Dictionary<string, int> MapHeader(string[] header)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < header.Length; i++)
        {
            string name = header[i].Trim();
            if (name.Length > 0 && !map.ContainsKey(name)) map[name] = i;
        }
        return map;
    }

    private static string? GetCol(string[] cols, Dictionary<string, int> map, string name)
    {
        return map.TryGetValue(name, out int i) && i < cols.Length ? cols[i] : null;
    }

    private static bool TryMs(string? raw, out double ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        raw = raw.Trim();
        if (raw.Equals("NA", StringComparison.OrdinalIgnoreCase)) return false;
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out ms);
    }

    private static string[] SplitCsv(string line)
    {
        // PresentMon fields never contain quotes/commas inside values.
        return line.Split(',');
    }
}
