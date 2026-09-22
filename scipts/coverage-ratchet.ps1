#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Records a run's line coverage and fails when it is lower than the default branch's.

.DESCRIPTION
    Coverage is read from the merged Cobertura report (lines-covered / lines-valid, so at full
    precision rather than the one decimal of Summary.txt).

    -SummaryOut writes it as a small JSON file. CI uploads that file from the default branch as the
    `coverage-baseline-<database>` artifact.

    -BaselineArtifact finds the newest such artifact from -Branch whose commit HEAD contains, i.e.
    the master this run was built on and not a newer one, downloads it and compares. -BaselineFile
    compares with a summary already on disk.

    Losing more than -Tolerance points fails. No baseline at all (first run, expired artifact) is a
    warning: the fixed threshold in run-tests.ps1 still applies.

.EXAMPLE
    ./scipts/coverage-ratchet.ps1 -SummaryOut artifacts/coverage/coverage-summary.json
.EXAMPLE
    ./scipts/coverage-ratchet.ps1 -BaselineArtifact coverage-baseline-postgres -Branch master -Tolerance 0.1
.EXAMPLE
    ./scipts/coverage-ratchet.ps1 -BaselineFile master-summary.json
#>
[CmdletBinding()]
param(
    [string] $Report = 'artifacts/coverage/Cobertura.xml',

    [string] $SummaryOut,

    [string] $BaselineFile,

    [string] $BaselineArtifact,

    [string] $Branch = 'master',

    [ValidateRange(0, 100)]
    [double] $Tolerance = 0.1,

    [string] $Repository = $env:GITHUB_REPOSITORY
)

$ErrorActionPreference = 'Stop'

function Format-Percent([double] $value) { $value.ToString('0.00', [cultureinfo]::InvariantCulture) }

function Read-Coverage([string] $path) {
    if (-not (Test-Path $path)) { throw "No coverage report at $path" }

    # Only the root element is needed; the DTD it names is never fetched.
    $reader = [System.Xml.XmlReader]::Create((Resolve-Path $path).Path, [System.Xml.XmlReaderSettings]@{ DtdProcessing = 'Ignore' })
    try {
        $null = $reader.MoveToContent()
        $covered = [long]$reader.GetAttribute('lines-covered')
        $valid   = [long]$reader.GetAttribute('lines-valid')
    }
    finally {
        $reader.Dispose()
    }

    if ($valid -le 0) { throw "$path has no coverable lines" }

    $sha = if ($env:GITHUB_SHA) { $env:GITHUB_SHA } else { git rev-parse HEAD }

    [pscustomobject]@{
        lineCoverage = [math]::Round(100.0 * $covered / $valid, 4)
        linesCovered = $covered
        linesValid   = $valid
        sha          = "$sha".Trim()
    }
}

function Read-Summary([string] $path) {
    $summary = Get-Content $path -Raw | ConvertFrom-Json
    if (-not $summary.linesValid) { throw "$path is not a coverage summary" }

    [pscustomobject]@{
        lineCoverage = 100.0 * $summary.linesCovered / $summary.linesValid
        sha          = "$($summary.sha)"
        runId        = $null
    }
}

function Get-BaselineFromArtifact {
    if (-not $Repository) { throw '-Repository (or GITHUB_REPOSITORY) is required with -BaselineArtifact' }

    $listing = gh api "repos/$Repository/actions/artifacts?name=$BaselineArtifact&per_page=100"
    if ($LASTEXITCODE -ne 0) { throw "Could not list the $BaselineArtifact artifacts (does the token have actions: read?)" }

    $candidates = @((($listing -join "`n") | ConvertFrom-Json).artifacts |
        Where-Object {
            -not $_.expired -and
            $_.workflow_run.head_branch -eq $Branch -and
            $_.workflow_run.head_repository_id -eq $_.workflow_run.repository_id
        } |
        Sort-Object created_at -Descending)

    foreach ($artifact in $candidates) {
        # A baseline from a commit this run does not contain would compare against tests it lacks.
        git merge-base --is-ancestor $artifact.workflow_run.head_sha HEAD 2>$null
        if ($LASTEXITCODE -ne 0) { continue }

        $dir = Join-Path ([IO.Path]::GetTempPath()) "coverage-baseline-$($artifact.id)"
        Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
        gh run download $artifact.workflow_run.id -R $Repository -n $BaselineArtifact -D $dir
        if ($LASTEXITCODE -ne 0) { throw "Could not download $BaselineArtifact from run $($artifact.workflow_run.id)" }

        $file = Get-ChildItem $dir -Recurse -Filter '*.json' | Select-Object -First 1
        if (-not $file) { throw "$BaselineArtifact from run $($artifact.workflow_run.id) holds no summary" }

        $baseline = Read-Summary $file.FullName
        $baseline.runId = $artifact.workflow_run.id
        return $baseline
    }

    Write-Host "Checked $($candidates.Count) $BaselineArtifact artifact(s) from $Branch; none is from a commit HEAD contains."
    return $null
}

$current = Read-Coverage $Report
Write-Host "==> Line coverage: $(Format-Percent $current.lineCoverage)% ($($current.linesCovered) of $($current.linesValid) lines)" -ForegroundColor Cyan

if ($SummaryOut) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent ([IO.Path]::GetFullPath($SummaryOut))) | Out-Null
    $current | ConvertTo-Json | Set-Content -Path $SummaryOut -Encoding utf8NoBOM
    Write-Host "==> Summary written to $SummaryOut"
}

if (-not $BaselineFile -and -not $BaselineArtifact) { exit 0 }
if ($BaselineFile -and $BaselineArtifact) { throw '-BaselineFile and -BaselineArtifact are mutually exclusive' }

$baseline = if ($BaselineFile) {
    if (Test-Path $BaselineFile) { Read-Summary $BaselineFile } else { Write-Host "No baseline file at $BaselineFile." ; $null }
} else {
    Get-BaselineFromArtifact
}

if (-not $baseline) {
    $message = "No coverage baseline from $Branch to compare with; only the fixed threshold applies to this run."
    Write-Host "::warning title=Coverage ratchet::$message"
    if ($env:GITHUB_STEP_SUMMARY) { "> [!WARNING]`n> $message`n" >> $env:GITHUB_STEP_SUMMARY }
    exit 0
}

$delta  = $current.lineCoverage - $baseline.lineCoverage
$source = "$Branch $($baseline.sha.Substring(0, [math]::Min(8, $baseline.sha.Length)))" + $(if ($baseline.runId) { " (run $($baseline.runId))" })
$line   = "Line coverage is $(Format-Percent $current.lineCoverage)%, $source has $(Format-Percent $baseline.lineCoverage)%: " +
          "$(if ($delta -ge 0) { '+' })$(Format-Percent $delta) points, tolerance $(Format-Percent $Tolerance)."

if ($env:GITHUB_STEP_SUMMARY) { "$line`n" >> $env:GITHUB_STEP_SUMMARY }

if ($delta -lt -$Tolerance) {
    Write-Host "::error title=Coverage ratchet::$line"
    exit 1
}

Write-Host "==> $line" -ForegroundColor Green
exit 0
