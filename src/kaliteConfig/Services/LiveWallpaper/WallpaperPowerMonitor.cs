using System;
using kaliteConfig.Services.Personalize.Native;
using Microsoft.Windows.System.Power;
using Windows.Graphics;

namespace kaliteConfig.Services.LiveWallpaper;

public interface IWallpaperPowerMonitor
{
    bool PauseOnFullscreen { get; set; }
    bool PauseOnBattery { get; set; }
    bool PauseOnEnergySaver { get; set; }
    bool CheckCovered { get; set; }
    LivePauseReasons Evaluate(RectInt32 coverArea);
}

public sealed class WallpaperPowerMonitor : IWallpaperPowerMonitor
{
    private const int QunsNotPresent = 1;
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullscreen = 3;
    private const int QunsPresentationMode = 4;

    public bool PauseOnFullscreen { get; set; } = true;
    public bool PauseOnBattery { get; set; } = true;
    public bool PauseOnEnergySaver { get; set; } = true;
    public bool CheckCovered { get; set; }

    public LivePauseReasons Evaluate(RectInt32 coverArea)
    {
        var reasons = LivePauseReasons.None;
        try
        {
            if (PauseOnBattery && PowerManager.BatteryStatus == BatteryStatus.Discharging)
                reasons |= LivePauseReasons.Battery;
        }
        catch { }
        try
        {
            if (PauseOnEnergySaver && PowerManager.EnergySaverStatus == EnergySaverStatus.On)
                reasons |= LivePauseReasons.EnergySaver;
        }
        catch { }
        try
        {
            if (PowerManager.DisplayStatus == DisplayStatus.Off)
                reasons |= LivePauseReasons.LockedOrDisplayOff;
        }
        catch { }
        try
        {
            int state = 0;
            if (DesktopHostNative.SHQueryUserNotificationState(out state) == 0)
            {
                if (state == QunsNotPresent)
                    reasons |= LivePauseReasons.LockedOrDisplayOff;
                else if (PauseOnFullscreen &&
                    (state == QunsRunningD3DFullscreen || state == QunsPresentationMode || state == QunsBusy))
                    reasons |= LivePauseReasons.Fullscreen;
            }
        }
        catch { }
        try
        {
            if (DesktopHostNative.GetSystemMetrics(DesktopHostNative.SM_REMOTESESSION) != 0)
                reasons |= LivePauseReasons.RemoteSession;
        }
        catch { }
        if (CheckCovered && IsCovered(coverArea))
            reasons |= LivePauseReasons.Covered;
        return reasons;
    }

    private static bool IsCovered(RectInt32 coverArea)
    {
        try
        {
            var fg = DesktopHostNative.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (!DesktopHostNative.GetWindowRect(fg, out var r)) return false;
            return r.Left <= coverArea.X && r.Top <= coverArea.Y &&
                r.Right >= coverArea.X + coverArea.Width && r.Bottom >= coverArea.Y + coverArea.Height;
        }
        catch { return false; }
    }
}
