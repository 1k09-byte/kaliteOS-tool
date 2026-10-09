param([int]$ProcId, [double]$X = 345, [double]$Y = 371)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ProcId).MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
$best = $null; $bestD = 1e9
foreach ($b in $btns) {
    $r = $b.Current.BoundingRectangle
    if ($r.Width -le 0) { continue }
    $cx = $r.X + $r.Width / 2; $cy = $r.Y + $r.Height / 2
    $d = [Math]::Abs($cx - $X) + [Math]::Abs($cy - $Y)
    if ($d -lt $bestD) { $bestD = $d; $best = $b }
}
if ($best -and $bestD -lt 80) {
    $p = $best.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke()
    Write-Host "invoked button at $($best.Current.BoundingRectangle) (distance $([int]$bestD))"
} else {
    Write-Host "no button near ($X,$Y)"
}
