using System;
using kaliteConfig.Services;

// Display tab fallback probe. Prints what the page would bind to when the
// NVIDIA vendor API cannot enumerate any display.
var rows = NvidiaDisplayService.EnumerateFromOperatingSystem();

Console.WriteLine($"rows: {rows.Count}");
foreach (var r in rows)
{
    Console.WriteLine($"  {r.DisplayLabel}  |  {r.DeviceName}  |  " +
                      $"{r.PanelWidth}x{r.PanelHeight}  |  nvidia={r.IsNvidiaControlled}");
}

if (!string.IsNullOrWhiteSpace(NvidiaDisplayService.UnavailableReason))
    Console.WriteLine($"reason: {NvidiaDisplayService.UnavailableReason}");

// The page must never end up with zero rows on a machine that has a display.
if (rows.Count == 0) return 1;

// Happy path: the full NVAPI enumeration must still produce rows with vendor
// controls when the NVIDIA driver can see the displays (desktop cards).
var live = NvidiaDisplayService.Enumerate();
Console.WriteLine($"live: {live.Count}");
foreach (var r in live)
{
    Console.WriteLine($"  {r.DisplayLabel}  |  {r.DeviceName}  |  nvidia={r.IsNvidiaControlled}  |  " +
                      $"dv={r.SupportsDigitalVibrance} bright={r.SupportsBrightness} scale={r.SupportsScaling}");
}
return live.Count > 0 ? 0 : 1;
