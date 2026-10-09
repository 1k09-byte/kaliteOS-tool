# Bring a pid's window to the foreground with AttachThreadInput fallback,
# then capture. Same output contract as cap2.ps1 (win_<tag>.png).
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class W3 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowThreadProcessId")] public static extern uint GetWindowThreadProcessId2(IntPtr h);
  [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public static List<IntPtr> ForPid(uint want) {
    var l = new List<IntPtr>();
    EnumWindows((h,lp) => { uint p; GetWindowThreadProcessId(h, out p); if (p == want) l.Add(h); return true; }, IntPtr.Zero);
    return l;
  }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassNameW(h, s, 256); return s.ToString(); }
}
"@
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Dpi2 { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[void][Dpi2]::SetProcessDPIAware()

$procId = [int]$args[0]; $tag = $args[1]
$main = [IntPtr]::Zero
foreach ($h in [W3]::ForPid([uint32]$procId)) {
  if ([W3]::Cls($h) -eq 'WinUIDesktopWin32WindowClass' -and [W3]::IsWindowVisible($h)) { $main = $h; break }
}
if ($main -eq [IntPtr]::Zero) { Write-Host "NO_WINDOW"; exit 2 }

[void][W3]::ShowWindow($main, 9)
[void][W3]::BringWindowToTop($main)
[void][W3]::SetForegroundWindow($main)

$ok = $false
for ($i = 0; $i -lt 30; $i++) {
  Start-Sleep -Milliseconds 300
  if ([W3]::GetForegroundWindow() -eq $main) { $ok = $true; break }
  # fallback: attach our input queue to the current foreground thread, which
  # legitimises SetForegroundWindow under the foreground-lock rules
  $fg = [W3]::GetForegroundWindow()
  if ($fg -ne [IntPtr]::Zero) {
    $fgT = [W3]::GetWindowThreadProcessId2($fg)
    $myT = [W3]::GetCurrentThreadId()
    if ($fgT -ne 0 -and $fgT -ne $myT) {
      [void][W3]::AttachThreadInput($myT, $fgT, $true)
      [void][W3]::BringWindowToTop($main)
      [void][W3]::SetForegroundWindow($main)
      [void][W3]::AttachThreadInput($myT, $fgT, $false)
    }
  }
}
Write-Host "foreground=$ok"
if (-not $ok) { exit 3 }

Start-Sleep -Seconds 2
$r = New-Object W3+RECT
[void][W3]::GetWindowRect($main, [ref]$r)
$w = $r.Right - $r.Left; $h2 = $r.Bottom - $r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h2)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save((Join-Path $PSScriptRoot ("win_" + $tag + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
$c = $bmp.GetPixel(20, 300)
Write-Host ("railPixel={0:X2}{1:X2}{2:X2} (expect 10150F)" -f $c.R, $c.G, $c.B)
Write-Host ("saved win_$tag.png ${w}x${h2}")
