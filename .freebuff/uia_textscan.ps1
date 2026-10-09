param([int]$ProcId, [string]$Match = "on CPU")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ProcId).MainWindowHandle)
$c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
$els = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
$n = 0
foreach ($el in $els) {
    $s = $el.Current.Name
    if ($s -like "*$Match*") { Write-Host "T: $s"; $n++; if ($n -ge 15) { break } }
}
Write-Host "---- matches: $n / $($els.Count) texts"
