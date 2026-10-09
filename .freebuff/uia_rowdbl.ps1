param([int]$ProcId, [switch]$CloseTool, [switch]$DblClickRow, [int]$Skip = 0)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public class Inp{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,IntPtr e);[DllImport("user32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr FindWindowW(string c,string n);[DllImport("user32.dll")]public static extern bool PostMessageW(IntPtr h,uint m,IntPtr w,IntPtr l);}'
if ($CloseTool) {
    $desk = [System.Windows.Automation.AutomationElement]::RootElement
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Live CPU - running devices")
    $w = $desk.FindFirst([System.Windows.Automation.TreeScope]::Children, $c)
    if ($w) { [Inp]::PostMessageW([IntPtr]$w.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero); Write-Host "tool window closed" }
    Start-Sleep -Milliseconds 600
}
if ($DblClickRow) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ProcId).MainWindowHandle)
    $target = $null
    foreach ($ct in @([System.Windows.Automation.ControlType]::Button, [System.Windows.Automation.ControlType]::ListItem, [System.Windows.Automation.ControlType]::Text)) {
        $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
        $els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
        foreach ($b in $els) {
            $r = $b.Current.BoundingRectangle
            if ($r.Width -gt 500 -and $r.Height -ge 30 -and $r.Height -le 90 -and $r.Y -gt 300) {
                if ($Skip -le 0) { $target = $r; break }
                $Skip--
            }
        }
        if ($target) { break }
    }
    if (-not $target) { Write-Host "no row button found"; exit 1 }
    $x = [int]($target.X + $target.Width / 2); $y = [int]($target.Y + $target.Height / 2)
    [Inp]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 80
    [Inp]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [Inp]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 70
    [Inp]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero); [Inp]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Write-Host "double-clicked row at $x,$y ($($target.Width)x$($target.Height))"
}
