#!/usr/bin/env pwsh
<#
.SYNOPSIS
    The fixture-to-shard partition the integration suite is split along, plus the planner that
    regenerates it from a measured run.

.DESCRIPTION
    `run-tests.ps1 -Shards N` runs N `dotnet test` processes at once. Each process boots its own
    container stack and its own Argon host, so the partition has to be a partition: every fixture in
    exactly one shard, or the suite silently stops covering something.

    Three kinds of shard, and the kinds are not interchangeable:

    * `topology` — the eight `[NonParallelizable]` fixtures. Four of them stand up extra silos on
      HARD-CODED ports (RoleStartupTests walks upward from 21111 and again from 21511,
      GrainMigrationTests takes 22111/22131, ReminderRoutingTests 22311/22331,
      RegionRegistryClusterTests 23111/23131) and the other four issue schema changes against the
      shared database. Keeping all eight in one process is what makes the ports safe — only one
      process ever binds them — and it is also the shard that keeps the suite's own default silo
      port 11111/gateway 30000, for the same reason. NUnit runs the non-parallel shift strictly
      alone, with nothing else in flight, so in a single-process run these eight cost their full
      serial time on top of everything else; in a sharded run they cost nothing but their own shard.

    * `presence` — the eleven Presence fixtures. They are slow by nature (they wait on grain timers
      and TTLs, and no amount of test-side cleverness makes a 15 s tick arrive sooner), so they get
      a process to themselves rather than dragging one general shard past every other.

    * `general-*` — everything else, split by MEASURED serial seconds with longest-processing-time
      first, so the shards finish together instead of one shard holding the wall clock open.

.PARAMETER FromTrx
    Where to read measured fixture durations from: one or more .trx files, or directories that are
    searched recursively for them. Prints a freshly balanced partition, ready to be pasted over the
    `$generalShards` literal below. This is how you regenerate it after fixtures are added, removed
    or made materially slower.

    A sharded run is the cheap way to feed it, and the reason it takes a set rather than a path: the
    fixtures are spread over one .trx per shard, and a single-process run costs twice the wall clock
    for the same numbers.

        ./scipts/run-tests.ps1 -Shards 4                           # any run that covers every fixture
        ./scipts/test-shards.ps1 -FromTrx artifacts/test-results -Shards 4

    Durations are summed per fixture across everything given, so overlapping inputs would double-count
    — hand it one run's results, not an accumulated pile.

.PARAMETER Shards
    How many shards the plan has in total, `topology` and `presence` included. Minimum 3 — below
    that there is nothing left for the general fixtures.

.PARAMETER Verify
    Check the partition against the fixtures on disk and say so out loud. Non-zero exit if a fixture
    is unassigned, assigned twice, or named here but gone from the tree. `run-tests.ps1` does this
    on every sharded run; this switch is for doing it by hand.

.EXAMPLE
    ./scipts/test-shards.ps1                      # the plan, as objects
.EXAMPLE
    ./scipts/test-shards.ps1 -Verify
.EXAMPLE
    ./scipts/test-shards.ps1 -FromTrx artifacts/test-results -Shards 4
#>
[CmdletBinding()]
param(
    [string[]] $FromTrx,

    [ValidateRange(3, 16)]
    [int] $Shards = 4,

    [switch] $Verify
)

$ErrorActionPreference = 'Stop'

# ── the partition ────────────────────────────────────────────────────────────────────────────────

# Every [NonParallelizable] fixture, and only those. Adding a fixture to this list is how a new
# port-pinning or schema-touching fixture stays safe under sharding.
$topologyFixtures = @(
    'GrainMigrationTests'
    'MigrationLeaseTests'
    'RegionRegistryClusterTests'
    'ReminderRoutingTests'
    'RoleStartupTests'
    'SchemaDeclarationTests'
    'TablePlacementTests'
    'TtlSweepTests'
)

$presenceFixtures = @(
    'PresenceActivityTests'
    'PresenceAggregationTests'
    'PresenceBotTests'
    'PresenceFriendsTests'
    'PresenceHarnessSmokeTests'
    'PresenceLifecycleTests'
    'PresenceRaceTests'
    'PresenceRealtimeTests'
    'PresenceRevocationTests'
    'PresenceSessionGrainTests'
    'PresenceVoiceAndCountsTests'
)

# Balanced by measured serial seconds from a four-shard run on 2026-09-06 (see -FromTrx above for how
# to regenerate). The order inside a shard is meaningless — NUnit schedules fixtures itself — but the
# grouping is not: it is what makes the shards finish within a few seconds of each other.
#
# As measured, these two are 90 s of serial work each against the presence shard's 606 s and
# topology's 128 s of strictly serial work, so what balances them does not show in the wall clock
# today — 67 s and 51 s of wall against topology's 178 s. They are split anyway, and regenerated
# rather than hand-tuned, because the day the two slow shards stop dominating is the day this
# matters. The previous split had drifted to 108 s against 73 s, which is what regenerating is for.
$generalShards = @(
    @(   # 90 s serial
        'AccountLifecycleTests'
        'AdminConsoleTests'
        'AdminModerationWorkflowTests'
        'ArchetypeTests'
        'ChannelHighWaterMarkTests'
        'ChannelModerationTests'
        'DataExportTests'
        'FeatureFlagTests'
        'IdentityTests'
        'MediaUploadTests'
        'ModerationTests'
        'NotificationCounterTests'
        'ProfileCardTests'
        'ReadStateCacheTests'
        'RealtimeReplayBufferTests'
        'SecurityTests'
        'SocialGraphTests'
        'SpaceAndChannelTests'
        'SpaceSnapshotTests'
        'SpaceTests'
    ),
    @(   # 90 s serial
        'AegisOAuthTests'
        'AegisRoleTests'
        'BotApiTests'
        'ChannelBadgeTests'
        'ClusterTopologyTests'
        'MessageTests'
        'OtpTests'
        'ProfileLookupTests'
        'QrLoginTests'
        'RealtimeReplayFlappingTests'
        'RefreshRevocationTests'
        'ReportCaseTests'
        'SentryTunnelCorsTests'
        'SessionTests'
        'SpaceDeletionTests'
        'SpaceJoinNoticeTests'
        'SystemMessageTests'
        'UltimaTests'
        'UserStatsAndLevelTests'
        'UserTests'
        'WebSessionTests'
        'XsollaWebHookTests'
    )
)

# Fixtures that are deliberately in no shard, with the reason. A full run does not execute these
# either, so leaving them out changes nothing about what is covered — but they still have to be
# named, or the partition check below would call them unassigned on every run.
$excludedFixtures = [ordered]@{
    'PhoneChannelManualTests' = 'every test is [Explicit] (needs live Telegram/Prelude/Twilio credentials); a filter that named the fixture would select them and they would fail'
}

# ── fixtures on disk ─────────────────────────────────────────────────────────────────────────────

<#
.SYNOPSIS
    Every [TestFixture] class in the integration suite, read out of the source.
.DESCRIPTION
    Source rather than `dotnet test --list-tests`: discovery costs a build and a vstest start-up for
    an answer that is sitting in the files, and this runs before every sharded run.
#>
function Get-ArgonTestFixture {
    param([Parameter(Mandatory)] [string] $SuiteRoot)

    Get-ChildItem -Path $SuiteRoot -Recurse -Filter '*.cs' |
        ForEach-Object {
            $text = Get-Content -Raw -LiteralPath $_.FullName
            # `[TestFixture]` or `[TestFixture, NonParallelizable]`, then the class it decorates.
            [regex]::Matches($text, '\[TestFixture[^\]]*\]\s*(?:\[[^\]]*\]\s*)*(?:public\s+|internal\s+|sealed\s+|abstract\s+)*class\s+(\w+)') |
                ForEach-Object { $_.Groups[1].Value }
        } |
        Sort-Object -Unique
}

# ── the plan ─────────────────────────────────────────────────────────────────────────────────────

<#
.SYNOPSIS
    The partition as objects: name, fixtures, the `--filter` they become, and the silo ports the
    shard's Argon host must use.
.DESCRIPTION
    Filter clauses are anchored with dots — `FullyQualifiedName~.SessionTests.` — because a bare
    substring would put WebSessionTests in SessionTests' shard as well as its own, and
    SystemMessageTests in MessageTests'. The dots are the class-name boundaries in
    `Namespace.Class.Method`, so the anchored form matches one fixture and no other.

    Ports: the topology shard keeps the product defaults (11111/30000) because its fixtures pin
    ports of their own around them and were written against those defaults. Every other shard is
    moved to a private pair well clear of 21111-23131, which is what lets N hosts run at once —
    `Argon:Cluster:SiloPort` is plain configuration, so the environment variable reaches it without
    the suite knowing anything about sharding.
#>
function Get-ArgonTestShardPlan {
    param([Parameter(Mandatory)] [int] $Count)

    $generalCount = $Count - 2
    if ($generalCount -lt 1) {
        throw "-Shards $Count leaves no room for the general fixtures: topology and presence take one each."
    }
    if ($generalCount -ne $generalShards.Count) {
        throw ("-Shards $Count wants $generalCount general shard(s) and the partition in " +
               "scipts/test-shards.ps1 has $($generalShards.Count). Regenerate it: " +
               "./scipts/test-shards.ps1 -FromTrx <run>.trx -Shards $Count")
    }

    # `UnitSuite` rides on the topology shard: the container-free suite needs no stack of its own,
    # and topology is the shard with the fewest fixtures and no Presence-style waiting, so it is the
    # one with room for it.
    #
    # `Workers` is NUnit's NumberOfTestWorkers for that shard; 0 means "whatever AssemblyInfo.cs
    # says", which is 4. Only the presence shard asks for more, and for a reason particular to it:
    # its fixtures spend their time waiting on grain timers and Redis TTLs rather than on a CPU, so
    # eight of them overlap for free where eight CPU-bound fixtures would just queue.
    $plan = @(
        [pscustomobject]@{
            Name        = 'topology'
            Fixtures    = $topologyFixtures
            SiloPort    = 0        # 0 = leave the product defaults alone
            GatewayPort = 0
            UnitSuite   = $true
            Workers     = 0        # every fixture here is [NonParallelizable]; workers change nothing
        }
        [pscustomobject]@{
            Name        = 'presence'
            Fixtures    = $presenceFixtures
            SiloPort    = 24110
            GatewayPort = 34110
            UnitSuite   = $false
            Workers     = 8
        }
    )

    for ($i = 0; $i -lt $generalCount; $i++) {
        $plan += [pscustomobject]@{
            Name        = "general-$($i + 1)"
            Fixtures    = $generalShards[$i]
            SiloPort    = 24120 + $i * 10
            GatewayPort = 34120 + $i * 10
            UnitSuite   = $false
            Workers     = 0
        }
    }

    foreach ($shard in $plan) {
        $shard | Add-Member -NotePropertyName Filter -NotePropertyValue (
            ($shard.Fixtures | ForEach-Object { "FullyQualifiedName~.$_." }) -join '|')
    }

    $plan
}

<#
.SYNOPSIS
    Fails loudly unless the partition and the fixtures on disk are the same set.
.DESCRIPTION
    The failure this guards against is silent: a fixture added to the tree and to no shard simply
    stops running, and a green sharded suite says nothing about it. Cheap enough to run every time.
#>
function Assert-ArgonTestShardPlan {
    param(
        [Parameter(Mandatory)] [object[]] $Plan,
        [Parameter(Mandatory)] [string]   $SuiteRoot
    )

    $onDisk   = Get-ArgonTestFixture -SuiteRoot $SuiteRoot
    $assigned = $Plan | ForEach-Object { $_.Fixtures }
    $problems = @()

    $duplicates = $assigned | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name
    if ($duplicates) {
        $problems += "assigned to more than one shard: $($duplicates -join ', ')"
    }

    $unassigned = $onDisk | Where-Object { $_ -notin $assigned -and -not $excludedFixtures.Contains($_) }
    if ($unassigned) {
        $problems += "in the tree and in no shard: $($unassigned -join ', ')"
    }

    $ghosts = $assigned | Where-Object { $_ -notin $onDisk } | Sort-Object -Unique
    if ($ghosts) {
        $problems += "named in a shard and not in the tree: $($ghosts -join ', ')"
    }

    if ($problems) {
        throw ("The shard partition and tests/ArgonComplexTest disagree — " +
               ($problems -join '; ') + ". Fix scipts/test-shards.ps1 " +
               "(regenerate with -FromTrx after a run that covered every fixture).")
    }

    [pscustomobject]@{
        Fixtures = $onDisk.Count
        Assigned = @($assigned).Count
        Excluded = $excludedFixtures.Keys
    }
}

# ── planner ──────────────────────────────────────────────────────────────────────────────────────

<#
.SYNOPSIS
    Serial seconds per fixture, summed over every .trx given.
.DESCRIPTION
    A set rather than one file because a sharded run writes one .trx per shard, and that is the run
    people actually have: no fixture appears in two of them, so summing is the whole merge.
#>
function Get-FixtureDuration {
    param([Parameter(Mandatory)] [string[]] $TrxPath)

    $seconds = @{}

    foreach ($path in $TrxPath) {
        [xml] $trx = Get-Content -Raw -LiteralPath $path

        $classOf = @{}
        foreach ($definition in $trx.TestRun.TestDefinitions.UnitTest) {
            $classOf[$definition.id] = ($definition.TestMethod.className -split '\.')[-1]
        }

        foreach ($result in $trx.TestRun.Results.UnitTestResult) {
            $fixture = $classOf[$result.testId]
            if (-not $fixture) { continue }
            $duration = if ($result.duration) { [TimeSpan]::Parse($result.duration).TotalSeconds } else { 0 }
            $seconds[$fixture] = [double] $seconds[$fixture] + $duration
        }
    }

    $seconds
}

<#
.SYNOPSIS
    Expands whatever -FromTrx was given into a list of .trx files.
.DESCRIPTION
    Directories are searched recursively, so `artifacts/test-results` — the layout a sharded run
    leaves behind, one subdirectory per shard — is a valid argument and the one worth typing.
#>
function Resolve-TrxInput {
    param([Parameter(Mandatory)] [string[]] $Path)

    $files = foreach ($item in $Path) {
        if (-not (Test-Path -LiteralPath $item)) { throw "No such .trx or directory: $item" }
        if (Test-Path -LiteralPath $item -PathType Container) {
            Get-ChildItem -LiteralPath $item -Recurse -Filter '*.trx' | ForEach-Object { $_.FullName }
        }
        else {
            (Convert-Path -LiteralPath $item)
        }
    }

    $files = @($files | Sort-Object -Unique)
    if (-not $files) { throw "No .trx files under: $($Path -join ', ')" }
    $files
}

<#
.SYNOPSIS
    Longest-processing-time-first packing of the general fixtures into buckets.
.DESCRIPTION
    LPT rather than anything cleverer: the bound is within 4/3 of optimal, the input is 40-odd
    numbers, and a partition a human can read and edit is worth more here than the last few seconds.
#>
function New-GeneralShardPlan {
    param(
        [Parameter(Mandatory)] [string[]]  $Fixtures,
        [Parameter(Mandatory)] [hashtable] $Seconds,
        [Parameter(Mandatory)] [int]       $Buckets
    )

    # `$packed`, not `$buckets`: PowerShell variables are case-insensitive, so a local named
    # `$buckets` would be the `-Buckets` parameter and the count would be overwritten on line one.
    $packed = 1..$Buckets | ForEach-Object { [pscustomobject]@{ Total = 0.0; Fixtures = [System.Collections.Generic.List[string]]::new() } }

    $Fixtures |
        Sort-Object -Property @{ Expression = { [double] $Seconds[$_] } } -Descending |
        ForEach-Object {
            $lightest = $packed | Sort-Object Total | Select-Object -First 1
            $lightest.Fixtures.Add($_)
            $lightest.Total += [double] $Seconds[$_]
        }

    $packed
}

# ── entry points ─────────────────────────────────────────────────────────────────────────────────

$suiteRoot = Join-Path (Split-Path -Parent $PSScriptRoot) 'tests/ArgonComplexTest'

if ($FromTrx) {
    $trxFiles = Resolve-TrxInput -Path $FromTrx

    $seconds  = Get-FixtureDuration -TrxPath $trxFiles
    $onDisk   = Get-ArgonTestFixture -SuiteRoot $suiteRoot
    $general  = $onDisk | Where-Object {
        $_ -notin $topologyFixtures -and $_ -notin $presenceFixtures -and -not $excludedFixtures.Contains($_)
    }

    $missing = $general | Where-Object { -not $seconds.ContainsKey($_) }
    if ($missing) {
        Write-Warning ("No timing in the .trx for: $($missing -join ', ') — they are packed as 0 s. " +
                       'A run that did not cover every fixture cannot balance the partition.')
    }

    $buckets = New-GeneralShardPlan -Fixtures $general -Seconds $seconds -Buckets ($Shards - 2)

    Write-Host "# Regenerated from $($trxFiles.Count) .trx: $(($trxFiles | Split-Path -Leaf) -join ', ')" -ForegroundColor Cyan
    Write-Host ("# topology {0:n0}s, presence {1:n0}s" -f
        (($topologyFixtures | ForEach-Object { [double] $seconds[$_] } | Measure-Object -Sum).Sum),
        (($presenceFixtures | ForEach-Object { [double] $seconds[$_] } | Measure-Object -Sum).Sum)) -ForegroundColor Cyan
    Write-Host '$generalShards = @('
    foreach ($bucket in $buckets) {
        Write-Host ("    @(   # {0:n0}s serial" -f $bucket.Total)
        foreach ($fixture in ($bucket.Fixtures | Sort-Object)) {
            Write-Host "        '$fixture'"
        }
        # Commas between the inner arrays, and they are not optional: without them PowerShell
        # unrolls the nested literal into one flat list of fixture names.
        Write-Host $(if ($bucket -eq $buckets[-1]) { '    )' } else { '    ),' })
    }
    Write-Host ')'
    return
}

$plan = Get-ArgonTestShardPlan -Count $Shards

if ($Verify) {
    $summary = Assert-ArgonTestShardPlan -Plan $plan -SuiteRoot $suiteRoot
    Write-Host ("==> $($summary.Assigned) fixtures over $Shards shards, " +
                "$($summary.Fixtures) in the tree, excluded: $($summary.Excluded -join ', ')") -ForegroundColor Green
    foreach ($shard in $plan) {
        Write-Host ("    {0,-12} {1,2} fixtures  silo {2}" -f $shard.Name, $shard.Fixtures.Count,
            ($(if ($shard.SiloPort) { $shard.SiloPort } else { 'default (11111)' })))
    }
    return
}

$plan
