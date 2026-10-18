using System;
using kaliteConfig.Services;

var rows = NvidiaDisplayService.EnumerateFromOperatingSystem();

Console.WriteLine($"rows: {rows.Count}");
foreach (var r in rows)
{
    Console.WriteLine($"  {r.DisplayLabel}  |  {r.DeviceName}  |  " +
                      $"{r.PanelWidth}x{r.PanelHeight}  |  nvidia={r.IsNvidiaControlled}");
}

if (!string.IsNullOrWhiteSpace(NvidiaDisplayService.UnavailableReason))
    Console.WriteLine($"reason: {NvidiaDisplayService.UnavailableReason}");

if (rows.Count == 0) return 1;

var live = NvidiaDisplayService.Enumerate();
Console.WriteLine($"live: {live.Count}");
foreach (var r in live)
{
    Console.WriteLine($"  {r.DisplayLabel}  |  {r.DeviceName}  |  nvidia={r.IsNvidiaControlled}  |  " +
                      $"dv={r.SupportsDigitalVibrance} bright={r.SupportsBrightness} scale={r.SupportsScaling}");
}
return live.Count > 0 ? 0 : 1;
