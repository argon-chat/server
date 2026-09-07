# Argon test suites

Two projects, split by what they need to run:

| Project | Needs Docker | What it covers |
| --- | --- | --- |
| `ArgonSharedLogicTest` | no | Pure logic: permission evaluation, fractional indexing, bot SSE serialisation, contract shapes. Milliseconds per test. |
| `ArgonComplexTest` | yes | The real server, end to end: Ion RPC, the Bot HTTP API, Orleans grains, EF migrations, webhooks. |

## Running

```pwsh
./scipts/run-tests.ps1                          # everything, PostgreSQL, no coverage
./scipts/run-tests.ps1 -UnitOnly                # no Docker required
./scipts/run-tests.ps1 -Coverage -Threshold 50  # what CI runs
./scipts/run-tests.ps1 -Database cockroach      # against the production engine
./scipts/run-tests.ps1 -Filter 'FullyQualifiedName~SpaceTests'
./scipts/run-tests.ps1 -IncludeKnownBugs        # the pinned-defect tests too; red when any exist
./scipts/run-tests.ps1 -Reuse                   # keep containers between runs
```

Every one of those excludes `TestCategory=KnownPresenceBug` unless `-IncludeKnownBugs` says
otherwise, so a plain run on a healthy tree is green — see "Tests that pin an open bug" below for
what that category is and why the default is exclusion.

Plain `dotnet test` works too; the script only adds the run settings, the known-bug exclusion, the
coverage merge and the threshold check.

## Running fast

```pwsh
./scipts/run-tests.ps1 -Shards 4                # the same suite, four processes at once
./scipts/run-tests.ps1 -Shard 2 -Shards 4       # only shard 2 (what one CI matrix job runs)
./scipts/test-shards.ps1 -Verify                # is the partition still a partition?
```

`-Shards N` runs N `dotnet test` processes side by side over disjoint slices of the integration
suite. Each process starts a container stack of its own, so the shards share no state at all —
Orleans clustering included: membership lives in that process's Redis, so two Argon hosts in two
shards cannot see each other's silo table. What they would collide on is the silo's listening
socket, so every shard but `topology` is moved off 11111/30000 through `Argon:Cluster:SiloPort` in
the environment.

Each shard's output goes to `artifacts/test-results/shard-<name>.log`, and the parent prints the
wall clock of every shard as it finishes plus its counts per project — `total / passed / failed /
skipped` — so a red shard is readable without opening anything.

The partition lives in `scipts/test-shards.ps1`, and it is checked against the fixtures on disk
before every sharded run: a fixture in no shard fails the run rather than quietly not running. Three
kinds of shard:

| Shard | What is in it | Why |
| --- | --- | --- |
| `topology` | the eight `[NonParallelizable]` fixtures, plus the unit suite | Four of them pin silo ports (RoleStartupTests from 21111, GrainMigrationTests 22111/22131, ReminderRoutingTests 22311/22331, RegionRegistryClusterTests 23111/23131) and four issue schema changes against the shared database. One process owns all of them, so exactly one process binds those ports — and this is the shard that keeps the suite's default 11111/30000. NUnit runs the non-parallel shift alone with nothing else in flight, so in a single-process run these eight are pure serial tail. |
| `presence` | the eleven `Presence*Tests` | 635 s of the suite's 949 s of serial work, spent waiting on grain timers and Redis TTLs rather than on a CPU — so this shard runs one worker per core, clamped to 2–8, rather than the assembly's four, and that many waiting fixtures overlap for free. |
| `general-N` | everything else | Split by measured serial seconds, longest first, so the shards finish together. The five account fixtures added on 2026-09-06/07 are hand-placed on top of that split rather than regenerated into it — the comment above `$generalShards` says why. |

Measured on this tree, PostgreSQL, no coverage, 32 cores: **250 s over four shards** (per shard:
general-2 250 s, topology 180 s, presence 160 s, general-1 130 s) for 703 integration tests, of which
701 pass, one stands down because it is CockroachDB-only and this is PostgreSQL, and one is red only
when all four shards run at once — see "A test that is red only under four shards" below. The
947-test unit suite rides along on `topology` inside that number, five seconds of it.

The floor moved with the account campaign. It used to be `topology` — a container stack plus
`RoleStartupTests` (68 s) and `GrainMigrationTests` (41 s), two fixtures that stand up silos of their
own and therefore run alone — and it is now `general-2`, because `DataExportArchiveTests` alone is
182 s of serial work there, nearly all of it waiting out export ticks and a 40 s archive TTL. Shrink
that fixture, or split it across the two general shards, and re-plan.

Even with that fixture in it the suite is far faster than it was before the presence clocks were
compressed (707 s single-process, 368-382 s sharded, with presence alone at 1 614 s of serial work),
and almost all of that difference is one change: the
presence fixtures no longer wait out shipped clocks. `PresenceTimingOptions` makes the session TTL,
the refresh tick, the grace and the rest configuration, and the integration host sets them a factor
of ten down (`TestPresenceTimings`). It is ordinary configuration bound by the ordinary options
pipeline — nothing is stubbed, no branch is skipped, and the fixtures express every wait as a ratio
of what the host actually bound (`PresenceWaits`), so the same assertions would still hold, slowly,
at production values.

The presence shard is also why `-Workers` exists: its fixtures spend their time waiting on clocks
rather than on a CPU, so it asks for more than the assembly's four and packs 635 s of serial work
into 156 s of wall. What it asks for is `clamp(ProcessorCount, 2, 8)` — eight on any box with eight
cores or more, and however many a CI runner turns out to have.

The clamp is there because "waiting overlaps for free" is a claim about the machine, not about the
tests. Each waiting fixture still has a small non-waiting part — its Ion calls, its SignalR frames,
the silo's own scheduler — and eight of them on a two-core runner that is already hosting a
container stack and an Argon host do not wait in parallel, they queue. A fixture that gave an event
ten seconds to arrive then fails with `RealtimeWaitTimeoutException` and reads exactly like a
product bug, which is what a hard-coded eight cost CI. So: one per core, never below two (one worker
serialises the whole 635 s this shard exists to overlap) and never above eight (measured; past that
the waiting is not what the shard is short of).

What the clamp costs where cores are not scarce is nothing, because it resolves to the same eight.
What it costs where they are is wall clock: on this 32-core box the shard is 156 s at eight workers
and 347 s at two, all 114 tests green both times. A shard that takes twice as long and answers the
question beats one that is fast and reports a timeout as a defect.

`-Workers N` overrides it for any shard, and every sharded run prints what it settled on — per
shard, next to the fixture count, with the core count it was derived from — because the number is no
longer the same on two machines and a shard that failed on timeouts is exactly when you want to read
it back. `./scipts/test-shards.ps1 -Verify` prints the same column. If you change any of it, measure
both settings again — the four-worker figure this paragraph used to quote was taken before the
presence clocks were compressed and no longer means anything.

`-Coverage` and a local `-Shards` fan-out are refused together, and the script says why: coverlet
instruments the assemblies in the test output directory in place and restores them at the end of the
run, so shards sharing one output directory corrupt each other — visibly, as a shard that discovers
zero tests in an assembly full of them. In CI it never arises, because every shard is a runner with a
build of its own.

Re-plan after fixtures are added, removed, or made materially slower — the partition is generated,
not hand-kept:

```pwsh
./scipts/run-tests.ps1 -Shards 4                                # any run that covers every fixture
./scipts/test-shards.ps1 -FromTrx artifacts/test-results -Shards 4
```

`-FromTrx` takes a set: one or more `.trx` files, or directories searched recursively for them. A
sharded run leaves one `.trx` per shard under `artifacts/test-results`, no fixture appears in two of
them, and summing them is the whole merge — so the ordinary fast run is enough to re-plan from and a
single-process run costs twice the wall clock for the same numbers. It prints a balanced
`$generalShards` block to paste over the one in the file.

In CI, `.github/workflows/tests.yml` runs the same shards as a matrix — one runner each — and a
final `coverage` job merges every shard's Cobertura and applies the threshold. No single shard
covers enough of the product for a per-shard coverage number to mean anything, so the gate lives
there and nowhere else.

Each shard job uploads its `.trx` as `test-results-<db>-shard-<i>`, and the step is `if: always()`
on purpose: the run step throws on a red shard, so without it the one run whose results are worth
reading is the one that uploads nothing. That artifact is both the failure detail in a form that
outlives a trimmed job log and the input `-FromTrx` wants, so a CI run is a valid thing to re-plan
the partition from. A shard that produced no `.trx` at all warns rather than passing quietly — that
means it died before running tests, and the question is for that job, not for this one.

## A test that was red only under four shards (closed)

`AdminConsoleTests.The_queue_still_shows_an_approval_once_its_erasure_has_run` used to fail in a full
`-Shards 4` run — twice on 2026-09-07, with and without `-IncludeKnownBugs` — and pass everywhere
quieter: 318/318 running `-Shard 3 -Shards 4` on its own, and twice in one process filtered to the
account fixtures. It was never a `KnownPresenceBug`, because nothing about the product was open in
the sense that category means. It is kept here because the shape recurs: a test whose window is
bounded by a neighbour's timer, in a cluster singleton the whole assembly shares.

The account-deletion queue is one Orleans singleton. `AccountDeletionQueueGrain.ReclassifyAsync`
retired a decided entry the moment its deletion reached `Completed` — deliberately, with the
reasoning written out beside it: the decision has played out and the worklist is for work.
`AccountConsoleTests` drives `IAutoDeleteSchedulerGrain.RunScanAsync()` seven times, and every scan
reconciles that singleton. So the test's window — the account is anonymised, the entry is not yet
retired — was bounded by the next neighbouring scan, and nothing in the test controlled when that
landed. Under four concurrent shard processes the console page read came back one to two seconds
after the erasure finished and lost the race: the shard log has the erasure completing at
03:09:32.834 and `Account deletion queue reconciled: 0 enqueued, 3 retired, 0 held` at 03:09:33.440,
before the page was read.

**Closed in the product, on 2026-09-07, by giving a played-out decision a retention window.** Of the
two ways out this section used to list, the other one — re-point the test at an erasure that fails
after step 3 `Anonymize`, where the entry stays `Approved`/`Stranded` with the account already
anonymised — would have pinned the join (defect R21) while leaving the promise the test's name makes
untrue: an operator had between one second and one day to read the outcome of their own approval
before the row vanished, and which of the two they got depended on when a daily sweep happened to
land. A guarantee that thin is not a guarantee, and a suite that is green only because no neighbour
swept is measuring the neighbours.

So `AccountDeletionOptions.DecisionRetention` (seven days shipped, `00:00:30` on the integration host
via `TestServerConfiguration.AccountDeletion`) is now how long a completed entry stays on the queue,
measured from `CompletedAt` — the moment the queue saw the erasure finish — and no reconciliation
from any fixture can retire it earlier. The entry becomes `QueuedAccountDeletionState.Completed`
rather than staying `Approved`, and the ion enum `AccountDeletionQueueEntryState` gained `COMPLETED`
to match, on defect F9's argument one step further along the lifecycle: an "Approved" badge over a
finished erasure reads as one still inside its grace period, where the account holder can yet call it
off. The test now drives a sweep of its own before reading the page — so a reconciliation is part of
what it asserts rather than a hazard it hopes to dodge — and then waits the window out and drives
another, which pins the other half: retention is a reprieve, not a second permanent queue.

That last wait is why `AdminConsoleTests` costs about half a minute more than it did, all of it in
this one test. Re-plan the general shards from a `.trx` (`-FromTrx`) after the next full run if they
have drifted apart.

## Tests that pin an open bug

Some tests are red on purpose. The presence suite was written against the behaviour a user would
expect rather than against the behaviour the server has, so a red test there is a finding — and a
finding is worth keeping in the tree, running, and readable, rather than deleted or quietly
weakened until it passes. The account-lifecycle suite (`AccountDeletionTests`,
`DataExportArchiveTests`, `AccountConsoleTests`) was written the same way and for the same reason.

Such a test carries `[Category("KnownPresenceBug")]` — the category is the suite's marker for "pinned
open bug" whatever the subject, presence or not — and a `<remarks>` paragraph naming the defect
precisely: the file and method it lives in, what happens, what should happen instead, and what would
close it. A remark that says the campaign adjudicated the claim a *design question* rather than a
defect means the same thing for the runner and something different for a reader: the mechanism is
real, the behaviour it demands is a product decision nobody has taken yet, and the test goes green
the day that decision is made and implemented.

**The runner excludes the category by default** — the single-process path, every shard, and CI — so a
plain `./scipts/run-tests.ps1` on a healthy tree is green. `-IncludeKnownBugs` is how you see the
pinned defects, and it is red whenever there are any. As of 2026-09-07 there are four, all in
`AccountDeletionTests` and `AccountConsoleTests`, so the two runs no longer agree — the default one
carries none of them and the `-IncludeKnownBugs` one ends on those four:

```pwsh
./scipts/run-tests.ps1 -IncludeKnownBugs             # everything, pinned defects included
./scipts/run-tests.ps1 -Shards 4 -IncludeKnownBugs   # the same, sharded
dotnet test tests/ArgonComplexTest --filter 'FullyQualifiedName~Presence&TestCategory!=KnownPresenceBug'
```

It is composed in exactly one place — `Get-EffectiveFilter` in `scipts/run-tests.ps1`, which every
`dotnet test` in the script goes through — and both halves are parenthesised, because a shard filter
is an alternation of fixtures and `--filter` binds `&` tighter than `|`. `A|B&TestCategory!=X` would
exclude the category from `B` alone and run every pinned test that lives in `A`;
`(A|B)&(TestCategory!=X)` is the form that means what it reads as. CI takes the switch through the
`includeKnownBugs` workflow input, which is off on everything that gates.

Three rules keep the category honest:

- Nothing is marked without having been run and its failure read. A guessed explanation in a
  `<remarks>` outlives the guess by years.
- The category comes off with the fix, in the same change. One left behind is a test nobody runs.
- It is for a defect that has been confirmed and left open on purpose — never for a test that is
  merely slow, awkward, or occasionally red for reasons of its own. Those get fixed.

**Four tests carry it today**, and every one of them is an adjudicated design question rather than an
agreed defect: the mechanism each describes was reproduced and confirmed, and the review then declined
to call the behaviour wrong until somebody decides what the right behaviour is.

- `AccountDeletionTests.A_space_already_scheduled_for_deletion_does_not_bar_the_owner` (campaign
  verdict `ACC-12`). The account guard in `AccountDeletionGrain.RequestDeletionAsync` is a plain "do
  you own any non-deleted space", and `SpaceDeletionGrain` leaves `SpaceEntity.IsDeleted` false for the
  whole of its own grace, so following the console's own instruction — delete your spaces first —
  stacks a space grace in front of the account grace. The review kept the bar, on the grounds that it
  is what prevents an unrecoverable orphaned space, and moved the fault to the console copy promising a
  transfer the product does not have and to the missing payload on `OwnsSpaces`.
- `AccountDeletionTests.A_second_request_keeps_and_reports_the_original_execution_date` (`ACC-13`) and
  `AccountConsoleTests.RequestDeleteAccount_WhenAlreadyScheduled_StillSaysWhenTheDeletionWillRun`
  (`CON-1`). The grain fills in `ScheduledDeletionAt` on the `AlreadyScheduled` refusal;
  `AccountConsoleService.RequestDeleteAccount` maps every failure through one expression that hard-codes
  both timestamps to null, so the date is discarded before the console sees it. Carrying it through only
  helps a client that refetches, and today's console learns the deadline from `GetMe` at page load and
  disables the button — an enhancement, said the review, not a correctness fix.
- `AccountDeletionTests.A_request_after_completion_says_the_account_is_already_gone` (`ACC-14`).
  `RequestDeletionAsync` collapses `Executing` and `Completed` into `AlreadyScheduled`, so an account
  that has already been erased is told its deletion is still pending and still cancellable. A finer
  refusal is an additive contract decision — an `AlreadyDeleted` arm on `DeleteAccountError` and
  `CancelDeleteError`, regenerated with `ionc` — rather than something to change quietly.

So the two runs differ by exactly those four: a default sharded run is 703 integration tests (701 pass,
one stands down as CockroachDB-only) plus 947 unit tests, and `-IncludeKnownBugs` is 707 integration
tests with those four red. Both counts carry the one further red described under "A test that is red
only under four shards", which is a test-side ordering hazard and belongs to neither list.

The presence campaign's two pins are the other half of the record — both lost the category in the same
change as their fix, which is rule two doing its job:

- `PresenceAggregationTests.ADeviceSwitch_NeverLeavesAConnectedUserOffline` pinned a lost update in
  `UserPresenceService.RecalculateAggregatedStatusAsync` — a read-fold-write with nothing atomic
  about it, so a fold that started before a newly arrived device was indexed could land last and
  leave a connected user cached Offline. It spent one release fixed by a Lua script
  (`IArgonCacheDatabase.FoldRankedSetAsync`), which made the fold atomic at the store and then had to
  be undone: production's cache is Dragonfly, and Dragonfly refuses a script that reads keys it did
  not declare. What serialises presence now is an Orleans grain — `UserPresenceGrain`, one activation
  per user id, deliberately neither `[StatelessWorker]` nor `[Reentrant]` — so the fold, the
  hysteresis decision and the fan-out for one user happen in one turn of one activation, cluster-wide,
  with no script and no distributed lock. That also closes the half no amount of atomicity could:
  two fan-outs for one user finishing in the wrong order, which
  `PresenceRaceTests.Switching_device_never_leaves_the_account_offline_while_the_new_device_is_online`
  reproduced about one round in twenty on a two-core box.
- `PresenceActivityTests.An_activity_that_lapses_under_a_live_session_is_retracted_from_the_room`
  pinned the residual half of the activity-lifetime defect: nothing announced a lapse, so the members
  already in a room kept rendering a game the snapshot had forgotten. It is closed by a sweep rather
  than by the keyspace-expiry subscriber it looked like it needed — the entry carries a lease stamp
  beside it, and `UserSessionGrain.UserSessionTickAsync`, which already reads that stamp to decide
  whether to renew, now asks the user's `IUserPresenceGrain` to retract an activity whose key is gone.

## How the integration suite is wired

One PostgreSQL (or CockroachDB), one Redis, one NATS and **one** Argon server are started for the
whole assembly by `GlobalTestSetup` and shared by every fixture. Migrations — around a hundred of
them — run once.

That is what makes fixture-level parallelism possible. `AssemblyInfo.cs` sets
`ParallelScope.Fixtures`: fixtures run concurrently, tests inside a fixture run in order. Each
fixture owns its own `IonClient` and bearer token (`TestBase`), so two fixtures can never
authenticate as each other. Tests needing two identities at once should take `CreateSessionAsync()`
rather than juggling the ambient token.

If a fixture mutates genuinely global server state and cannot tolerate a neighbour, mark it
`[NonParallelizable]` — but prefer making the test allocate its own space/user/flag instead.

## Choosing a database

Production runs CockroachDB. Tests default to PostgreSQL because it starts in a couple of seconds
rather than tens of them, and because every Argon migration is portable once the Cockroach-only
pieces are switched off:

- `Database:Provider` (`PostgreSql` / `CockroachDb`) decides whether `MultiregionalMigrationsSqlGenerator`
  is installed. On PostgreSQL the stock Npgsql generator runs instead and the `LOCALITY` /
  `WITH (ttl = 'on')` clauses are simply not emitted.

  Spell it exactly — these are `DatabaseProviderKind` members. This line used to read `Postgres`, which
  parses to no member, and an unparsable value resolves to `CockroachDb` rather than failing. So a
  PostgreSQL deployment configured from this file announced itself as CockroachDB. The boot path now
  probes the server with `version()` and refuses to start on a mismatch, but the spelling is still the
  thing to get right.
- `PostgresCompatibilityShims` defines `unique_rowid()`, the one CockroachDB built-in the migration
  history bakes into column defaults.

Nothing in the migration history is rewritten, so both engines replay exactly the same SQL. The
Cockroach-specific DDL is exercised by the nightly `test-cockroach` job.

## Environment variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `ARGON_TEST_DB` | `postgres` | `postgres` or `cockroach`. |
| `ARGON_TEST_DB_IMAGE` | per engine | Override the database image. |
| `ARGON_TEST_REDIS_IMAGE` | `redis:7-alpine` | The cache image. Point it at Dragonfly to run against what production actually has — see below. |
| `ARGON_TEST_NATS_IMAGE` | `nats:2.10-alpine` | |
| `ARGON_TEST_REUSE_CONTAINERS` | off | Keep containers alive between runs (needs `testcontainers.reuse.enable=true`). |
| `ARGON_TEST_STARTUP_TIMEOUT` | `300` | Seconds to wait for the stack. |
| `ARGON_TEST_LOGS` | off | Write the server's own logs to the test output. |
| `ARGON_TEST_LOG_LEVEL` | `Warning` | Level for the above. |

`ARGON_TEST_LOGS=1 ARGON_TEST_LOG_LEVEL=Debug` is the first thing to reach for when an Ion call
comes back as a bare `UPSTREAM_ERROR: Internal Server Error` — the useful exception is server-side.

## Running against Dragonfly

Production's cache is Dragonfly, not Redis, so anything that touches the cache is worth running
against it before it is believed. One variable is the whole recipe — `RedisBuilder` in
`Infrastructure/ArgonTestEnvironment.cs` starts whatever image `ARGON_TEST_REDIS_IMAGE` names
(`Infrastructure/TestEnvironmentOptions.cs` holds the default), Dragonfly answers the same protocol
on the same port and ships the `redis-cli` the Testcontainers module's wait strategy shells out to,
and the first run pays for a ~200 MB pull:

```pwsh
$env:ARGON_TEST_REDIS_IMAGE = 'docker.dragonflydb.io/dragonflydb/dragonfly'
./scipts/run-tests.ps1 -Shards 4          # or -Shard 2 -Shards 4 for presence alone
```

Measured on this tree it is indistinguishable from Redis: the same 178 s over four shards, the same
625 integration and 929 unit tests green, no `WRONGTYPE`, no unknown command, no unsupported option.
That is the point of the exercise rather than a formality — the aggregate fold spent a release as a
Lua script and had to be undone precisely because Dragonfly refuses `EVAL` over keys the script does
not declare (see "Tests that pin an open bug"), and what is left is exactly the surface a
compatibility gap would show up on: `SET … GET` under
`IArgonCacheDatabase.StringSetAndGetPreviousAsync`, `GETEX` under `KeyExpireAsync`, the
`SADD`/`SREM`/`SMEMBERS` session index in `UserPresenceService`, and the `SCAN` that
`UserPresenceMetricsService` sweeps `presence:user:*:session:*` with. Nothing in `src/` calls `EVAL`,
`EVALSHA` or `ScriptEvaluate` any more, and a grep for those three is the cheap way to keep it so.

## Coverage

`tests/coverlet.runsettings` is passed to `dotnet test` only when `-Coverage` is asked for — a
`<DataCollector>` is enabled by being declared, so handing that file to every run had coverlet
instrument the whole of `Argon.Core` and `Argon.Api` and write a report nobody wanted. It restricts
measurement to `Argon.Core` and `Argon.Api` and excludes machine-authored code — EF migration snapshots alone are ~120k lines no test can execute
line-by-line. Reports land in `artifacts/coverage` (`index.html` for browsing, `Summary.txt` for the
number the gate reads).
