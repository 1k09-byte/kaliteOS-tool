param([int]$Seconds = 30, [string]$Out = 'perf/idle-state.csv')
$p = Get-Process kaliteConfig -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { Write-Output 'PROCESS-NOT-FOUND'; exit 1 }
$rows = @()
$prev = $p.TotalProcessorTime.TotalMilliseconds
for ($i = 1; $i -le $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $p.Refresh()
    $now = $p.TotalProcessorTime.TotalMilliseconds
    $rows += [PSCustomObject]@{ t = $i; cpuPct = [math]::Round(($now - $prev) / 1000 / [Environment]::ProcessorCount * 100, 3); privateMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1); threads = $p.Threads.Count }
    $prev = $now
}
$rows | Export-Csv -NoTypeInformation -Path $Out
$avg = ($rows | Measure-Object cpuPct -Average).Average
Write-Output ("avgCpuPct={0:N3} threads={1} privateMB={2}" -f $avg, $rows[-1].threads, $rows[-1].privateMB)
