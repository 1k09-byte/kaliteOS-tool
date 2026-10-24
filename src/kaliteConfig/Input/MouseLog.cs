using System;
using System.Collections.ObjectModel;

namespace kaliteConfig.Input;

public sealed class MouseLogRow
{
    public MouseLogRow(string time, string device, string action, string result, string message)
    {
        Time = time;
        Device = device;
        Action = action;
        Result = result;
        Message = message;
    }

    public string Time { get; }
    public string Device { get; }
    public string Action { get; }
    public string Result { get; }
    public string Message { get; }
}

public interface IMouseLog
{
    ObservableCollection<MouseLogRow> Rows { get; }
    void Add(string device, string action, MouseResult result, string message);
}

public sealed class MouseLog : IMouseLog
{
    public ObservableCollection<MouseLogRow> Rows { get; } = new();

    public void Add(string device, string action, MouseResult result, string message)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        try
        {
            Rows.Add(new MouseLogRow(stamp, device ?? string.Empty, action ?? string.Empty, ResultText(result), message ?? string.Empty));
            while (Rows.Count > 500) Rows.RemoveAt(0);
        }
        catch { }
    }

    public static string ResultText(MouseResult result) => result switch
    {
        MouseResult.Verified => "Verified",
        MouseResult.AppliedNoVerify => "Applied (couldn't verify)",
        MouseResult.Refused => "Refused by device",
        MouseResult.Reverted => "Reverted (read-back mismatch)",
        MouseResult.Locked => "Locked (vendor software or firmware is in control)",
        MouseResult.Unsupported => "Unsupported by this mouse",
        MouseResult.NotImplemented => "Not implemented in this app",
        MouseResult.Blocked => "Blocked by guard",
        MouseResult.CantRead => "Can't read",
        MouseResult.Drifted => "Drifted (changed after we set it)",
        MouseResult.RestoreFailed => "Could not restore",
        _ => "Unknown",
    };
}
