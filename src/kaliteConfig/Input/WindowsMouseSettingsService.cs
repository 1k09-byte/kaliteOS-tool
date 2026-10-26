using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using kaliteConfig.Input.Interop;
using kaliteConfig.Services.Personalize;

namespace kaliteConfig.Input;

public sealed class WindowsMouseState
{
    public int PointerSpeed = 10;
    public bool Precision;
    public int WheelLines = 3;
    public int WheelChars = 1;
    public bool Trails;
    public bool Swapped;
    public uint DoubleClickMs = 500;
    public bool SnapDefault;
    public bool ClickLock;
    public uint ClickLockMs = 1200;
}

public interface IWindowsMouseSettingsService
{
    WindowsMouseState Read();
    Task<MouseWriteOutcome> ApplySpeedAsync(string deviceId, string deviceName, int original, int requested);
    Task<MouseWriteOutcome> ApplyWheelLinesAsync(string deviceId, string deviceName, int original, int requested);
    Task<MouseWriteOutcome> ApplyWheelCharsAsync(string deviceId, string deviceName, int original, int requested);
    Task<MouseWriteOutcome> ApplySwapAsync(string deviceId, string deviceName, bool original, bool requested);
    Task<MouseWriteOutcome> ApplyDoubleClickAsync(string deviceId, string deviceName, uint original, uint requested);
    Task<MouseWriteOutcome> ApplySnapAsync(string deviceId, string deviceName, bool original, bool requested);
    Task<MouseWriteOutcome> ApplyClickLockAsync(string deviceId, string deviceName, bool original, bool requested);
    Task<MouseWriteOutcome> ApplyClickLockTimeAsync(string deviceId, string deviceName, uint original, uint requested);
    Task<MouseWriteOutcome> ApplyPrecisionAsync(string deviceId, string deviceName, bool original, bool requested);
    Task<MouseWriteOutcome> ApplyTrailsAsync(string deviceId, string deviceName, bool original, bool requested);
    Task<MouseWriteOutcome> RestoreAllAsync(string deviceId, string deviceName);
}

public sealed class WindowsMouseSettingsService : IWindowsMouseSettingsService
{
    private readonly ICursorService _cursors;
    private readonly IMouseWriteTransaction _tx;

    public WindowsMouseSettingsService(ICursorService cursors, IMouseWriteTransaction tx)
    {
        _cursors = cursors;
        _tx = tx;
    }

    public WindowsMouseState Read()
    {
        var state = new WindowsMouseState();
        try
        {
            var mouse = _cursors.CurrentMouse();
            state.PointerSpeed = ClampInt(mouse.PointerSpeed, 1, 20, 10);
            state.Precision = mouse.EnhancedPrecision;
            state.Trails = mouse.Trails > 0;
            state.WheelLines = ClampInt(mouse.WheelLines, -1, 100, 3);
        }
        catch { }
        try { state.WheelChars = ClampInt(GetDword(MouseSystemInterop.SpiGetWheelScrollChars), 1, 20, 1); }
        catch { }
        try { state.Swapped = IsSwapped(); }
        catch { }
        try { state.DoubleClickMs = ClampUint(unchecked((int)MouseSystemInterop.GetDoubleClickTime()), 100, 5000, 500); }
        catch { }
        try { state.SnapDefault = GetBool(MouseSystemInterop.SpiGetSnapToDefault); }
        catch { }
        try { state.ClickLock = GetBool(MouseSystemInterop.SpiGetClickLock); }
        catch { }
        try { state.ClickLockMs = ClampUint(GetDword(MouseSystemInterop.SpiGetClickLockTime), 200, 2200, 1200); }
        catch { }
        return state;
    }

    public Task<MouseWriteOutcome> ApplySpeedAsync(string deviceId, string deviceName, int original, int requested)
    {
        if (requested < 1 || requested > 20)
            return Task.FromResult(new MouseWriteOutcome(MouseResult.Blocked, "Blocked by guard: pointer speed stays between 1 and 20."));
        var current = Read();
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Pointer speed",
            Original = original.ToString(),
            Requested = requested.ToString(),
            Write = () => Task.FromResult(ApplyMouseOptions(requested, current.Precision, current.Trails ? 7 : 0, current.WheelLines)),
            ReadBack = () => Task.FromResult<string?>(Read().PointerSpeed.ToString()),
        });
    }

    public Task<MouseWriteOutcome> ApplyWheelLinesAsync(string deviceId, string deviceName, int original, int requested)
    {
        if (requested < -1 || requested > 100)
            return Task.FromResult(new MouseWriteOutcome(MouseResult.Blocked, "Blocked by guard: scroll lines stay between page-scroll and 100."));
        var current = Read();
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Scroll lines",
            Original = original.ToString(),
            Requested = requested.ToString(),
            Write = () => Task.FromResult(ApplyMouseOptions(current.PointerSpeed, current.Precision, current.Trails ? 7 : 0, requested)),
            ReadBack = () => Task.FromResult<string?>(Read().WheelLines.ToString()),
        });
    }

    public Task<MouseWriteOutcome> ApplyWheelCharsAsync(string deviceId, string deviceName, int original, int requested)
    {
        if (requested < 1 || requested > 20)
            return Task.FromResult(new MouseWriteOutcome(MouseResult.Blocked, "Blocked by guard: horizontal scroll characters stay between 1 and 20."));
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Horizontal scroll",
            Original = original.ToString(),
            Requested = requested.ToString(),
            Write = () => Task.FromResult(SetDword(MouseSystemInterop.SpiSetWheelScrollChars, (uint)requested)),
            ReadBack = () => Task.FromResult<string?>(Read().WheelChars.ToString()),
        });
    }

    public Task<MouseWriteOutcome> ApplySwapAsync(string deviceId, string deviceName, bool original, bool requested)
    {
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Primary button",
            Original = original ? "Left" : "Right",
            Requested = requested ? "Left" : "Right",
            Write = () => Task.FromResult(MouseSystemInterop.SwapMouseButton(!requested)),
            ReadBack = () => Task.FromResult<string?>(Read().Swapped ? "Left" : "Right"),
        });
    }

    public Task<MouseWriteOutcome> ApplyDoubleClickAsync(string deviceId, string deviceName, uint original, uint requested)
    {
        if (requested < 100 || requested > 2000)
            return Task.FromResult(new MouseWriteOutcome(MouseResult.Blocked, "Blocked by guard: double-click speed stays between 100 and 2000 ms."));
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Double-click speed",
            Original = original.ToString(),
            Requested = requested.ToString(),
            Write = () => Task.FromResult(MouseSystemInterop.SetDoubleClickTime(requested)),
            ReadBack = () => Task.FromResult<string?>(MouseSystemInterop.GetDoubleClickTime().ToString()),
        });
    }

    public Task<MouseWriteOutcome> ApplySnapAsync(string deviceId, string deviceName, bool original, bool requested)
    {
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Snap to default button",
            Original = OnOff(original),
            Requested = OnOff(requested),
            Write = () => Task.FromResult(SetBool(MouseSystemInterop.SpiSetSnapToDefault, requested)),
            ReadBack = () => Task.FromResult<string?>(OnOff(Read().SnapDefault)),
        });
    }

    public Task<MouseWriteOutcome> ApplyClickLockAsync(string deviceId, string deviceName, bool original, bool requested)
    {
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "ClickLock",
            Original = OnOff(original),
            Requested = OnOff(requested),
            Write = () => Task.FromResult(SetBool(MouseSystemInterop.SpiSetClickLock, requested)),
            ReadBack = () => Task.FromResult<string?>(OnOff(Read().ClickLock)),
        });
    }

    public Task<MouseWriteOutcome> ApplyClickLockTimeAsync(string deviceId, string deviceName, uint original, uint requested)
    {
        if (requested < 200 || requested > 2200)
            return Task.FromResult(new MouseWriteOutcome(MouseResult.Blocked, "Blocked by guard: ClickLock time stays between 200 and 2200 ms."));
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "ClickLock time",
            Original = original.ToString(),
            Requested = requested.ToString(),
            Write = () => Task.FromResult(SetDword(MouseSystemInterop.SpiSetClickLockTime, requested)),
            ReadBack = () => Task.FromResult<string?>(Read().ClickLockMs.ToString()),
        });
    }

    public Task<MouseWriteOutcome> ApplyPrecisionAsync(string deviceId, string deviceName, bool original, bool requested)
    {
        var current = Read();
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Pointer precision",
            Original = OnOff(original),
            Requested = OnOff(requested),
            Write = () => Task.FromResult(ApplyMouseOptions(current.PointerSpeed, requested, current.Trails ? 7 : 0, current.WheelLines)),
            ReadBack = () => Task.FromResult<string?>(OnOff(Read().Precision)),
        });
    }

    public Task<MouseWriteOutcome> ApplyTrailsAsync(string deviceId, string deviceName, bool original, bool requested)
    {
        var current = Read();
        return _tx.RunAsync(new MouseWritePlan
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Setting = "Pointer trails",
            Original = OnOff(original),
            Requested = OnOff(requested),
            Write = () => Task.FromResult(ApplyMouseOptions(current.PointerSpeed, current.Precision, requested ? 7 : 0, current.WheelLines)),
            ReadBack = () => Task.FromResult<string?>(OnOff(Read().Trails)),
        });
    }

    public Task<MouseWriteOutcome> RestoreAllAsync(string deviceId, string deviceName)
    {
        var current = Read();
        var saved = new System.Collections.Generic.List<MouseSnapshotValue>();
        try
        {
            var snapshots = new MouseSnapshotService();
            foreach (var entry in snapshots.ForDevice(deviceId)) saved.Add(entry);
        }
        catch { }
        int restored = 0;
        int failed = 0;
        foreach (var entry in saved)
        {
            bool ok = RestoreOne(entry.Setting, entry.Original, current);
            if (ok) ok = MatchesCurrent(entry.Setting, entry.Original);
            if (ok) restored++; else failed++;
        }
        string message = failed == 0
            ? "Restored and verified: " + restored + " setting(s) returned to how they were."
            : "Could not restore: " + failed + " setting(s) failed, " + restored + " restored.";
        return Task.FromResult(new MouseWriteOutcome(failed == 0 ? MouseResult.Verified : MouseResult.RestoreFailed, message));
    }

    private bool MatchesCurrent(string setting, string original)
    {
        try
        {
            var current = Read();
            string now = setting switch
            {
                "Pointer speed" => current.PointerSpeed.ToString(),
                "Scroll lines" => current.WheelLines.ToString(),
                "Horizontal scroll" => current.WheelChars.ToString(),
                "Primary button" => current.Swapped ? "Left" : "Right",
                "Double-click speed" => current.DoubleClickMs.ToString(),
                "Snap to default button" => OnOff(current.SnapDefault),
                "ClickLock" => OnOff(current.ClickLock),
                "ClickLock time" => current.ClickLockMs.ToString(),
                "Pointer precision" => OnOff(current.Precision),
                "Pointer trails" => OnOff(current.Trails),
                _ => string.Empty,
            };
            return now.Length > 0 && string.Equals(now.Trim(), original.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private bool RestoreOne(string setting, string original, WindowsMouseState current)
    {
        try
        {
            if (setting == "Pointer speed" && int.TryParse(original, out int speed))
                return ApplyMouseOptions(speed, current.Precision, current.Trails ? 7 : 0, current.WheelLines);
            if (setting == "Scroll lines" && int.TryParse(original, out int lines))
                return ApplyMouseOptions(current.PointerSpeed, current.Precision, current.Trails ? 7 : 0, lines);
            if (setting == "Horizontal scroll" && uint.TryParse(original, out uint chars))
                return SetDword(MouseSystemInterop.SpiSetWheelScrollChars, chars);
            if (setting == "Primary button")
                return MouseSystemInterop.SwapMouseButton(!string.Equals(original, "Left", StringComparison.OrdinalIgnoreCase));
            if (setting == "Double-click speed" && uint.TryParse(original, out uint ms))
                return MouseSystemInterop.SetDoubleClickTime(ms);
            if (setting == "Snap to default button")
                return SetBool(MouseSystemInterop.SpiSetSnapToDefault, string.Equals(original, "On", StringComparison.OrdinalIgnoreCase));
            if (setting == "ClickLock")
                return SetBool(MouseSystemInterop.SpiSetClickLock, string.Equals(original, "On", StringComparison.OrdinalIgnoreCase));
            if (setting == "ClickLock time" && uint.TryParse(original, out uint lockMs))
                return SetDword(MouseSystemInterop.SpiSetClickLockTime, lockMs);
            if (setting == "Pointer precision")
                return ApplyMouseOptions(current.PointerSpeed, string.Equals(original, "On", StringComparison.OrdinalIgnoreCase), current.Trails ? 7 : 0, current.WheelLines);
            if (setting == "Pointer trails")
                return ApplyMouseOptions(current.PointerSpeed, current.Precision, string.Equals(original, "On", StringComparison.OrdinalIgnoreCase) ? 7 : 0, current.WheelLines);
        }
        catch { }
        return false;
    }

    private bool ApplyMouseOptions(int speed, bool precision, int trails, int wheelLines)
    {
        try
        {
            var results = _cursors.ApplyMouse(new Services.Personalize.MouseOptions(speed, precision, trails, wheelLines));
            foreach (var row in results)
                if (!row.Ok) return false;
            return true;
        }
        catch { return false; }
    }

    private static bool IsSwapped()
    {
        try
        {
            return MouseSystemInterop.GetSystemMetrics(MouseSystemInterop.SmSwapButton) != 0;
        }
        catch { return false; }
    }

    private static int GetDword(uint action)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, 0);
            if (!MouseSystemInterop.SystemParametersInfoW(action, 0, buffer, 0)) return 0;
            return Marshal.ReadInt32(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool SetDword(uint action, uint value)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, unchecked((int)value));
            return MouseSystemInterop.SystemParametersInfoW(action, 0, buffer, MouseSystemInterop.SpiFlagsUpdateAndBroadcast);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool GetBool(uint action)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, 0);
            if (!MouseSystemInterop.SystemParametersInfoW(action, 0, buffer, 0)) return false;
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool SetBool(uint action, bool value)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buffer, value ? 1 : 0);
            return MouseSystemInterop.SystemParametersInfoW(action, 0, buffer, MouseSystemInterop.SpiFlagsUpdateAndBroadcast);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int ClampInt(int value, int minimum, int maximum, int fallback)
    {
        if (value < minimum || value > maximum) return fallback;
        return value;
    }

    private static uint ClampUint(int value, uint minimum, uint maximum, uint fallback)
    {
        if (value < 0 || (uint)value < minimum || (uint)value > maximum) return fallback;
        return (uint)value;
    }

    private static string OnOff(bool value) => value ? "On" : "Off";
}
