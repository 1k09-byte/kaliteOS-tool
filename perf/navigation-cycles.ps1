param([int]$Cycles = 50, [string]$Out = 'perf/navigation-cycles.csv')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-Process kaliteConfig -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $proc) { Write-Output 'WINDOW-NOT-FOUND'; exit 1 }
$sig = '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'
Add-Type -MemberDefinition $sig -Name F -Namespace N -ErrorAction SilentlyContinue
[void][N.F]::SetForegroundWindow($proc.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)
function Get-Nav([string]$name) {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($el in $all) { if ($el.Current.Name -like "*$name*" -and $el.Current.ControlType.ProgrammaticName -eq 'ControlType.TreeItem') { return $el } }
    return $null
}
$pages = @('App Manager','Graphics Control','Process Affinity','Process Tuning','BIOS Control','Personalize Windows','Windows Tuning','Power')
$rows = @()
for ($c = 1; $c -le $Cycles; $c++) {
    foreach ($name in $pages) {
        $el = Get-Nav $name
        if ($el) { try { $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() } catch {} }
        Start-Sleep -Milliseconds 900
    }
    $p = Get-Process kaliteConfig -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { Write-Output "PROCESS-EXITED cycle=$c"; break }
    $p.Refresh()
    $rows += [PSCustomObject]@{ cycle = $c; privateMB = [math]::Round($p.PrivateMemorySize64/1MB,1); workingSetMB = [math]::Round($p.WorkingSet64/1MB,1); threads = $p.Threads.Count; handles = $p.HandleCount }
}
$rows | Export-Csv -NoTypeInformation -Path $Out
Write-Output ("cycles={0} firstPrivateMB={1} lastPrivateMB={2} firstHandles={3} lastHandles={4}" -f $rows.Count, $rows[0].privateMB, $rows[-1].privateMB, $rows[0].handles, $rows[-1].handles)
