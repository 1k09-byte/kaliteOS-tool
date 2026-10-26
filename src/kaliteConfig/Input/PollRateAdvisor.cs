using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using kaliteConfig.Native;
using kaliteConfig.Services;
using Microsoft.Win32;

namespace kaliteConfig.Input;

public sealed class DisplayRefreshInfo
{
    public string Name { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public int RefreshHz { get; init; }

    public string Text => (Name.Length > 0 ? Name + " " : string.Empty)
        + (Width > 0 && Height > 0 ? Width + "x" + Height + " " : string.Empty)
        + "@ " + RefreshHz + " Hz";
}

public sealed class SystemInputProfile
{
    public IReadOnlyList<DisplayRefreshInfo> Displays { get; init; } = Array.Empty<DisplayRefreshInfo>();
    public int RefreshHz { get; init; } = 60;
    public string CpuName { get; init; } = "Unknown CPU";
    public int PhysicalCores { get; init; } = 1;
    public int LogicalCores { get; init; } = 1;
}

public sealed class PollRateAdvice
{
    public int RecommendedHz { get; set; }
    public int MinimumHz { get; set; }
    public int CeilingHz { get; set; }
    public int TargetHz { get; set; }
    public int DisplayBudgetHz { get; set; }
    public string Explanation { get; set; } = string.Empty;
}

public interface IPollRateAdvisor
{
    SystemInputProfile Read();
    void Refresh();
    PollRateAdvice Advise(SystemInputProfile profile, IReadOnlyList<int> supportedRates, int measuredHz = 0);
}

public sealed class PollRateAdvisor : IPollRateAdvisor
{
    private readonly object _gate = new();
    private SystemInputProfile? _cached;

    public SystemInputProfile Read()
    {
        lock (_gate)
        {
            _cached ??= Detect();
            return _cached;
        }
    }

    public void Refresh()
    {
        lock (_gate) _cached = null;
    }

    private static SystemInputProfile Detect()
    {
        int logical = Math.Max(1, Environment.ProcessorCount);
        int physical = logical;
        try
        {
            var topology = TopologyService.Get();
            if (topology.PhysicalCores > 0) physical = topology.PhysicalCores;
            if (topology.TotalLogicalCores > 0) logical = topology.TotalLogicalCores;
        }
        catch { }
        int refresh = 0;
        var displays = ReadDisplays(ref refresh);
        return new SystemInputProfile
        {
            Displays = displays,
            RefreshHz = refresh > 0 ? refresh : 60,
            CpuName = ReadCpuName(),
            PhysicalCores = physical,
            LogicalCores = logical,
        };
    }

    public PollRateAdvice Advise(SystemInputProfile profile, IReadOnlyList<int> supportedRates, int measuredHz = 0)
    {
        var rates = new List<int>();
        foreach (int rate in supportedRates)
            if (rate > 0 && !rates.Contains(rate)) rates.Add(rate);
        rates.Sort();
        var advice = new PollRateAdvice();
        if (rates.Count == 0)
        {
            advice.Explanation = "This mouse does not report the rates it supports, so no rate can be recommended.";
            return advice;
        }

        int refresh = profile.RefreshHz > 0 ? profile.RefreshHz : 60;
        int cores = profile.PhysicalCores > 0 ? profile.PhysicalCores : 1;
        advice.MinimumHz = Math.Max(1000, refresh * 2);
        int ceiling = Math.Clamp(cores * 1500, 1000, 8000);
        int byDisplay = refresh * 8;
        int desired = Math.Clamp(Math.Max(byDisplay, advice.MinimumHz), advice.MinimumHz, ceiling);
        advice.CeilingHz = ceiling;
        advice.TargetHz = desired;
        advice.DisplayBudgetHz = byDisplay;

        int pick = 0;
        foreach (int rate in rates)
        {
            if (rate >= desired && rate <= ceiling)
            {
                pick = rate;
                break;
            }
        }
        if (pick == 0)
        {
            int best = int.MaxValue;
            foreach (int rate in rates)
            {
                int distance = Math.Abs(rate - desired) * (rate < advice.MinimumHz ? 4 : 1);
                if (distance < best)
                {
                    best = distance;
                    pick = rate;
                }
            }
        }
        advice.RecommendedHz = pick;

        string cpu = FriendlyCpu(profile.CpuName);
        string coresText = cores + " physical core" + (cores == 1 ? string.Empty : "s") + " and " + profile.LogicalCores + " thread" + (profile.LogicalCores == 1 ? string.Empty : "s");
        string frameText = profile.RefreshHz > 0
            ? "Your display refreshes " + refresh + " times a second, one frame every " + (1000.0 / refresh).ToString("0.00") + " ms."
            : "No display rate was found, so 60 Hz is assumed.";
        string needText = "That is a " + byDisplay + " Hz poll rate (8 reports per " + refresh + " Hz frame).";
        string supportText = pick >= desired
            ? pick + " Hz is the closest supported rate at or above that need."
            : "No supported rate reaches that need, so the nearest supported rate is " + pick + " Hz.";
        string rateText = string.Empty;
        if (pick > 0 && refresh > 0)
        {
            double waitMs = 500.0 / pick;
            double share = waitMs / (1000.0 / refresh) * 100.0;
            rateText = " At " + pick + " Hz a report waits " + waitMs.ToString("0.000") + " ms on average, " + share.ToString("0.0") + "% of a frame.";
        }
        string measured = measuredHz > 0 ? " The last test measured " + measuredHz + " Hz on this PC." : string.Empty;
        advice.Explanation = "Your " + cpu + " has " + coresText + ". " + frameText + " " + needText + " " + supportText + rateText + measured;
        return advice;
    }

    private static string FriendlyCpu(string name)
    {
        string text = (name ?? string.Empty).Replace("(R)", string.Empty).Replace("(TM)", string.Empty).Replace(" Processor", string.Empty).Replace(" CPU", string.Empty).Replace("-Core", " ");
        var words = new System.Collections.Generic.List<string>(text.Split(' ', System.StringSplitOptions.RemoveEmptyEntries));
        if (words.Count > 0 && (words[0].Equals("AMD", System.StringComparison.OrdinalIgnoreCase) || words[0].Equals("Intel", System.StringComparison.OrdinalIgnoreCase)))
            words.RemoveAt(0);
        if (words.Count > 0)
        {
            string last = words[words.Count - 1];
            bool digits = last.Length > 0 && last.Length <= 2;
            foreach (char c in last) if (!char.IsDigit(c)) digits = false;
            if (digits) words.RemoveAt(words.Count - 1);
        }
        string clean = string.Join(' ', words);
        return clean.Length > 0 ? clean : "PC";
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0", false);
            if (key?.GetValue("ProcessorNameString") is string name && name.Trim().Length > 0)
            {
                string clean = name.Trim()
                    .Replace(" Processor", string.Empty)
                    .Replace(" CPU", string.Empty)
                    .Replace(" (R)", string.Empty)
                    .Replace(" (TM)", string.Empty)
                    .Replace("  ", " ");
                return clean.Length > 0 ? clean : name.Trim();
            }
        }
        catch { }
        return "Unknown CPU";
    }

    private static IReadOnlyList<DisplayRefreshInfo> ReadDisplays(ref int maxHz)
    {
        var list = new List<DisplayRefreshInfo>();
        maxHz = 0;
        if (!TryQuery(NativeMethods.Display.QDC_DATABASE_CURRENT, out var paths, out var modes, out uint pathCount, out uint modeCount)
            && !TryQuery(NativeMethods.Display.QDC_ONLY_ACTIVE_PATHS, out paths, out modes, out pathCount, out modeCount))
            return list;

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            if (!path.TargetInfo.TargetAvailable) continue;
            if ((path.Flags & NativeMethods.Display.DISPLAYCONFIG_PATH_ACTIVE) == 0) continue;
            int hz = ReadRefresh(path, modes, modeCount);
            if (hz <= 0) continue;
            int width = 0;
            int height = 0;
            uint sourceIndex = path.SourceInfo.ModeInfoIdx;
            if (sourceIndex < modeCount
                && modes[sourceIndex].InfoType == NativeMethods.Display.DISPLAYCONFIG_MODE_INFO_TYPE.Source)
            {
                width = (int)modes[sourceIndex].Info.SourceMode.Width;
                height = (int)modes[sourceIndex].Info.SourceMode.Height;
            }
            list.Add(new DisplayRefreshInfo
            {
                Name = ReadMonitorName(path),
                Width = width,
                Height = height,
                RefreshHz = hz,
            });
            if (hz > maxHz) maxHz = hz;
        }
        return list;
    }

    private static bool TryQuery(
        uint flags,
        out NativeMethods.Display.DISPLAYCONFIG_PATH_INFO[] paths,
        out NativeMethods.Display.DISPLAYCONFIG_MODE_INFO[] modes,
        out uint pathCount,
        out uint modeCount)
    {
        paths = Array.Empty<NativeMethods.Display.DISPLAYCONFIG_PATH_INFO>();
        modes = Array.Empty<NativeMethods.Display.DISPLAYCONFIG_MODE_INFO>();
        pathCount = 0;
        modeCount = 0;
        try
        {
            if (NativeMethods.Display.GetDisplayConfigBufferSizes(flags, out pathCount, out modeCount) != 0) return false;
            if (pathCount == 0) return false;
            var foundPaths = new NativeMethods.Display.DISPLAYCONFIG_PATH_INFO[pathCount];
            var foundModes = new NativeMethods.Display.DISPLAYCONFIG_MODE_INFO[modeCount];
            if (NativeMethods.Display.QueryDisplayConfig(flags, ref pathCount, foundPaths, ref modeCount, foundModes, out _) != 0)
                return false;
            paths = foundPaths;
            modes = foundModes;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadRefresh(
        NativeMethods.Display.DISPLAYCONFIG_PATH_INFO path,
        NativeMethods.Display.DISPLAYCONFIG_MODE_INFO[] modes,
        uint modeCount)
    {
        uint targetIndex = path.TargetInfo.ModeInfoIdx;
        if (targetIndex < modeCount
            && modes[targetIndex].InfoType == NativeMethods.Display.DISPLAYCONFIG_MODE_INFO_TYPE.Target)
        {
            var signal = modes[targetIndex].Info.TargetMode.TargetVideoSignalInfo;
            if (signal.VSyncFreq.Denominator != 0)
            {
                double hz = signal.VSyncFreq.Numerator / (double)signal.VSyncFreq.Denominator;
                if (hz > 1) return (int)Math.Round(hz);
            }
        }
        if (path.TargetInfo.RefreshRate.Denominator != 0)
        {
            double hz = path.TargetInfo.RefreshRate.Numerator / (double)path.TargetInfo.RefreshRate.Denominator;
            if (hz > 1) return (int)Math.Round(hz);
        }
        return 0;
    }

    private static string ReadMonitorName(NativeMethods.Display.DISPLAYCONFIG_PATH_INFO path)
    {
        try
        {
            var name = new NativeMethods.Display.DISPLAYCONFIG_TARGET_DEVICE_NAME();
            name.Header.Type = NativeMethods.Display.DISPLAYCONFIG_DEVICE_INFO_TYPE.GetTargetName;
            name.Header.Size = (uint)Marshal.SizeOf<NativeMethods.Display.DISPLAYCONFIG_TARGET_DEVICE_NAME>();
            name.Header.AdapterId = path.TargetInfo.AdapterId;
            name.Header.Id = path.TargetInfo.Id;
            int result = NativeMethods.Display.DisplayConfigGetDeviceInfo(ref name);
            if (result == 0 && name.MonitorFriendlyDeviceName.Length > 0)
                return name.MonitorFriendlyDeviceName;
        }
        catch { }
        return string.Empty;
    }
}
