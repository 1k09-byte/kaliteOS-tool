$self = $PID
$ok = 0; $skip = 0
$deny = @('csrss','wininit','services','lsass','winlogon','smss','system','idle','registry','dwm','audiodg','spoolsv','fontdrvhost','sihost','taskhostw','msmpeng','nissrv','smartscreen','sechealthui','memcompression')
Get-Process | ForEach-Object {
    try {
        if ($_.Id -le 4 -or $_.Id -eq $self) { $script:skip++; return }
        if ($deny -contains $_.ProcessName.ToLower()) { $script:skip++; return }
        if ($_.PriorityClass -ne 'Normal') { $_.PriorityClass = 'Normal'; $script:ok++ }
        else { $script:skip++ }
    } catch { $script:skip++ }
}
Write-Host "reset=$ok skipped=$skip"
