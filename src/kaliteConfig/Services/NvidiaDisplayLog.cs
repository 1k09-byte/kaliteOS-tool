// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
// ==============================================================================
using System;
using System.Diagnostics;
using System.IO;

namespace kaliteConfig.Services;

/// <summary>
/// Diagnostic log for the NVIDIA display panel.
///
/// This panel calls straight into the display driver, and a fault inside a driver call
/// cannot be caught — it takes the process down with no managed exception and no entry in
/// the application log. So every step is written here before and after the call, and the
/// last line in the file is whatever was in flight when the process died.
/// </summary>
internal static class NvidiaDisplayLog
{
    private static readonly object Gate = new();

    private static string LogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kaliteConfig", "Nvidia", "nvidia-display.log");

    public static void Write(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line);
            }
            Debug.WriteLine("[Nvidia] " + message);
        }
        catch { /* logging must never be the thing that throws */ }
    }

    public static void Fault(string step, Exception ex)
        => Write($"!! {step} threw {ex.GetType().Name}: {ex.Message}");
}
