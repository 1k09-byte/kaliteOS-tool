param([int]$ProcId, [string]$NameFilter = "Button")
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [System.Windows.Automation.AutomationElement]::FromHandle((Get-Process -Id $ProcId).MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$btns = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
foreach ($b in $btns) {
    $r = $b.Current.BoundingRectangle
    if ($b.Current.Name -like "*$NameFilter*" -or $NameFilter -eq "*") {
        '{0} | x={1:N0} y={2:N0} w={3:N0} h={4:N0}' -f $b.Current.Name, $r.X, $r.Y, $r.Width, $r.Height
    }
}
