param([int]$ProcId, [string]$Pattern)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public class Inp{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,IntPtr e);}'
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ProcId).MainWindowHandle)
$c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
foreach ($el in $els) {
    if ($el.Current.Name -eq $Pattern) {
        $r = $el.Current.BoundingRectangle
        $x = [int]($r.X + 40); $y = [int]($r.Y + $r.Height / 2)
        if ($x -gt 300) {
            [Inp]::SetCursorPos($x, $y) | Out-Null
            Start-Sleep -Milliseconds 80
            [Inp]::mouse_event(0x0002,0,0,0,[IntPtr]::Zero); [Inp]::mouse_event(0x0004,0,0,0,[IntPtr]::Zero)
            Start-Sleep -Milliseconds 70
            [Inp]::mouse_event(0x0002,0,0,0,[IntPtr]::Zero); [Inp]::mouse_event(0x0004,0,0,0,[IntPtr]::Zero)
            Write-Host "double-clicked text '$($el.Current.Name)' at $x,$y"
            exit 0
        }
    }
}
Write-Host "no text matching '$Pattern'"
