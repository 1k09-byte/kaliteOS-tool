Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WF {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
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
public class Dpi { [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[void][Dpi]::SetProcessDPIAware()
$procId = [int]$args[0]; $tag = $args[1]
$main = [IntPtr]::Zero
foreach ($h in [WF]::ForPid([uint32]$procId)) { if ([WF]::Cls($h) -eq 'WinUIDesktopWin32WindowClass') { $main = $h; break } }
if ($main -eq [IntPtr]::Zero) { Write-Host "NO_WINDOW"; exit 2 }
[void][WF]::ShowWindow($main, 9)
[void][WF]::BringWindowToTop($main)
[void][WF]::SetForegroundWindow($main)
$ok = $false
for ($i=0; $i -lt 20; $i++) {
  Start-Sleep -Milliseconds 400
  if ([WF]::GetForegroundWindow() -eq $main) { $ok = $true; break }
  [void][WF]::SetForegroundWindow($main)
}
Write-Host "foreground=$ok"
if (-not $ok) { exit 3 }
Start-Sleep -Seconds 2
$r = New-Object WF+RECT; [void][WF]::GetWindowRect($main, [ref]$r)
$w = $r.Right-$r.Left; $h2 = $r.Bottom-$r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h2)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()
$bmp.Save((Join-Path $PSScriptRoot ("win_" + $tag + ".png")), [System.Drawing.Imaging.ImageFormat]::Png)
# sanity: is our rail colour present (the app draws #101014)?
$c = $bmp.GetPixel(20, 300)
Write-Host ("railPixel={0:X2}{1:X2}{2:X2} (expect 101014)" -f $c.R, $c.G, $c.B)
Write-Host ("saved win_$tag.png ${w}x${h2}")