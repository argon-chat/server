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
./scipts/run-tests.ps1 -IncludeKnownBugs        # the pinned-defect tests too; expected red
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
| `presence` | the eleven `Presence*Tests` | 606 s of the suite's 914 s of serial work, spent waiting on grain timers and Redis TTLs rather than on a CPU — so this shard runs eight workers rather than four, and eight waiting fixtures overlap for free. |
| `general-N` | everything else | Split by measured serial seconds, longest first, so the shards finish together. |

Measured on this tree, PostgreSQL, no coverage, 32 cores: **369 s single-process → 181 s over four
shards** (per shard: topology 178 s, presence 142 s, general-1 82 s, general-2 68 s). Same 620
integration tests, same outcomes: 619 pass and the rest stand down, being CockroachDB-only and this
being PostgreSQL. The 929-test unit suite rides along on `topology` inside that number, four seconds
of it. The floor is the topology shard, and it is a container stack plus two fixtures that
stand up silos of their own and therefore run alone: `RoleStartupTests` (69 s) and
`GrainMigrationTests` (41 s). Shrink those and re-plan.

Those numbers are far better than the ones this section used to carry (707 s single-process, 368-382 s
sharded, with presence alone at 1 614 s of serial work), and almost all of the difference is one change: the
presence fixtures no longer wait out shipped clocks. `PresenceTimingOptions` makes the session TTL,
the refresh tick, the grace and the rest configuration, and the integration host sets them a factor
of ten down (`TestPresenceTimings`). It is ordinary configuration bound by the ordinary options
pipeline — nothing is stubbed, no branch is skipped, and the fixtures express every wait as a ratio
of what the host actually bound (`PresenceWaits`), so the same assertions would still hold, slowly,
at production values.

The presence shard is also why `-Workers` exists: its fixtures spend their time waiting on clocks
rather than on a CPU, so it asks for eight and packs 606 s of serial work into 142 s of wall. If you
change it, measure both settings again — the four-worker figure this paragraph used to quote was
taken before the presence clocks were compressed and no longer means anything.

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

## Tests that pin an open bug

Some tests are red on purpose. The presence suite was written against the behaviour a user would
expect rather than against the behaviour the server has, so a red test there is a finding — and a
finding is worth keeping in the tree, running, and readable, rather than deleted or quietly
weakened until it passes.

Such a test carries `[Category("KnownPresenceBug")]` and a `<remarks>` paragraph naming the defect
precisely: the file and method it lives in, what happens, what should happen instead, and what would
close it.

**The runner excludes the category by default** — the single-process path, every shard, and CI — so a
plain `./scipts/run-tests.ps1` on a healthy tree is green. `-IncludeKnownBugs` is how you see the
pinned defects, and that run is expected to be red:

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

One test carries it today:

| Test | What it pins |
| --- | --- |
| `PresenceActivityTests.An_activity_that_lapses_under_a_live_session_is_retracted_from_the_room` | The residual half of the activity-lifetime defect. The session tick now renews `activity:user:{u}:session:{sid}`, so an activity no longer lapses under a session that is still announcing it — but when one does lapse anyway, Redis emits no event and nothing sweeps, so the members already in the room keep rendering a game the snapshot has forgotten. Closing it needs a keyspace-expiry subscriber or a server-side sweep. |

`PresenceAggregationTests.ADeviceSwitch_NeverLeavesAConnectedUserOffline` used to be the second, and
its category came off with the fix rather than in a later tidy-up, which is rule two doing its job.
The lost update it pinned was in `UserPresenceService.RecalculateAggregatedStatusAsync` — a
read-fold-write with nothing atomic about it, so a fold that started before a newly arrived device
was indexed could land last and leave a connected user cached Offline. The fold is now a single
`IArgonCacheDatabase.FoldRankedSetAsync`, one Lua script that reads the index, reads every session's
status and writes the aggregate with nothing able to get between the three, so the last write is by
construction the one that saw the most recent state. The test is a plain `[Test]` again and runs in
the gate.

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
| `ARGON_TEST_REDIS_IMAGE` | `redis:7-alpine` | |
| `ARGON_TEST_NATS_IMAGE` | `nats:2.10-alpine` | |
| `ARGON_TEST_REUSE_CONTAINERS` | off | Keep containers alive between runs (needs `testcontainers.reuse.enable=true`). |
| `ARGON_TEST_STARTUP_TIMEOUT` | `300` | Seconds to wait for the stack. |
| `ARGON_TEST_LOGS` | off | Write the server's own logs to the test output. |
| `ARGON_TEST_LOG_LEVEL` | `Warning` | Level for the above. |

`ARGON_TEST_LOGS=1 ARGON_TEST_LOG_LEVEL=Debug` is the first thing to reach for when an Ion call
comes back as a bare `UPSTREAM_ERROR: Internal Server Error` — the useful exception is server-side.

## Coverage

`tests/coverlet.runsettings` is passed to `dotnet test` only when `-Coverage` is asked for — a
`<DataCollector>` is enabled by being declared, so handing that file to every run had coverlet
instrument the whole of `Argon.Core` and `Argon.Api` and write a report nobody wanted. It restricts
measurement to `Argon.Core` and `Argon.Api` and excludes machine-authored code — EF migration snapshots alone are ~120k lines no test can execute
line-by-line. Reports land in `artifacts/coverage` (`index.html` for browsing, `Summary.txt` for the
number the gate reads).
