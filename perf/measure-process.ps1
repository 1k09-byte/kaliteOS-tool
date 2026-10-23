param([int]$Seconds = 30, [string]$Name = 'kaliteConfig', [string]$Out = 'perf/sample.csv')
$samples = @()
$cpuPrev = $null
for ($i = 0; $i -lt $Seconds; $i++) {
    $p = Get-Process -Name $Name -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { Write-Output 'PROCESS-NOT-FOUND'; exit 1 }
    $p.Refresh()
    $cpu = $p.TotalProcessorTime.TotalMilliseconds
    $cpuPct = if ($null -ne $cpuPrev) { ($cpu - $cpuPrev) / 1000 / [Environment]::ProcessorCount * 100 } else { 0 }
    $cpuPrev = $cpu
    $samples += [PSCustomObject]@{
        t = $i
        workingSetMB = [math]::Round($p.WorkingSet64 / 1MB, 1)
        privateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
        threads = $p.Threads.Count
        handles = $p.HandleCount
        cpuPct = [math]::Round($cpuPct, 2)
    }
    Start-Sleep -Seconds 1
}
$samples | Export-Csv -NoTypeInformation -Path $Out
$avgCpu = ($samples | Where-Object { $_.t -gt 0 } | Measure-Object cpuPct -Average).Average
$last = $samples[-1]
Write-Output ("avgCpuPct={0:N2} lastPrivateMB={1} lastWsMB={2} threads={3} handles={4}" -f $avgCpu, $last.privateMB, $last.workingSetMB, $last.threads, $last.handles)
