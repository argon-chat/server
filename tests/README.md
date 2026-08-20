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
./scipts/run-tests.ps1 -Reuse                   # keep containers between runs
```

Plain `dotnet test` works too; the script only adds the run settings, the coverage merge and the
threshold check.

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

`tests/coverlet.runsettings` restricts measurement to `Argon.Core` and `Argon.Api` and excludes
machine-authored code — EF migration snapshots alone are ~120k lines no test can execute
line-by-line. Reports land in `artifacts/coverage` (`index.html` for browsing, `Summary.txt` for the
number the gate reads).
