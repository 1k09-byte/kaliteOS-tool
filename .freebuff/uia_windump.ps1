Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$desk = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, "Live CPU - running devices")
$wins = $desk.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
if ($wins.Count -eq 0) { Write-Host "window not found"; exit 1 }
$win = $wins.Item(0)
Write-Host "window found: $($win.Current.Name)"
$textCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$els = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond)
$shown = 0
foreach ($el in $els) {
    $n = $el.Current.Name
    if ($n -match "." ) { Write-Host "T: $n"; $shown++ }
    if ($shown -ge 60) { break }
}
Write-Host "---- total texts: $($els.Count), cpu-bearing: $shown"
