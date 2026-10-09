param([int]$ProcId, [string]$File)
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Win32R {
    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int L, T, R, B; }
}
'@
$proc = Get-Process -Id $ProcId
$rect = New-Object Win32R+RECT
[Win32R]::GetWindowRect($proc.MainWindowHandle, [ref]$rect) | Out-Null
"window origin: $($rect.L),$($rect.T)"
$bmp = [System.Drawing.Bitmap]::FromFile((Resolve-Path $File).Path)
$root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
foreach ($b in $btns) {
    $r = $b.Current.BoundingRectangle
    if ($r.Width -lt 60 -or $r.Height -lt 20) { continue }
    # sample a point 25% from the left edge, mid-height (avoids centred label text)
    $sx = [int]($r.X + $r.Width * 0.12) - $rect.L
    $sy = [int]($r.Y + $r.Height / 2) - $rect.T
    $edge = [int]($r.X - 3) - $rect.L
    $fill = '#??????'; $out = '#??????'
    if ($sx -ge 0 -and $sy -ge 0 -and $sx -lt $bmp.Width -and $sy -lt $bmp.Height) {
        $c = $bmp.GetPixel($sx, $sy); $fill = '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
    }
    if ($edge -ge 0 -and $edge -lt $bmp.Width -and $sy -ge 0 -and $sy -lt $bmp.Height) {
        $c = $bmp.GetPixel($edge, $sy); $out = '#{0:X2}{1:X2}{2:X2}' -f $c.R, $c.G, $c.B
    }
    '{0} | rect=({1:N0},{2:N0} {3:N0}x{4:N0}) fill={5} beside={6}' -f $b.Current.Name, $r.X, $r.Y, $r.Width, $r.Height, $fill, $out
}
$bmp.Dispose()
