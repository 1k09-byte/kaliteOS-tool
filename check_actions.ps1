$run = Invoke-RestMethod -Uri "https://api.github.com/repos/1k09-byte/kaliteOS-tool/actions/runs?per_page=1"
$jobsUrl = $run.workflow_runs[0].jobs_url
$jobs = Invoke-RestMethod -Uri $jobsUrl
Write-Host "Jobs:"
foreach ($job in $jobs.jobs) {
    Write-Host "$($job.name) - $($job.conclusion)"
    Write-Host "Steps:"
    foreach ($step in $job.steps) {
        Write-Host "  $($step.name): $($step.conclusion)"
    }
}
