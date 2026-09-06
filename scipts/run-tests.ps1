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

    `-Shards N` takes that one step further and runs N such processes side by side over disjoint
    slices of the suite — see the note on sharding at the bottom of this comment.

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

.PARAMETER IncludeKnownBugs
    Also run the tests categorised `KnownPresenceBug` — the ones that are red on purpose because they
    pin a confirmed, still-open defect (see "Tests that pin an open bug" in tests/README.md). Without
    it every run here, sharded or not, appends `TestCategory!=KnownPresenceBug` to whatever filter it
    was going to use, so a healthy tree exits green. With it, nothing is excluded and the run is
    expected to be red: it is the way to see the current list of pinned defects, not a gating mode.

.PARAMETER Reuse
    Keep the containers alive between runs. Needs `testcontainers.reuse.enable=true` in
    ~/.testcontainers.properties. Saves the start-up cost on every run after the first.

.PARAMETER Shards
    Split the integration suite over N concurrent `dotnet test` processes. 1 (the default) is the
    single-process behaviour this script has always had, unchanged.

.PARAMETER Shard
    Run only shard i of N, in this process, and skip the coverage merge. This is what a CI matrix
    job calls; locally you want plain `-Shards N`, which spawns the shards for you.

.PARAMETER Workers
    NUnit's `NumberOfTestWorkers` — how many fixtures one process runs at once. Passed as a
    runsettings parameter, so `AssemblyInfo.cs` keeps a safe default for anyone running `dotnet test`
    by hand. Unset leaves the assembly's own LevelOfParallelism alone — except on the presence shard,
    which asks for one worker per core clamped to 2..8 (see scipts/test-shards.ps1). An explicit
    -Workers beats both, and every sharded run prints the number it settled on, per shard, because it
    is no longer the same on every machine and a shard that timed out is where you want to see it.

.PARAMETER NoBuild
    Skip the build. Set by the shard children, which the parent has already built for.

.PARAMETER MergeCoverageFrom
    Build the coverage report (and apply -Threshold) from a directory tree of already-collected
    `coverage.cobertura.xml` files instead of running any tests. CI's final job points this at the
    artifacts it downloaded from the shard jobs.

.EXAMPLE
    ./scipts/run-tests.ps1
.EXAMPLE
    ./scipts/run-tests.ps1 -Shards 4
.EXAMPLE
    ./scipts/run-tests.ps1 -Coverage -Threshold 50
.EXAMPLE
    ./scipts/run-tests.ps1 -IncludeKnownBugs        # the pinned-defect tests too; expected red
.EXAMPLE
    ./scipts/run-tests.ps1 -Database cockroach -Filter 'FullyQualifiedName~SpaceTests'
.EXAMPLE
    ./scipts/run-tests.ps1 -MergeCoverageFrom ./downloaded-artifacts -Coverage -Threshold 50

.NOTES
    Sharding, and why it is safe.

    Each `dotnet test` process starts a container stack of its own — its own PostgreSQL, its own
    Redis, its own NATS, its own MinIO, on Testcontainers' random host ports — so two shards share no
    state whatsoever. Orleans clustering in particular is isolated by construction: membership lives
    in that process's Redis, so two Argon hosts in two shards cannot see each other's silo table and
    cannot merge into one cluster, whatever cluster id they were given.

    What they would collide on is the silo's own listening socket: the suite's host takes the product
    defaults, 11111 and 30000, and only one process can bind those. So every shard but the topology
    one is moved to a private pair through `Argon:Cluster:SiloPort` / `:GatewayPort` in the
    environment — plain configuration the server already reads, no test-side knowledge of sharding.
    The topology shard keeps the defaults because it owns the fixtures that pin ports around them
    (RoleStartupTests from 21111, GrainMigrationTests 22111/22131, ReminderRoutingTests 22311/22331,
    RegionRegistryClusterTests 23111/23131); they are all in that one shard, so exactly one process
    binds each of those.

    The price of a shard is one stack start-up plus one ~100-migration bootstrap, about 30 s. The
    saving is the whole serial tail: NUnit runs `[NonParallelizable]` fixtures alone, with nothing
    else in flight, and there are eight of them.

    `scipts/test-shards.ps1` holds the partition and checks it against the fixtures on disk before
    every sharded run, so a new fixture in no shard fails the run instead of quietly not running.
#>
[CmdletBinding()]
param(
    [ValidateSet('postgres', 'cockroach')]
    [string] $Database = 'postgres',

    [switch] $Coverage,

    [int] $Threshold = 0,

    [string] $Filter,

    [switch] $UnitOnly,

    [switch] $IncludeKnownBugs,

    [switch] $Reuse,

    [ValidateSet('quiet', 'minimal', 'normal', 'detailed', 'diagnostic')]
    [string] $Verbosity = 'normal',

    [ValidateRange(1, 16)]
    [int] $Shards = 1,

    [ValidateRange(0, 16)]
    [int] $Shard = 0,

    [ValidateRange(0, 64)]
    [int] $Workers = 0,

    [switch] $NoBuild,

    [string] $MergeCoverageFrom
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$shardPlanScript = Join-Path $PSScriptRoot 'test-shards.ps1'

# The category a test carries when it is red on purpose — it pins a confirmed defect that is still
# open (tests/README.md, "Tests that pin an open bug"). Excluded from every run unless
# -IncludeKnownBugs, so a healthy tree exits green and the finding still lives in the tree, running
# and readable, rather than deleted or weakened until it passes.
$knownBugCategory = 'KnownPresenceBug'

<#
.SYNOPSIS
    The `--filter` a run actually uses: the caller's filter with the known-bug exclusion appended.
.DESCRIPTION
    One place, because there are four callers — the single-process run, its `-Filter`, the shard's
    fixture alternation, and the unit suite riding on the topology shard — and the day one of them
    forgets is the day a "green" gate is a coin toss.

    The parentheses are the point. `dotnet test --filter` binds `&` tighter than `|`, so
    `A|B&TestCategory!=X` excludes the category from B alone and runs every pinned test in A — which
    is exactly the shape a shard filter has. `(A|B)&(TestCategory!=X)` is the one that means what it
    reads as. An empty base filter needs no wrapping and gets none, so a plain run's filter stays
    short enough to read in the log.
#>
function Get-EffectiveFilter {
    param([string] $Base)

    if ($IncludeKnownBugs) { return $Base }

    $exclusion = "TestCategory!=$knownBugCategory"
    if ([string]::IsNullOrWhiteSpace($Base)) { return $exclusion }
    return "($Base)&($exclusion)"
}

<#
.SYNOPSIS
    How many fixtures a shard will run at once, phrased for the console.
.DESCRIPTION
    Said out loud on every sharded run because the number stopped being a constant: the presence
    shard asks for one worker per core clamped to 2..8, so the same command means eight fixtures at
    once on a 32-core dev box and two on a GitHub runner. When a shard fails on timeouts that is the
    first thing worth knowing, and it appears in no .trx and in no test output — only here.

    0 is not "no workers": it is "say nothing and let `[assembly: LevelOfParallelism]` decide", which
    is what every shard but presence does, so it is printed as what it means rather than as a zero.
#>
function Format-ShardWorkers {
    param([int] $Workers)

    if ($Workers -gt 0) { return "$Workers workers" }
    'workers: assembly default'
}

<#
.SYNOPSIS
    One `dotnet test` invocation, with the flags every caller here wants.
.DESCRIPTION
    Deliberately returns NOTHING and leaves the verdict in `$LASTEXITCODE`. A native command's
    console output IS this function's pipeline output, so a `return $LASTEXITCODE` hands the caller
    every line dotnet printed with the exit code tacked on the end — `(Invoke-TestProject ...) -ne 0`
    then compares an array of log lines against 0, is true whatever happened, and the run reports a
    failure for a shard where every test passed. The output belongs on the console, not in a return
    value.
#>
function Invoke-TestProject {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [string] $ResultsDirectory,
        [Parameter(Mandatory)] [string] $TrxName,
        [string] $TestFilter
    )

    $testArgs = @(
        'test', $Project,
        '--no-build',
        '--nologo',
        '--verbosity', $Verbosity,
        '--results-directory', $ResultsDirectory,
        '--logger', "trx;LogFileName=$TrxName"
    )

    # Composed here rather than at the call sites so no path can skip it: -Filter, a shard's fixture
    # alternation and the unfiltered unit suite all arrive through this one function.
    $effectiveFilter = Get-EffectiveFilter -Base $TestFilter
    if ($effectiveFilter) { $testArgs += @('--filter', $effectiveFilter) }

    # The run settings file goes in only when coverage was asked for, and that is not tidiness.
    # A <DataCollector> declared in a run settings file is ENABLED by that declaration — `--collect`
    # only adds to it — so passing the file on a plain run had coverlet instrument every Argon.Core
    # and Argon.Api module in the test output directory, write a Cobertura report nobody asked for,
    # and slow the run down for it. Worse under -Shards: coverlet instruments those modules IN PLACE
    # and restores them when the run ends, so two shards over one output directory corrupt each
    # other — the symptom is a shard discovering zero tests in an assembly full of them.
    if ($Coverage) { $testArgs += @('--settings', 'tests/coverlet.runsettings', '--collect:XPlat Code Coverage') }

    # Everything after a bare `--` is a runsettings override: `A.B=C` becomes <A><B>C</B></A>.
    # DefaultTimeout is the ceiling the run settings file would have given us and is worth having on
    # every run — a hung test that fails after ten minutes is a bug report, one that hangs the runner
    # is a mystery. NumberOfTestWorkers is the same knob `[assembly: LevelOfParallelism]` sets;
    # overriding it here rather than in the attribute keeps a conservative default for anyone running
    # `dotnet test` by hand.
    $runSettings = @('NUnit.DefaultTimeout=600000')
    if ($Workers -gt 0) { $runSettings += "NUnit.NumberOfTestWorkers=$Workers" }
    $testArgs += @('--') + $runSettings

    dotnet @testArgs
}

<#
.SYNOPSIS
    The counts a `dotnet test` log ends each project on, as one readable line per project.
.DESCRIPTION
    This exists because the obvious thing did not work. The sharded parent used to echo
    `Select-String -Pattern '^(Passed|Failed|Skipped)!'` over each shard's log, which is the summary
    VSTest prints in its one-line form — `Passed!  - Failed: 0, Passed: 42, ...`. The SDK in this
    repo prints the block form instead:

        Test Run Successful.
        Total tests: 929
             Passed: 929
         Total time: 2.7321 Seconds

    Nothing there starts with `Passed!`, so the loop matched nothing and every sharded run printed a
    header, a wall-clock line, and then silence where the per-shard counts should have been. Worse
    than useless: it looked like a shard had produced no output.

    Parsed rather than pattern-echoed because the block is four or five lines and a shard that ran
    two projects prints two of them; folding each into a line keeps a four-shard summary to six lines
    you can read at a glance. The absent keys default to zero — the SDK omits `Failed:` and
    `Skipped:` entirely when they are zero, so a clean run has a three-line block.

    The one-line form is read too, because which of the two you get is decided by `--verbosity`:
    `minimal` and below print `Passed!  - Failed: 0, Passed: 18, ...` on one line, `normal` and above
    print the block. This script defaults to `normal` and CI asks for it explicitly, so the block is
    the usual case — but `-Verbosity minimal` is a supported way to run it and must not silently lose
    the per-shard counts, which is the bug this function was written to fix.
#>
function Get-TestRunSummary {
    param([Parameter(Mandatory)] [string] $LogPath)

    if (-not (Test-Path $LogPath)) { return @() }

    $summaries = @()
    $current = $null

    foreach ($line in [IO.File]::ReadLines((Convert-Path -LiteralPath $LogPath))) {
        # `Passed!  - Failed: 0, Passed: 18, Skipped: 0, Total: 18, Duration: 22 ms - Some.dll (net10.0)`
        if ($line -match ('^(?:Passed|Failed|Skipped)!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+),' +
                          '\s*Skipped:\s*(\d+),\s*Total:\s*(\d+),\s*Duration:\s*(.+?)\s*$')) {
            $summaries += ('total {0}, passed {1}, failed {2}, skipped {3}, {4}' -f
                $Matches[4], $Matches[2], $Matches[1], $Matches[3], $Matches[5])
            continue
        }
        if ($line -match '^\s*Total tests:\s*(\d+)') {
            $current = [ordered]@{ Total = $Matches[1]; Passed = '0'; Failed = '0'; Skipped = '0' }
            continue
        }
        if ($null -eq $current) { continue }

        # The colon is what separates a summary line from the per-test lines above it, which read
        # `  Passed SomeTest [3 ms]` and would otherwise all match.
        if ($line -match '^\s*(Passed|Failed|Skipped):\s*(\d+)\s*$') {
            $current[$Matches[1]] = $Matches[2]
            continue
        }
        if ($line -match '^\s*Total time:\s*(.+?)\s*$') {
            $summaries += ('total {0}, passed {1}, failed {2}, skipped {3}, {4}' -f
                $current.Total, $current.Passed, $current.Failed, $current.Skipped, $Matches[1])
            $current = $null
        }
    }

    if ($current) {
        $summaries += ('total {0}, passed {1}, failed {2}, skipped {3}' -f
            $current.Total, $current.Passed, $current.Failed, $current.Skipped)
    }

    $summaries
}

<#
.SYNOPSIS
    Merges every coverage.cobertura.xml under a directory into one report and enforces -Threshold.
.DESCRIPTION
    The source is a directory rather than "the results directory" so CI's final job can point it at
    the artifacts it downloaded from the shard jobs; the glob is recursive either way. Returns the
    line coverage; throws when there is nothing to merge or the report cannot be read.
#>
function Invoke-CoverageReport {
    param(
        [Parameter(Mandatory)] [string] $Source,
        [Parameter(Mandatory)] [string] $ReportDir,
        [switch] $TestsFailed
    )

    Write-Host "==> Building coverage report" -ForegroundColor Cyan

    if (-not (Test-Path $Source)) { throw "No coverage source directory: $Source" }

    $coverageFiles = Get-ChildItem -Path $Source -Recurse -Filter 'coverage.cobertura.xml'
    if (-not $coverageFiles) {
        # Usually means the run never started (bad run settings, build mismatch) rather than that
        # coverage itself failed — say so, and surface the test failure if there was one.
        if ($TestsFailed) { throw "Tests failed before any coverage was collected - see the output above" }
        throw "No coverage files were produced under $Source"
    }

    dotnet tool restore | Out-Null

    # Captured and echoed rather than left to run into the pipeline: this function RETURNS the line
    # coverage, and a native command's console output is pipeline output too — uncaptured, the
    # caller gets reportgenerator's banner with the number tacked on the end, and `$coverage -lt
    # $Threshold` then compares an array of log lines against the gate.
    $report = dotnet reportgenerator `
        "-reports:$Source/**/coverage.cobertura.xml" `
        "-targetdir:$ReportDir" `
        "-reporttypes:Html;Cobertura;TextSummary;MarkdownSummaryGithub" `
        "-title:Argon Server Coverage" 2>&1
    $reportExit = $LASTEXITCODE
    $report | ForEach-Object { Write-Host $_ }
    if ($reportExit -ne 0) { throw "reportgenerator failed" }

    $summaryPath = Join-Path $ReportDir 'Summary.txt'
    Get-Content $summaryPath | Write-Host

    # ReportGenerator writes the aggregate as e.g. "Line coverage: 63.4%".
    $summary = Get-Content $summaryPath -Raw
    if ($summary -notmatch 'Line coverage:\s*([0-9]+(?:[.,][0-9]+)?)%') {
        throw "Could not read line coverage out of $summaryPath"
    }
    $lineCoverage = [double]($Matches[1] -replace ',', '.')

    Write-Host "==> Line coverage: $lineCoverage% (report: $ReportDir/index.html)" -ForegroundColor Cyan

    return $lineCoverage
}

Push-Location $repoRoot

try {
    $resultsDir = Join-Path $repoRoot 'artifacts/test-results'
    $reportDir = Join-Path $repoRoot 'artifacts/coverage'

    # ── merge-only: CI's final job, over the shard jobs' downloaded artifacts ─────────────────────
    if ($MergeCoverageFrom) {
        $lineCoverage = Invoke-CoverageReport -Source $MergeCoverageFrom -ReportDir $reportDir
        if ($Threshold -gt 0 -and $lineCoverage -lt $Threshold) {
            Write-Host "==> FAIL: line coverage $lineCoverage% is below the required $Threshold%" -ForegroundColor Red
            exit 1
        }
        exit 0
    }

    # `-Shard 1 -Shards 1` is a CI matrix of one, which is the unsharded suite: there is no partition
    # for a single shard and there does not need to be, so fall through to the single-process path.
    if ($Shards -eq 1) { $Shard = 0 }

    if ($Shards -gt 1 -or $Shard -gt 0) {
        if ($Filter) { throw "-Filter and sharding are mutually exclusive: the shards ARE the filter." }
        if ($UnitOnly) { throw "-UnitOnly has nothing to shard - the unit suite is seconds, not minutes." }
        if ($Reuse) { throw "-Reuse and sharding cannot be combined: reused containers mean two Argon hosts on one Redis, which is one Orleans cluster." }
        # Coverlet instruments the assemblies in the test output directory in place and restores them
        # when the run finishes. Shards on one machine share that directory, so they would corrupt
        # each other's copy of Argon.Core mid-run. In CI this never arises — every shard is a runner
        # of its own with a build of its own — so `-Shard i -Shards N -Coverage` is fine and only the
        # local fan-out is refused.
        if ($Coverage -and $Shard -eq 0) {
            throw ("-Coverage cannot be combined with a local -Shards fan-out: the shards would share " +
                   "one instrumented test output directory. Run the shards on separate machines " +
                   "(-Shard i -Shards N -Coverage, which is what CI does), or take coverage from a " +
                   "single-process run.")
        }
        if ($Shard -gt $Shards) { throw "-Shard $Shard is out of range for -Shards $Shards." }
    }

    $env:ARGON_TEST_DB = $Database
    if ($Reuse) { $env:ARGON_TEST_REUSE_CONTAINERS = '1' }

    # Said out loud on every run, because the alternative is someone comparing two test counts and
    # concluding tests went missing. Only the child shards stay quiet about it — the parent has
    # already said it once and four copies of one line is noise.
    if ($Shard -eq 0) {
        if ($IncludeKnownBugs) {
            Write-Host ("==> -IncludeKnownBugs: the $knownBugCategory tests run too. They pin confirmed, " +
                        "still-open defects, so expect this run to be red - see tests/README.md.") -ForegroundColor Yellow
        }
        else {
            Write-Host "==> Excluding TestCategory=$knownBugCategory (run with -IncludeKnownBugs to see them)" -ForegroundColor DarkGray
        }
    }

    $projects = @('tests/ArgonSharedLogicTest/ArgonSharedLogicTest.csproj')
    if (-not $UnitOnly) { $projects += 'tests/ArgonComplexTest/ArgonComplexTest.csproj' }

    if (-not $NoBuild) {
        Write-Host "==> Building" -ForegroundColor Cyan
        dotnet build $repoRoot/Argon.Server.slnx -c Debug --nologo -v minimal
        if ($LASTEXITCODE -ne 0) { throw "Build failed" }
    }

    $failed = $false

    # ── one shard, in this process (what a CI matrix job runs) ────────────────────────────────────
    if ($Shard -gt 0) {
        # The partition check runs here too, not only in the local parent: in CI every shard is a job
        # of its own and nothing else ever looks at the whole plan, so this is the only place a
        # fixture that landed in no shard would be noticed. It reads the source files and costs
        # milliseconds.
        & $shardPlanScript -Shards $Shards -Verify

        $plan = & $shardPlanScript -Shards $Shards
        $mine = $plan[$Shard - 1]

        # An explicit -Workers wins; otherwise the shard's own answer, which is 0 — the assembly
        # default — for every shard but presence, whose answer depends on the machine this is
        # running on.
        if ($Workers -eq 0) { $Workers = $mine.Workers }

        # Only the topology shard keeps 11111/30000; see the sharding note in the header.
        if ($mine.SiloPort -gt 0) {
            $env:Argon__Cluster__SiloPort = $mine.SiloPort
            $env:Argon__Cluster__GatewayPort = $mine.GatewayPort
        }

        $shardResults = Join-Path $resultsDir "shard-$($mine.Name)"
        if (Test-Path $shardResults) { Remove-Item $shardResults -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $shardResults | Out-Null

        Write-Host ("==> Shard $Shard/$Shards '$($mine.Name)': $($mine.Fixtures.Count) fixtures, " +
                    "$(Format-ShardWorkers -Workers $Workers) on $([Environment]::ProcessorCount) cores (db=$Database)") -ForegroundColor Cyan

        if ($mine.UnitSuite) {
            Write-Host "==> Testing tests/ArgonSharedLogicTest (rides along on this shard)" -ForegroundColor Cyan
            Invoke-TestProject `
                -Project 'tests/ArgonSharedLogicTest/ArgonSharedLogicTest.csproj' `
                -ResultsDirectory $shardResults `
                -TrxName 'unit.trx'
            if ($LASTEXITCODE -ne 0) { $failed = $true }
        }

        Invoke-TestProject `
            -Project 'tests/ArgonComplexTest/ArgonComplexTest.csproj' `
            -ResultsDirectory $shardResults `
            -TrxName "$($mine.Name).trx" `
            -TestFilter $mine.Filter
        if ($LASTEXITCODE -ne 0) { $failed = $true }

        if ($failed) { throw "Shard '$($mine.Name)' failed" }
        Write-Host "==> Shard '$($mine.Name)' passed" -ForegroundColor Green
        exit 0
    }

    # ── the parent of a sharded run: spawn the shards, wait, then merge ───────────────────────────
    if ($Shards -gt 1) {
        & $shardPlanScript -Shards $Shards -Verify

        if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

        $plan = & $shardPlanScript -Shards $Shards

        # This very host executable, not a bare "pwsh": the script is only ever run from a shell that
        # could already load it, and hard-coding a name would pick a different pwsh off PATH.
        $shell = (Get-Process -Id $PID).Path
        $started = [datetime]::UtcNow

        $running = @()
        for ($i = 1; $i -le $Shards; $i++) {
            $name = $plan[$i - 1].Name
            $log = Join-Path $resultsDir "shard-$name.log"

            $childArgs = @(
                '-NoProfile', '-File', $PSCommandPath,
                '-Shard', $i, '-Shards', $Shards,
                '-Database', $Database,
                '-Verbosity', $Verbosity,
                '-NoBuild'
            )
            # No -Coverage here on purpose: a local fan-out with coverage is refused above, so a
            # child never collects it. CI's shards do, and they call this script directly.
            if ($Workers -gt 0) { $childArgs += @('-Workers', $Workers) }
            # Switches do not cross a -File boundary by themselves: without this the parent would run
            # the pinned tests and every child would quietly skip them.
            if ($IncludeKnownBugs) { $childArgs += '-IncludeKnownBugs' }

            $running += [pscustomobject]@{
                Name    = $name
                Log     = $log
                Seconds = 0
                Process = Start-Process -FilePath $shell -ArgumentList $childArgs -NoNewWindow -PassThru `
                    -RedirectStandardOutput $log -RedirectStandardError "$log.err"
            }

            # The child says this too, but into its redirected log, which nobody opens on a run that
            # went well. The worker count is the one setting that differs between machines now, so
            # the parent's console — the thing a person is actually watching — carries it as well.
            $childWorkers = if ($Workers -gt 0) { $Workers } else { $plan[$i - 1].Workers }
            Write-Host "==> shard $i/$Shards '$name' started, $(Format-ShardWorkers -Workers $childWorkers) -> $log" -ForegroundColor Cyan
        }

        # `$job`, never `$shard`: PowerShell variables are case-insensitive, so a loop variable named
        # `$shard` IS the -Shard parameter, and assigning a shard object to an [int] parameter throws
        # in the middle of the run — with every child already started and nobody left to reap them.
        function Report-Shard {
            param($Job, [datetime] $RunStart)
            $Job.Seconds = [int]([datetime]::UtcNow - $RunStart).TotalSeconds
            $verdict = if ($Job.Process.ExitCode -eq 0) { 'passed' } else { "FAILED (exit $($Job.Process.ExitCode))" }
            $colour = if ($Job.Process.ExitCode -eq 0) { 'Green' } else { 'Red' }
            Write-Host "==> shard '$($Job.Name)' $verdict in $($Job.Seconds)s" -ForegroundColor $colour
        }

        while ($running | Where-Object { -not $_.Process.HasExited }) {
            Start-Sleep -Seconds 2
            foreach ($job in @($running | Where-Object { $_.Process.HasExited -and $_.Seconds -eq 0 })) {
                Report-Shard -Job $job -RunStart $started
            }
        }
        foreach ($job in @($running | Where-Object { $_.Seconds -eq 0 })) {
            Report-Shard -Job $job -RunStart $started
        }

        $wall = [int]([datetime]::UtcNow - $started).TotalSeconds
        Write-Host "==> $Shards shards in $wall s wall (slowest: $(($running | Sort-Object Seconds -Descending | Select-Object -First 1).Name))" -ForegroundColor Cyan

        foreach ($job in $running) {
            # The counts each `dotnet test` ends on, so a failure is readable without opening the
            # log. The whole file rather than its tail: a shard that runs two projects ends the first
            # one long before the end of its log, and that summary is the one worth seeing when the
            # unit suite is what broke.
            $summaries = @(Get-TestRunSummary -LogPath $job.Log)
            if ($summaries) {
                foreach ($summary in $summaries) { Write-Host "    [$($job.Name)] $summary" }
            }
            else {
                # No summary block at all means the run did not get as far as running tests — a
                # filter that matched nothing, a stack that never came up. Say which log to open
                # rather than printing nothing, which is what the old pattern did on every run.
                Write-Host "    [$($job.Name)] no test summary in the log - see $($job.Log)" -ForegroundColor Yellow
            }
        }

        $failed = [bool] ($running | Where-Object { $_.Process.ExitCode -ne 0 })
        if ($failed) {
            Write-Host "==> Logs of the failing shards are under $resultsDir" -ForegroundColor Red
        }
    }
    else {
        # ── the single-process path, unchanged ────────────────────────────────────────────────────
        if (Test-Path $resultsDir) { Remove-Item $resultsDir -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $resultsDir | Out-Null

        foreach ($project in $projects) {
            Write-Host "==> Testing $project (db=$Database)" -ForegroundColor Cyan
            $trx = [IO.Path]::GetFileNameWithoutExtension($project) + '.trx'
            Invoke-TestProject -Project $project -ResultsDirectory $resultsDir -TrxName $trx -TestFilter $Filter
            if ($LASTEXITCODE -ne 0) { $failed = $true }
        }
    }

    if (-not $Coverage) {
        if ($failed) { throw "Tests failed" }
        Write-Host "==> All tests passed" -ForegroundColor Green
        exit 0
    }

    $lineCoverage = Invoke-CoverageReport -Source $resultsDir -ReportDir $reportDir -TestsFailed:$failed

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
