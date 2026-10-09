# uia_nav.ps1 <pid> <element name> - find window, invoke element by name
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$procId = [int]$args[0]; $targetName = $args[1]
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pc = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
if (-not $win) { Write-Host "NO_WIN"; exit 2 }
$nc = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $targetName)
$all = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $nc)
if ($all.Count -eq 0) { Write-Host "NO_EL"; exit 3 }
# Prefer an element that actually supports Invoke; otherwise walk ancestors
# up from the first match until one does (rail label Text -> nav item).
$el = $null
foreach ($c in $all) {
    $pats = $c.GetSupportedPatterns()
    foreach ($p in $pats) { if ($p.ProgrammaticName -like "InvokePattern*") { $el = $c; break } }
    if ($el) { break }
}
if (-not $el) {
    $walk = $all.Item(0)
    while ($walk -and -not $el) {
        foreach ($p in $walk.GetSupportedPatterns()) {
            if ($p.ProgrammaticName -like "InvokePattern*") { $el = $walk; break }
        }
        if (-not $el) { $walk = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($walk) }
    }
}
if (-not $el) { $el = $all.Item(0) }
Write-Host ("found: {0} name='{1}'" -f $el.Current.ControlType.ProgrammaticName, $el.Current.Name)
try {
    $ip = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $ip.Invoke()
    Write-Host "invoked"
} catch {
    Write-Host "invoke failed: $_"
    exit 4
}
