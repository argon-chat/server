#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the Argon test suites and, optionally, produces a merged coverage report.

.DESCRIPTION
    One entry point for every way the suites get run: locally while iterating, in CI with a coverage
    gate, and against either database engine.

    The integration suite boots one PostgreSQL (or CockroachDB), one Redis and one NATS container for
    the whole assembly and runs its fixtures concurrently, so a full run costs roughly one
    infrastructure start-up rather than one per fixture.

.PARAMETER Database
    Which engine the integration suite runs against.
      postgres  (default) - fast; what you want while iterating.
      cockroach           - what production runs; exercises the multi-region / TTL DDL path.

.PARAMETER Coverage
    Collect coverage and emit a merged HTML + Cobertura report under artifacts/coverage.

.PARAMETER Threshold
    Minimum line coverage percentage. The script exits non-zero below it. Ignored without -Coverage.

.PARAMETER Filter
    Passes through to `dotnet test --filter`.

.PARAMETER UnitOnly
    Run only the container-free suite (ArgonSharedLogicTest). No Docker required.

.PARAMETER Reuse
    Keep the containers alive between runs. Needs `testcontainers.reuse.enable=true` in
    ~/.testcontainers.properties. Saves the start-up cost on every run after the first.

.EXAMPLE
    ./scipts/run-tests.ps1
.EXAMPLE
    ./scipts/run-tests.ps1 -Coverage -Threshold 50
.EXAMPLE
    ./scipts/run-tests.ps1 -Database cockroach -Filter 'FullyQualifiedName~SpaceTests'
#>
[CmdletBinding()]
param(
    [ValidateSet('postgres', 'cockroach')]
    [string] $Database = 'postgres',

    [switch] $Coverage,

    [int] $Threshold = 0,

    [string] $Filter,

    [switch] $UnitOnly,

    [switch] $Reuse,

    [ValidateSet('quiet', 'minimal', 'normal', 'detailed', 'diagnostic')]
    [string] $Verbosity = 'normal'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $env:ARGON_TEST_DB = $Database
    if ($Reuse) { $env:ARGON_TEST_REUSE_CONTAINERS = '1' }

    $projects = @('tests/ArgonSharedLogicTest/ArgonSharedLogicTest.csproj')
    if (-not $UnitOnly) { $projects += 'tests/ArgonComplexTest/ArgonComplexTest.csproj' }

    $resultsDir = Join-Path $repoRoot 'artifacts/test-results'
    $reportDir = Join-Path $repoRoot 'artifacts/coverage'

    if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

    Write-Host "==> Building" -ForegroundColor Cyan
    dotnet build $repoRoot/Argon.Server.slnx -c Debug --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    $failed = $false
    foreach ($project in $projects) {
        Write-Host "==> Testing $project (db=$Database)" -ForegroundColor Cyan

        $testArgs = @(
            'test', $project,
            '--no-build',
            '--nologo',
            '--verbosity', $Verbosity,
            '--results-directory', $resultsDir,
            '--settings', 'tests/coverlet.runsettings',
            '--logger', 'trx'
        )
        if ($Filter) { $testArgs += @('--filter', $Filter) }
        if ($Coverage) { $testArgs += @('--collect:XPlat Code Coverage') }

        dotnet @testArgs
        if ($LASTEXITCODE -ne 0) { $failed = $true }
    }

    if (-not $Coverage) {
        if ($failed) { throw "Tests failed" }
        Write-Host "==> All tests passed" -ForegroundColor Green
        exit 0
    }

    Write-Host "==> Building coverage report" -ForegroundColor Cyan

    $coverageFiles = Get-ChildItem -Path $resultsDir -Recurse -Filter 'coverage.cobertura.xml'
    if (-not $coverageFiles) {
        # Usually means the run never started (bad run settings, build mismatch) rather than that
        # coverage itself failed — say so, and surface the test failure if there was one.
        if ($failed) { throw "Tests failed before any coverage was collected - see the output above" }
        throw "No coverage files were produced under $resultsDir"
    }

    dotnet tool restore | Out-Null
    dotnet reportgenerator `
        "-reports:$resultsDir/**/coverage.cobertura.xml" `
        "-targetdir:$reportDir" `
        "-reporttypes:Html;Cobertura;TextSummary;MarkdownSummaryGithub" `
        "-title:Argon Server Coverage"
    if ($LASTEXITCODE -ne 0) { throw "reportgenerator failed" }

    $summaryPath = Join-Path $reportDir 'Summary.txt'
    Get-Content $summaryPath | Write-Host

    # ReportGenerator writes the aggregate as e.g. "Line coverage: 63.4%".
    $summary = Get-Content $summaryPath -Raw
    if ($summary -notmatch 'Line coverage:\s*([0-9]+(?:[.,][0-9]+)?)%') {
        throw "Could not read line coverage out of $summaryPath"
    }
    $lineCoverage = [double]($Matches[1] -replace ',', '.')

    Write-Host "==> Line coverage: $lineCoverage% (report: $reportDir/index.html)" -ForegroundColor Cyan

    if ($Threshold -gt 0 -and $lineCoverage -lt $Threshold) {
        Write-Host "==> FAIL: line coverage $lineCoverage% is below the required $Threshold%" -ForegroundColor Red
        exit 1
    }

    if ($failed) { throw "Tests failed" }
    Write-Host "==> All tests passed" -ForegroundColor Green
}
finally {
    Pop-Location
}
