# Coverage gate: 100% line coverage, and 100% branch coverage except for branches
# explicitly baselined in coverage-baseline.json (compiler artifacts only, each with a
# reason, reviewed like code). See ADR 0003.
param(
    [string]$SummaryPath = "coverage-report/Summary.json",
    [string]$CoberturaPath = "TestResults/coverage.cobertura.xml",
    [string]$BaselinePath = "build/coverage-baseline.json"
)

$ErrorActionPreference = "Stop"

$summary = (Get-Content $SummaryPath -Raw | ConvertFrom-Json).summary
$line = [double]$summary.linecoverage
Write-Host "Line coverage:   $line%"
if ($line -lt 100) {
    Write-Error "Coverage gate failed: 100% line coverage required."
    exit 1
}

$baseline = (Get-Content $BaselinePath -Raw | ConvertFrom-Json).allowedPartialBranches
[xml]$cobertura = Get-Content $CoberturaPath

$violations = @()
$tolerated = 0
foreach ($package in $cobertura.coverage.packages.package | Where-Object { $_.name -like 'Emissary*' -and $_.name -notlike '*.Tests' }) {
    foreach ($class in $package.classes.class) {
        $partial = @($class.lines.line | Where-Object { $_.'condition-coverage' -and $_.'condition-coverage' -notlike '100%*' })
        foreach ($branch in $partial) {
            $sourceLine = (Get-Content $class.filename)[[int]$branch.number - 1].Trim()
            $allowed = $baseline | Where-Object {
                $class.filename.Replace('\', '/').EndsWith($_.file) -and $sourceLine.Contains($_.lineContains)
            }
            if ($allowed) {
                $tolerated++
                Write-Host "Tolerated (baselined): $($class.filename):$($branch.number) [$($branch.'condition-coverage')]"
            }
            else {
                $violations += "$($class.filename):$($branch.number) [$($branch.'condition-coverage')] $sourceLine"
            }
        }
    }
}

if ($violations.Count -gt 0) {
    $violations | ForEach-Object { Write-Host "MISSING BRANCH: $_" }
    Write-Error "Coverage gate failed: $($violations.Count) non-baselined partial branch(es)."
    exit 1
}

Write-Host "Branch coverage: 100% of non-baselined branches ($tolerated baselined artifact(s) tolerated)."
Write-Host "Coverage gate passed."
