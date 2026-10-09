Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class WE {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
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
$procId = [int]$args[0]
$tag = $args[1]
$proc = Get-Process -Id $procId
$main = [IntPtr]::Zero
foreach ($h in [WE]::ForPid([uint32]$procId)) { if ([WE]::Cls($h) -eq 'WinUIDesktopWin32WindowClass') { $main = $h; break } }
if ($main -eq [IntPtr]::Zero) { Write-Host "NO_WINDOW"; exit 2 }
[void][WE]::ShowWindow($main, 9); [void][WE]::SetForegroundWindow($main); Start-Sleep -Seconds 2
$r = New-Object WE+RECT; [void][WE]::GetWindowRect($main, [ref]$r)
$w = $r.Right-$r.Left; $h2 = $r.Bottom-$r.Top
$bmp = New-Object System.Drawing.Bitmap($w, $h2)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$g.Dispose()
$out = Join-Path $PSScriptRoot ("win_" + $tag + ".png")
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host ("saved {0}  {1}x{2}" -f $out, $w, $h2)
# map the page header band: x 90..700, y 90..300
$bx=90; $by=90; $bw=610; $bh=210
$cols=150; $rows=34
Write-Host "--- header band (${bx},${by} ${bw}x${bh})"
for ($ry=0; $ry -lt $rows; $ry++) {
  $line=''
  for ($cx=0; $cx -lt $cols; $cx++) {
    $x=$bx+[int](($cx+0.5)*$bw/$cols); $y=$by+[int](($ry+0.5)*$bh/$rows)
    $c=$bmp.GetPixel($x,$y); $l=($c.R+$c.G+$c.B)/3
    if ($l -lt 30) { $ch='.' } elseif ($l -lt 75) { $ch='o' } elseif ($l -lt 140) { $ch='-' } else { $ch='O' }
    $line+=$ch
  }
  Write-Host ('{0,2} |{1}|' -f $ry,$line)
}