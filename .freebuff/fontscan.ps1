Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WC {
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
$p = Start-Process -FilePath $args[0] -PassThru
Start-Sleep -Seconds 16
$hwnd = [IntPtr]::Zero
foreach ($h in [WC]::ForPid([uint32]$p.Id)) { if ([WC]::Cls($h) -eq 'WinUIDesktopWin32WindowClass') { $hwnd = $h; break } }
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "NO_WINDOW"; exit 2 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$texts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
Write-Host ("text elements: " + $texts.Count)
$seen = @{}
foreach ($el in $texts) {
  $name = $el.Current.Name
  if (-not $name) { continue }
  $font = "<no text pattern>"
  try {
    $tp = $el.GetCurrentPattern([System.Windows.Automation.Text.TextPattern]::Pattern)
    if ($tp) {
      $v = $tp.GetCurrentValue([System.Windows.Automation.Text.TextPattern]::FontNameAttribute)
      $font = if ($v -eq [System.Windows.Automation.Text.TextAttribute]::Identifiers.FontName) { "<Identifiers.FontName>" } else { [string]$v }
    }
  } catch { $font = "ERR: " + $_.Exception.Message.Substring(0,[Math]::Min(60,$_.Exception.Message.Length)) }
  $key = "$font|$name"
  if ($seen.ContainsKey($key)) { continue }
  $seen[$key] = 1
  $shown = if ($name.Length -gt 42) { $name.Substring(0,42) + "..." } else { $name }
  Write-Host ("  [{0}] {1}" -f $font, $shown)
}
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue