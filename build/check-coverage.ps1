# Fails (exit 1) unless line and branch coverage are both 100%.
# Input: ReportGenerator JsonSummary output. Used locally and by CI.
param(
    [string]$SummaryPath = "coverage-report/Summary.json"
)

$ErrorActionPreference = "Stop"

$summary = (Get-Content $SummaryPath -Raw | ConvertFrom-Json).summary
$line = [double]$summary.linecoverage
$branch = [double]$summary.branchcoverage

Write-Host "Line coverage:   $line%"
Write-Host "Branch coverage: $branch%"

if ($line -lt 100 -or $branch -lt 100) {
    Write-Error "Coverage gate failed: 100% line and branch coverage required."
    exit 1
}

Write-Host "Coverage gate passed."
