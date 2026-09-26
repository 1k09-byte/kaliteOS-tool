$run = Invoke-RestMethod -Uri "https://api.github.com/repos/1k09-byte/kaliteOS-tool/actions/runs?per_page=1"
$jobsUrl = $run.workflow_runs[0].jobs_url
$jobs = Invoke-RestMethod -Uri $jobsUrl
foreach ($job in $jobs.jobs) {
    $failedStep = $job.steps | Where-Object { $_.conclusion -eq 'failure' }
    if ($failedStep) {
        Write-Host "Failed step in $($job.name): $($failedStep.name)"
        $logUrl = "https://api.github.com/repos/1k09-byte/kaliteOS-tool/actions/jobs/$($job.id)/logs"
        # Since logs require authentication, we can't fetch them if we don't have token with rights.
        # Let's at least see the exact step name
    }
}
