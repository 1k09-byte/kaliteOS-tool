Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$pid0 = [int]$args[0]
$proc = Get-Process -Id $pid0
$hwnd = $proc.MainWindowHandle
if ($hwnd -eq [IntPtr]::Zero) {
  Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WD {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public static List<IntPtr> ForPid(uint want) {
    var l = new List<IntPtr>();
    EnumWindows((h,lp) => { uint p; GetWindowThreadProcessId(h, out p); if (p == want) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassNameW(h, s, 256); return s.ToString(); }
}
"@
  foreach ($h in [WD]::ForPid([uint32]$pid0)) { if ([WD]::Cls($h) -eq 'WinUIDesktopWin32WindowClass') { $hwnd = $h; break } }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "NO_WINDOW"; exit 2 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Host ("text elements: " + $texts.Count)
$rows = @()
foreach ($el in $texts) {
  $name = $el.Current.Name
  if (-not $name) { continue }
  $font = "<no pattern>"
  try {
    $tp = $el.GetCurrentPattern([System.Windows.Automation.Text.TextPattern]::Pattern)
    if ($tp) { $font = [string]$tp.GetCurrentValue([System.Windows.Automation.Text.TextPattern]::FontNameAttribute) }
  } catch { $font = "ERR" }
  $shown = if ($name.Length -gt 46) { $name.Substring(0,46) + "..." } else { $name }
  $rows += ("  [{0}] {1}" -f $font, $shown)
}
$rows | Sort-Object -Unique | ForEach-Object { Write-Host $_ }