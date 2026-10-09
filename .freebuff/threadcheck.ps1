param([int]$ProcId)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type 'using System;using System.Runtime.InteropServices;public class Inp{[DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,IntPtr e);[DllImport("user32.dll")]public static extern bool SetForegroundWindow(IntPtr h);}'
Add-Type -AssemblyName System.Windows.Forms
function Texts($root,$pat){ $c=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Text); $out=@(); foreach($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$c)){ if($e.Current.Name -like $pat){ $out+=$e } }; return ,$out }
function DblClick($x,$y){ [Inp]::SetCursorPos([int]$x,[int]$y)|Out-Null; Start-Sleep -Milliseconds 80; [Inp]::mouse_event(2,0,0,0,[IntPtr]::Zero);[Inp]::mouse_event(4,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 70; [Inp]::mouse_event(2,0,0,0,[IntPtr]::Zero);[Inp]::mouse_event(4,0,0,0,[IntPtr]::Zero) }
$p = Get-Process -Id $ProcId
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
# candidate rows: plain .exe labels with a valid on-screen rect
$cands = @()
foreach ($e in (Texts $root "*.exe")) {
    $r = $e.Current.BoundingRectangle
    if ($r.Width -gt 0 -and $r.X -gt 300 -and $r.Height -gt 10) { $cands += $e }
}
Write-Host "visible row candidates: $($cands.Count)"
foreach ($e in $cands) {
    $r = $e.Current.BoundingRectangle
    Write-Host "trying: $($e.Current.Name)"
    DblClick ($r.X + 40) ($r.Y + $r.Height/2)
    Start-Sleep -Seconds 3
    $hdrs = Texts $root "Threads - *"
    if ($hdrs.Count -gt 0) {
        $h = $hdrs[0].Current.Name
        Write-Host "header: $h"
        if ($h -match "(\d+) threads - select one" -and [int]$Matches[1] -gt 0) {
            Start-Sleep -Seconds 2
            $rows = Texts $root "on CPU*"
            Write-Host "---- on-CPU lines: $($rows.Count)"
            foreach ($r2 in $rows | Select-Object -First 8) { Write-Host "ROW: $($r2.Current.Name)" }
            exit 0
        }
    }
    [Inp]::SetForegroundWindow($p.MainWindowHandle)|Out-Null; Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait("{ESC}")
    Start-Sleep -Milliseconds 700
}
Write-Host "no process with readable threads found among visible rows"
