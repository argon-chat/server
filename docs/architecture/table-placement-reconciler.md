# Regionality by reconciliation

**Automatic detection of the cluster's regional state, and convergence of an existing database towards the placement the model declares — without a migration squash.**

Status: design. Nothing in this document has been implemented. Every behavioural claim is tagged with the CockroachDB version it was checked against and with whether it was **verified** (fetched from official v25.3 documentation or read in `cockroachdb/cockroach` at `release-25.3`), **read from this repository**, or **inferred**.

---

## 0. What is actually broken, and what this replaces

`ArgonTablePlacement` declares ten tables `GLOBAL` and `Messages` `REGIONAL BY ROW`. `MultiregionalMigrationsSqlGenerator` emits the `LOCALITY` clause from exactly one place — its `CreateTableOperation` override — and EF produces no migration operation when an annotation changes on a table that already exists (`DbLocalityTests.Changing_a_locality_after_the_table_exists_produces_nothing` pins that on purpose). `grep -rl "Regional:Locality" src/Argon.Core/Migrations/` returns nothing across 103 migration files. The declaration has therefore never reached any database and cannot, by the mechanism that exists.

The previously-identified fix was to squash the migrations, which means resetting `__EFMigrationsHistory` against a schema that already exists in production: coordinated, one-way, unrepeatable, and unverifiable until after it has run.

This design replaces it with a reconciler. **Yes, it removes the need for the squash** — see §8, which also says what happens to the `CreateTable` override so that two sources of truth do not appear.

It also says no to something the brief did not ask about but that the audit in §5b forces: **the current contents of `ArgonTablePlacement` are not a desired state worth reconciling towards.** Four or five of the ten `GLOBAL` declarations are wrong on the write path, and a reconciler that faithfully applies a wrong desired state is worse than no reconciler. The audit lands in the model first; the apply path is enabled second.

### Resolved since this was written: the connection-string question

An earlier plan treated "one connection string or per-region connection strings" as a decision that
blocked the migration work, on the reasoning that `crdb_region` defaulting to `gateway_region()` is only
correct if each region's pods enter through their own Cockroach gateway.

That precondition holds already, and it needs no configuration axis. There is **one logical Cockroach
cluster spanning the regions** — `deploy/docker-compose.local.yml` joins three nodes with
`--join=cockroach1,cockroach2,cockroach3` under `--locality=region=ru-central,zone=ru-3`,
`region=eu-central,zone=eu-1` and `region=us-east,zone=us-1` — and each Kubernetes cluster resolves the
same service name to its own local nodes. Same connection string in every region, different gateway per
region, by DNS rather than by config. Nothing to decide and nothing to add.

Two things this does **not** change. The backfill still homes every existing row to the primary region
(§6b), so the computed-column path remains the right conversion for `Messages` rather than an
optimisation. And the placement audit in §5b is still a prerequisite for enabling apply — it is about
which tables should be `GLOBAL` at all, which has nothing to do with how the connection is made.

One consequence worth stating because it is now a live property rather than a hypothetical: a network
partition between the Kubernetes clusters partitions the Cockroach cluster too. Under
`SURVIVE ZONE FAILURE` every regional range homed in the minority side loses quorum. With three regions
configured, `SURVIVE REGION FAILURE` becomes available and is the goal that matches this topology — see
§4 for why the reconciler will report that gap rather than close it on its own.

---

## 1. Desired state — the EF model annotations, read at runtime

### Where it comes from

Two annotations, both already written:

- `Regional:MultiRegion` on the model — JSON `{Primary, Regions, Survive}`, set by `UseMultiRegionDatabase`.
- `Regional:Locality` on each entity type — a raw SQL fragment: `GLOBAL`, `REGIONAL BY ROW`, `REGIONAL BY TABLE`, `REGIONAL BY TABLE IN "<region>"`.

The reconciler reads them from the live `DbContext.Model`. Custom annotations survive EF Core 10's runtime-model pruning — `RuntimeModelConvention` strips only keys in `CoreAnnotationNames.AllNames` / `RelationalAnnotationNames`, and `Regional:*` is in neither — so no `IDesignTimeModel`, no design-time package, and no migration is involved in reading the declaration. `RegionTaggedIdTests.Context()` already constructs the real `ApplicationDbContext` over a connection string nothing dials and walks `Model.GetEntityTypes()`; the desired-state computation is testable the same way, in the ~11-second `ArgonSharedLogicTest`, against the actual production model rather than a toy one.

**Rejected: reading from `migration.TargetModel` or a `.Designer.cs` snapshot.** They are the reason the problem exists. They carry `Regional:MultiRegion` and not one `Regional:Locality`, and the payload they carry is stale in a way Cockroach would reject — `20251101075823_Initial.Designer.cs` froze `{"Primary":"ru-central","Regions":["us-east","eu-central","ru-central"],"Survive":"REGION FAILURE"}` while every later Designer froze one region with `REGION FAILURE`, which needs three (`at least %d regions are required for surviving a region failure`, `minNumRegionsForSurviveRegionGoal = 3`). Those payloads are inert and must be understood as inert.

### How it is keyed

```
key   = (entityType.GetSchema() ?? "public", entityType.GetTableName())
value = TablePlacement parsed from FindAnnotation("Regional:Locality")
```

Entity types with a null `GetTableName()` are skipped (owned types, query types). The table name is what the *model* says, not what the `DbSet` property is called, and the two differ: `SpaceMemberEntity` maps to `UsersToServerRelations`, and `ChannelGroupEntity` has no `ToTable` at all so its table is literally `"ChannelGroupEntity"` (verified in `20251230152442_ChannelGroups.cs`). A reconciler keyed on anything but `GetTableName()` would emit `ALTER TABLE "ChannelGroups"` against a table that does not exist.

Grouping is by `(schema, table)` and a conflict is a hard error. `MultiregionalMigrationsSqlGenerator.HasAnnotation` uses `GetEntityTypes().FirstOrDefault(...)`, and the model has TPH inheritance (`ItemUseScenario` with a `ScenarioType` discriminator and four derived types), so several entity types can map to one table and `FirstOrDefault` picks by model-build order. Copying that would make physical data placement depend on the order entity types were registered in. Two entity types on one table with two different declared localities is a modelling mistake and must throw with both names in the message.

### Normalisation

Three declarations render as the same physical state and must compare equal:

| declared | what the server reports |
|---|---|
| `REGIONAL BY TABLE` | `REGIONAL BY TABLE IN PRIMARY REGION` |
| no annotation at all | `REGIONAL BY TABLE IN PRIMARY REGION` |
| `REGIONAL BY TABLE IN "ru-central"` where `ru-central` is primary | `REGIONAL BY TABLE IN PRIMARY REGION` |

Without one canonicalising function applied to both sides, the ~40 tables `ArgonTablePlacement` does not name would each show a spurious diff and a naive reconciler would emit forty pointless `SET LOCALITY` statements on every boot. This is the single piece most worth unit-testing and it needs no database.

### The one change to the annotation itself

`Regional:Locality` should stop being a raw SQL fragment and become a structured value — `TablePlacement(Kind, Region?)` with `Kind ∈ {Global, RegionalByTable, RegionalByRow}`. It is provably free to do: zero of 103 migration files reference the key, so there is no serialised shape with a backward-compatibility burden. The payoff is that the generator and the reconciler share one renderer instead of one of them parsing what the other printed. Do it in the same change that adds the reconciler, or the string normalisation above has to exist twice.

### What the desired state deliberately does *not* include

**The region set.** `Database:Regions:ReplicateRegion` is a JSON list of regions and this design stops reading it. Regions are observed from the database (§2) and changed only by an explicit operator command (§4), so there is nothing for a declared list to be the source of. `Database:Regions:PrimaryRegion` and `ReplicateRegion` become *assertions* checked loudly at boot against what the server reports, and are candidates for deletion once nothing reads them. `Regional:MultiRegion` keeps its one remaining job: telling `CREATE DATABASE` what to say when there is no database yet to read from.

**The survival goal.** See §4 — it is refused, not reconciled.

---

## 2. Observed state — the actual queries, and the Cockroach-vs-Postgres guard

### The guard is three conditions, all of which must hold

**(a) The engine, probed rather than declared.** `DatabaseFeature.GetDatabaseProviderKind` reads `Database:Provider` and returns `CockroachDb` when the key is unset *or unparsable*, and `WarmUp` re-applies the same default when the singleton is missing entirely (`GetService<DatabaseProvider>()?.Kind ?? DatabaseProviderKind.CockroachDb`). That is a declaration, and it fails open in the dangerous direction: `tests/README.md` documents the value as `Postgres`, which parses to no member of `DatabaseProviderKind` (`PostgreSql`), so a PostgreSQL deployment configured from this repository's own documentation gets `IsCockroach == true`. For a thing that emits `ALTER` against production, mis-detecting Postgres as Cockroach emits DDL that errors, and mis-detecting Cockroach as Postgres silently no-ops the fix that is the entire point.

The probe, in preference order:

```sql
-- zero round trips: CockroachDB sends crdb_version as a pgwire ParameterStatus in the
-- startup message set; PostgreSQL never does. Npgsql 10.0.3 exposes the dictionary as
-- NpgsqlConnection.PostgresParameters.
conn.PostgresParameters.ContainsKey("crdb_version")

-- fallback, one round trip, survives any pooler because it is a backend function call
-- rather than a SHOW: returns the full version string on Cockroach, NULL on PostgreSQL 17.
SELECT current_setting('crdb_version', true);
```

Do **not** branch on `NpgsqlConnection.ServerVersion` or the `server_version` GUC: CockroachDB reports `13.0.0` / `130000` and is indistinguishable from a real PostgreSQL 13 by that route (verified: `pkg/sql/vars.go` `PgServerVersion = "13.0.0"` at v24.3 through v26.2; `master` moved it to `18.0.0`, which makes any value whitelist break on upgrade). Do not use the one-argument `current_setting('crdb_version')` — it raises 42704 on PostgreSQL.

Keep `Database:Provider` as an override and kill-switch. A disagreement between the probe and the config is a loud startup error, not a silent override in either direction.

**(b) The cluster has regions at all.**

```sql
SHOW LOCALITY;
```

v25.3 docs, verbatim: *"No privileges are required to list the locality of the current node."* and *"If locality was not specified on node startup, the statement returns an empty row."* An empty row is the clean, privilege-free signal for "this Cockroach cluster has no regions" — the exact failure `WarmUpExtensions.CreateDatabaseAsync` already found the hard way and wraps in a readable message. It must degrade to a silent, logged-once no-op, not to a boot failure.

Its second job: the region tier of this node's locality is the region this pod's *database gateway* is in. Cross-check it against `Argon:Regions:Self`. A disagreement means the pod is writing rows homed somewhere other than where its Orleans cluster believes it is, which is invisible at runtime and catastrophic once `REGIONAL BY ROW` is real.

**(c) The database is multi-region.** From `SHOW CREATE DATABASE` (below). A database with exactly one region still counts — v25.3 docs are explicit that a single configured region is still a multi-region database — and Argon's shipped configuration already produces one, so this condition will answer yes on any database Argon created itself.

### The read surface, and the alternative rejected

**Rejected: `crdb_internal.databases`, `crdb_internal.tables`, `crdb_internal.regions`.** They are structurally nicer — typed columns, no parsing, `locality` strings byte-identical to the `LOCALITY` clause. Three reasons not to:

1. The v25.3 `crdb_internal` documentation marks all three ✗ (not production-safe) and states: *"The contents of these tables are unstable, and subject to change in new releases of CockroachDB, without prior notice. There are memory and latency costs associated with each table in `crdb_internal`. Accessing the tables in the schema can impact cluster stability and performance."*
2. `crdb_internal.tables` **silently omits rows** for tables the caller has no privilege on. A reconciler reading it as an under-privileged role sees fewer tables and concludes "nothing to do".
3. Its `locality` column is NULL both for "no privilege" and for "this table has no locality configured" — and the second is Argon's state on every table today, which is the exact situation the reconciler exists for. The two failure modes are indistinguishable at the point where it matters most.

**Chosen: the documented `SHOW CREATE …` statements.** v25.3 docs, Required privileges: *"The user must have any privilege on the target database, function, table, view, or sequence."* The app role has privileges on all of them by construction. The response columns are documented (`database_name, create_statement` / `table_name, create_statement`), and `SHOW CREATE DATABASE` is documented to render `PRIMARY REGION`, `REGIONS` and `SURVIVE`. The cost is string matching, and that cost is bounded because Cockroach renders the locality clause in precisely the syntax the annotation emits.

`TablePlacementTests.CreateTableSqlAsync` already uses exactly this technique, which means the acceptance test and the observed-state reader share a code path rather than agreeing by coincidence.

### The queries

```sql
-- 1. engine + full version, in one value. Store it; every version-specific
--    decision below is gated on the patch level, not the major.
SELECT current_setting('crdb_version', true);

-- 2. does this cluster have regions, and which one is this gateway in.
SHOW LOCALITY;

-- 3. the database's own regionality — primary, region set, survival goal.
--    Quoted: the database name comes from the connection string, not from a literal.
SHOW CREATE DATABASE "argon";
--   → CREATE DATABASE argon PRIMARY REGION "ru-central" REGIONS = "ru-central"
--     SURVIVE ZONE FAILURE

-- 4. per declared table only — never "every table in the database".
SHOW CREATE TABLE "Users";
SHOW CREATE TABLE "UsersToServerRelations";
SHOW CREATE TABLE "ChannelGroupEntity";
SHOW CREATE TABLE "Messages";
--   the trailing clause is one of:
--     ) LOCALITY GLOBAL
--     ) LOCALITY REGIONAL BY ROW
--     ) LOCALITY REGIONAL BY ROW AS crdb_region
--     ) LOCALITY REGIONAL BY TABLE IN PRIMARY REGION
--     ) LOCALITY REGIONAL BY TABLE IN "eu-central"
--   or there is no LOCALITY line at all, on a database that is not multi-region.

-- 5. in-flight schema work, before issuing anything and before declaring convergence.
WITH j AS (SHOW JOBS)
SELECT job_id, job_type, status, running_status, fraction_completed, description, error
FROM j
WHERE job_type IN ('SCHEMA CHANGE', 'TYPEDESC SCHEMA CHANGE', 'NEW SCHEMA CHANGE')
  AND status IN ('pending', 'running', 'paused');
```

Parsing rule, and it is a refusal rather than a heuristic: on a **multi-region** database Cockroach always renders a `LOCALITY` line. So *multi-region + no `LOCALITY` line parsed* means the reader failed, not that the table is unplaced — the reconciler must refuse and report a parse failure, never treat it as drift and emit an ALTER.

### The read the design deliberately does not depend on

```sql
SHOW REGIONS FROM CLUSTER;   -- (region, zones)
```

v25.3 docs: *"Only members of the `admin` role can run `SHOW REGIONS`."* `release-25.3`'s `systemStatusServer.Regions` RPC contains no privilege check where its neighbour `NodesList` calls `RequireViewClusterMetadataPermission`, so docs and source disagree and empirical reports go both ways. **The design is built so the answer does not matter.** Nothing in the automatic tier reads it. The operator-triggered path reads it, and that path runs with a credential that can add a region anyway. A 42501 is reported as *"cannot determine cluster regions"* and never as *"converged"*.

### On PostgreSQL

None of the above is reached, because (a) gates. For completeness, each fails distinctly rather than returning a plausible wrong answer: `SHOW CREATE DATABASE x` → 42601 syntax error; `SHOW LOCALITY` → 42704 unrecognized configuration parameter `"locality"`; `SHOW REGIONS FROM CLUSTER` → 42601; `SELECT … FROM crdb_internal.*` → 42P01. Note the trap that makes probing-by-failure a bad idea: PostgreSQL parses `SHOW TABLES` and `SHOW DATABASES` as GUC lookups and answers 42704, not 42601, so a syntax-error check is not a reliable "this is PostgreSQL" marker. Gate up front.

The reconciler is *registered* inside the existing `if (providerKind is DatabaseProviderKind.CockroachDb)` branch in `DatabaseFeature.AddPooledDatabase`, so on Postgres the service does not exist rather than existing-and-branching. An object that is absent cannot be called by mistake, and the guard sits in the one place the team already knows to look. The runtime probe is a second, independent gate on top of it, because that config branch fails open.

---

## 3. The diff, and the ordered plan

### Ordering is fixed by CockroachDB, not by preference

1. `SET PRIMARY REGION` — nothing else is legal first. v25.3 docs, ADD REGION: *"In order to add a region with `ADD REGION`, you must first set a primary database region with `SET PRIMARY REGION`, or at database creation."* And `SET LOCALITY` on a non-multi-region database fails with 42P16 `cannot alter a table's LOCALITY if its database is not multi-region enabled`.
2. `ADD REGION IF NOT EXISTS`, in a deterministic order.
3. `SURVIVE …`.
4. Per-table `SET LOCALITY`, cheap first.

Region **add order is permanently load-bearing** and this is the non-obvious reason step 2 is sorted rather than "whatever the cluster returned". `RESTORE` of `REGIONAL BY TABLE`/`REGIONAL BY ROW` tables requires that source and destination databases have the same set of regions, *added in the same order*, with the same primary. An auto-discovering reconciler that iterates regions in `SHOW REGIONS` order produces a different ordering in staging than in production and silently breaks restore of production backups into staging — a failure that surfaces months later, during an incident. The order is part of the schema contract.

### The statement shapes

Each is issued alone, autocommitted, with no surrounding transaction.

```sql
-- database level
ALTER DATABASE "argon" SET PRIMARY REGION "ru-central";
ALTER DATABASE "argon" ADD REGION IF NOT EXISTS "eu-central";
ALTER DATABASE "argon" SURVIVE ZONE FAILURE;

-- table level
ALTER TABLE "Users"                  SET LOCALITY GLOBAL;
ALTER TABLE "UsersToServerRelations" SET LOCALITY REGIONAL BY TABLE IN PRIMARY REGION;
ALTER TABLE "Channels"               SET LOCALITY REGIONAL BY TABLE IN "eu-central";
ALTER TABLE "Messages"               SET LOCALITY REGIONAL BY ROW AS "crdb_region";
```

`ADD REGION IF NOT EXISTS` is real and is the cheapest correctness win in the design — one keyword between "second run is a no-op" and "second run dies with 42710 `region %q already added to database`". **Verified** in `release-25.3` `pkg/sql/parser/sql.y`: `alter_database_add_region_stmt` has two alternatives, the second setting `IfNotExists: true`; the runtime path catches `pgcode.DuplicateObject` and buffers the notice `region %q already exists; skipping`. The mirror `DROP REGION IF EXISTS` exists too, and this design never emits it.

Identifiers are quoted throughout: table names are mixed case and an unquoted one folds to lower (`TablePlacementTests` already depends on this), and region names contain hyphens.

### The shape of the runner: level-triggered, no stored plan

```
loop:
  read observed  (queries 2,3,4)
  read in-flight (query 5) → if a matching job is running, adopt and wait, do not re-issue
  compute delta  (desired ∩ tier policy) − observed
  if delta empty → converged, done
  take the first statement, by the fixed order
  classify its tier
  if tier > allowed-by-mode → report, skip, continue
  issue exactly one statement, autocommitted
  write one append-only audit row (separate statement, never in the DDL's transaction)
  goto loop
```

**The plan is never persisted and never resumed from.** A stored plan can be wrong in both directions: the process died after issuing and before recording (looks pending, is running), or the record says done and the background job later failed. Correctness comes from re-deriving from the catalog, every pass, always. This is the Kubernetes-controller shape and it is what makes it survive a crash at any step.

### There is no transaction, and that is not negotiable

Verified, v25.3: *"CockroachDB does not support schema changes within explicit transactions with full atomicity guarantees. CockroachDB only supports DDL changes within implicit transactions (individual statements)."* Only `CREATE TABLE` and `CREATE INDEX` have full atomicity inside a transaction. Mixing DDL with non-DDL in one transaction risks `XXA00`: `transaction committed but schema change aborted with error … Manual inspection may be required`.

Two consequences that shape the code:

- The audit row must be its own statement. Writing it in the same transaction as the DDL is precisely the XXA00 trap.
- Do not route this through EF's migration pipeline, which wraps in a transaction by default. In an explicit transaction, a failed schema-change job's error is *replaced* by the "transaction committed but schema change failed" serious-error code and the original pgcode is lost — so the one thing that would tell you what went wrong is discarded.

And design as if there is no transaction rather than depending on which way `autocommit_before_ddl` is set: with it on, an explicit transaction is committed underneath you before the schema change and `ROLLBACK` returns a *warning* instead of an error.

### Never converge downward

Three rules, and together they are what make this safe under a hard-reboot deployment where every pod comes up at once running the same code:

- A table reporting a locality the model does not declare is **reported, never overwritten**. If a human set `REGIONAL BY TABLE IN "eu-central"` on a table the model calls `GLOBAL`, that is drift-to-report.
- A region the database has and nothing declares is **reported, never dropped**.
- A table the model does not name is **not touched**. `__EFMigrationsHistory` and `__MigrationLock` are not EF entity types and are therefore invisible to a model-driven reconciler, which is exactly right — they keep Cockroach's default.

---

## 4. Three tiers, and one category of permanent refusal

### Tier A — applies automatically, on the boot path, unattended

`ALTER TABLE … SET LOCALITY {GLOBAL | REGIONAL BY TABLE …}` on tables the model declares, **and only while the database has exactly one region.**

*Why this boundary and not a bigger one.* These statements are metadata plus a zone config: `alterTableLocalityToGlobal` and its siblings mutate the descriptor's `LocalityConfig`, adjust the enum back-reference, and call `ApplyZoneConfigForMultiRegionTable`. No backfill, no index rebuild, no primary-key swap. And with exactly one region, every locality is *semantically* the same thing — `GLOBAL`'s cost is cross-region write latency and `REGIONAL BY ROW`'s is cross-region homing, and neither exists when there is one region. So on a one-region database this is a metadata reconfiguration with no physical consequence at all. The instant a second region exists, the identical statement moves replicas across a WAN, and it drops to Tier B automatically.

That self-limiting envelope is the point: the reconciler is boldest exactly where it is safest, and quiets itself as the deployment grows, with no configuration change to remember.

*Precondition that is not negotiable:* Tier A is enabled **after** the placement audit in §5b lands in `ArgonTablePlacement`. Applying today's declarations automatically would put a commit-wait on the message-send path.

### Tier B — requires an explicit operator action (CLI or Kubernetes Job)

- `ALTER DATABASE … SET PRIMARY REGION` on a database that has none.
- `ALTER DATABASE … ADD REGION`.
- Any `SET LOCALITY` once the database has two or more regions.
- The `Messages` → `REGIONAL BY ROW` conversion (§6).

*Why.* The first `SET PRIMARY REGION` is the single most expensive statement in the whole design: `setInitialPrimaryRegion` creates the region enum, sets the database zone config, and calls `addDefaultLocalityConfigToAllTables`, which walks **every** table and makes it `REGIONAL BY TABLE` in the primary region. The docs say the consequence plainly — all such tables *"will have all of their voting replicas and leaseholders moved to the primary region"* — and warn that the cluster *"may not be able to handle its normal workload"* until rebalancing settles, with a recommendation to do it during scheduled maintenance. That is not a pod-boot statement. `ADD REGION` repartitions every `REGIONAL BY ROW` table and blocks index modifications on them while it runs.

There is also a hard operational reason the boundary must exist: **every silo role runs the schema path.** `WarmUp` returns early only for `RoleDescriptor.IsClient`, and six silo roles (core, voice, media, moderation, commerce, jobs) plus dev do not. On a hard reboot that is dozens of pods across six deployments arriving at the same instant. A cluster-wide rebalance must not be reachable from that path at all, lease or no lease.

The entry point is `ArgonClusterCli` — the argv front-end that already runs before the host is built and returns a process exit code, the same shape `--validate-config` uses. `--schema-plan` and `--schema-apply` cost almost nothing to add, and the Kubernetes Job is then the same image with different argv, with no second artifact to drift out of sync.

### Tier C — refused in every mode, reported only, no flag enables it

- **`DROP REGION`, in any form.** It repartitions every `REGIONAL BY ROW` table; dropping the last region strips locality from every table in the database; and the row-level safety check runs inside the type-schema-change job, which means the failure arrives after minutes of scanning. It fails rather than deleting rows — `canRemoveEnumValueFromTable` returns `could not remove enum value %q as it is being used by %q in row: %s` — but that is a reason to be relieved, not a reason to automate it. A reconciler that converges "declared regions" downward by dropping is a categorically more dangerous object than one that only ever adds. Region removal is a human operation with a runbook (`docs/architecture/region-lifecycle.md` already owns it).
- **Secondary regions, entirely.** Not modelled, not read, not written. v25.3 docs: *"Secondary regions are not compatible with databases containing `REGIONAL BY ROW` tables. CockroachDB does not prevent you from defining secondary regions on databases with regional by row tables, but the interaction of these features is not supported."* Argon declares one `REGIONAL BY ROW` table. Leaving the surface out removes a whole class of unsupported-interaction bugs, and Cockroach will not stop anyone creating one by hand.
- **Any change to the survival goal.** `UseMultiRegionDatabase` currently *derives* it: `survive ?? (regions.Count >= 3 ? "REGION FAILURE" : "ZONE FAILURE")`. As a `CREATE DATABASE` default that derivation is correct and matches Cockroach's own rule exactly, and the comment explaining it is right. As a *reconciliation target* it means that adding a third region silently upgrades survival and re-replicates the entire database — the most expensive operation in the system, triggered as a side effect of an unrelated change. Survival becomes an explicit `Database:Regions:Survive`; the reconciler compares it, reports a mismatch, and never issues the statement. Note also that Cockroach validates `SURVIVE REGION FAILURE` (fewer than three regions → 22023) but accepts `SURVIVE ZONE FAILURE` unconditionally, so guessing low always succeeds and silently under-delivers availability. The database will never complain; a log line must.
- **Any table the model does not declare**, and any locality the model has no rule for.
- **Emitting any of this through a migration file.** `MigrationPortabilityTests` already forbids `LOCALITY GLOBAL`, `LOCALITY REGIONAL`, `PRIMARY REGION`, `SURVIVE REGION`, `SURVIVE ZONE`, `ttl_expiration_expression`, `ttl_job_cron` from appearing in a migration's string literals, because migrations must replay byte-for-byte on PostgreSQL and *"there is no shimming a syntax error"*. Add `SET LOCALITY` to that list in the same change.

### Also refused: reporting "converged" when it could not look

If a read returns 42501, the verdict is **undetermined**, not **converged**. A reconciler that reports success because it lacked permission to look is the worst failure available to it, and it is a silent one.

---

## 5. Concurrency and resumability

### Exactly one runner, and the mechanism is a repaired lease

Three candidates, and two are unavailable rather than merely worse:

- **A database-level lock.** Does not exist. CockroachDB has no `LOCK TABLE` — npgsql/npgsql#6025 records `42601: at or near "lock": syntax error` from Cockroach CCL v24.3.4 for exactly the `LOCK TABLE "__EFMigrationsHistory" IN ACCESS EXCLUSIVE MODE` that Npgsql's `NpgsqlHistoryRepository` issues, which is almost certainly why this repository ships `NoLockHistoryRepository` at all. Worse for anyone reaching for the obvious alternative: v25.3 documents the advisory-lock functions as present with *no-op implementations*, so `pg_advisory_lock` would silently lock nothing rather than error.
- **An Orleans grain.** `Program.cs` is `builder.Build()` → `UseArgonRole()` → `WarmUpRotations()` → `WarmUp<ApplicationDbContext>()` → `RunAsync()`. `RunAsync` is what starts the silo, so at warm-up time no grain is activatable. Moving the reconciler after `RunAsync` to get one means pods serve traffic against an unreconciled schema — trading away the guarantee you wanted for the one you had. And even after boot, Orleans favours availability and documents duplicate activations under partition, with membership in Redis, so the mutex would break for reasons uncorrelated with the database it protects. A grain remains the right home for *scheduling* a periodic pass and for holding the cached verdict the health check reads — never for exclusion.
- **A lease row.** The only mechanism that works identically on Cockroach and PostgreSQL with no engine-specific syntax, and that lives in the same database as the state it protects, so it cannot be reachable while the database is not.

So: a lease. But **not the existing one as written.** `__MigrationLock` has four defects that are tolerable for "one pod applies migrations in the common case" and fatal for a reconcile:

| defect | where | consequence |
|---|---|---|
| no renewal on a fixed 10-minute TTL | `lockTtl = TimeSpan.FromMinutes(10)` | a pass longer than 10 minutes lets a second pod steal the lease and run concurrently |
| release has no owner predicate | `DELETE FROM "__MigrationLock" WHERE id = 1` | a holder whose lease was stolen deletes the *stealer's* row on the way out, admitting a third |
| `expires_at` computed client-side from `DateTime.UtcNow`, compared against server-side `now()` | acquire/steal | clock skew shifts the effective TTL |
| `workerId = Environment.MachineName` | not unique when several roles run as processes on one host (docker-compose, dev) | two "holders" with the same identity |

The reconciler gets its own `__SchemaReconcileLock`, created by raw SQL outside migration history the same way `__MigrationLock` is, with `TEXT` rather than `STRING` so the same bootstrap DDL replays on PostgreSQL:

```sql
CREATE TABLE IF NOT EXISTS "__SchemaReconcileLock" (
    id         INT PRIMARY KEY DEFAULT 1,
    fence      BIGINT      NOT NULL DEFAULT 0,   -- monotonic, incremented on every acquire
    locked_by  TEXT        NOT NULL,
    locked_at  TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);

-- acquire or steal, in one compare-and-swap, with the server's clock on both sides
INSERT INTO "__SchemaReconcileLock" (id, fence, locked_by, locked_at, expires_at)
VALUES (1, 1, $1, now(), now() + $2::INTERVAL)
ON CONFLICT (id) DO UPDATE
   SET fence      = "__SchemaReconcileLock".fence + 1,
       locked_by  = excluded.locked_by,
       locked_at  = now(),
       expires_at = now() + $2::INTERVAL
 WHERE "__SchemaReconcileLock".expires_at < now()
RETURNING fence;

-- heartbeat, every TTL/3, predicated on the fence this holder was given
UPDATE "__SchemaReconcileLock"
   SET expires_at = now() + $2::INTERVAL
 WHERE id = 1 AND locked_by = $1 AND fence = $3;

-- release, likewise predicated
DELETE FROM "__SchemaReconcileLock" WHERE id = 1 AND locked_by = $1 AND fence = $3;
```

TTL 30 seconds with a heartbeat, not 10 minutes without one: a dead holder is detected in seconds and a live one never loses its lease however long the work runs. `locked_by` is `{MachineName}/{RoleId}/{pid}/{bootGuid}`. Every mutating statement is issued only while the holder's own heartbeat has succeeded within the last TTL.

Repairing `__MigrationLock` the same way is a free side benefit of building this and should ship in the same change — it fixes a real hazard on a path that already exists.

*Why the lease is needed at all, given that the statements are idempotent:* Cockroach rejects overlapping multi-region operations. v25.3 docs on both `ADD REGION` and `DROP REGION`: *"while this statement is running, all index modifications and locality changes on `REGIONAL BY ROW` tables will be blocked"*, and symmetrically on `SET LOCALITY`: *"any `ADD REGION` and `DROP REGION` statements on that database will fail."* Two pods reconciling simultaneously make each other fail with confusing errors. And the docs are blunt in general: *"We do not recommend doing more than one schema change at a time while in production."*

### Where it sits in the boot path — and the trap that would make it silently useless

Inside `MigrateArgonDatabase`, after the lock is acquired, **before** this:

```csharp
var pending = (await db.GetPendingMigrationsAsync()).ToList();
if (pending.Count == 0)
{
    logger.LogInformation("No pending migrations.");
    return;      // ← inside the try; runs the finally, skips everything after it
}
```

The obvious placement — "after the migration loop, still inside the try" — is **unreachable in production**. Annotation-only model changes emit no migration operations, so a deployed database has `pending.Count == 0` on every boot, forever. A reconciler placed there would run on fresh databases, where `CREATE TABLE` already carries the `LOCALITY` clause and reconciliation is least needed, and would never run on the one database it exists for. That is the worst failure shape available: green in tests and on every fresh deployment, silent no-op in production.

Either the detect pass goes ahead of the pending check, or that early `return` becomes a flag that skips only the loop. Either way, the reconciler must run on the no-pending-migrations path, because that *is* the path.

Note also what is *not* covered by any lock: `CreateDatabaseAsync` (which on Cockroach emits `CREATE DATABASE … PRIMARY REGION … SURVIVE …`) and `PostgresCompatibilityShims.ApplyAsync` both run before the lease is taken. Those are pre-existing unserialized windows and the reconciler must not join them.

### Resumability, and the failure mode that is the inverse of the obvious one

Every one of these statements is a background job that outlives the session, pausable and cancellable via `SHOW JOBS` / `PAUSE JOB` / `CANCEL JOB`, and *"If a schema change fails, the schema change job will be cleaned up automatically."* So if the runner dies mid-operation the **database** is not left inconsistent: each completed statement is committed and each in-flight job is driven to completion or rollback by the cluster, not by the reconciler. Recovery is "restart and re-derive", which is what the level-triggered shape gives for free.

The subtle part is the opposite of what it looks like. For `ADD REGION`/`DROP REGION` the *session blocks on the job* and receives the job's error — `waitForTxnJobs` is on the commit path in `release-24.3` through `release-25.3`. So:

- **Statement success is proof of convergence.** Good.
- **Statement failure is not proof of non-convergence.** If a `statement_timeout` fires, the client gets `QueryTimeoutError` plus a notice that background jobs *"have been created and will continue running"* — an ERROR while the job may still succeed. Same if the connection drops mid-wait. Argon sets no statement timeout on the database connection and Cockroach's default is 0, so today it waits indefinitely; a long operation blocking a reconciler for minutes is still a real operational hazard.

Therefore: on any error, re-read before deciding anything, and never retry blindly. And record the `job_id` at issue time in an append-only audit table — statement text, job id, actor, lease holder, fence, timestamps, outcome — that is **never read for control flow**, only by humans and dashboards. Before issuing, check for a matching in-flight job and adopt it rather than re-issuing.

A failing ALTER retried every pass is a self-inflicted outage. Exponential backoff plus a poisoned-change latch that requires an operator to clear, and the job's `error` surfaced verbatim.

Two more small rules: a pod already draining does not start a pass (it is about to disappear with the lease); and losing the lease is a *correct outcome*, logged as "another worker is reconciling", never reported as converged.

### On hard-reboot deployment

The owner's point (3) removes mixed-version coexistence from the problem, and this design spends nothing on it — there is no version-negotiation, no "old pod sees a locality it does not know" reconciliation protocol beyond the never-converge-downward rule, which is cheap and worth having anyway. What a hard reboot does *not* remove is crash-at-any-step, which is the normal case and is what §5 is entirely about.

---

## 5b. The placement audit, and the NATS + HLC alternative

### The criterion

`LOCALITY GLOBAL` buys local reads in every region and charges for it on **every write**. v25.3 documentation, verbatim:

> *"Writes incur higher latencies than reads, since they require a 'commit-wait' step to ensure consistency."*
> *"the observed write latency is dependent on the `--max-offset` setting"*
> *"Your application has a 'read-mostly' table of reference data that is rarely updated"*
> *"Cockroach Labs recommends lowering the `--max-offset` setting to `250ms`"* for new multi-region clusters.

`--max-offset` defaults to 500ms. So the honest unit is: a write to a `GLOBAL` table costs on the order of a few hundred milliseconds of *waiting*, in every region, per write. It is a wait and not a lock, so concurrent writes still pipeline and throughput survives; per-operation latency does not, and neither does the number of connections held open while waiting.

The test is therefore not "is this table small" or "is it read from everywhere". It is: **is it written on a user-facing action, or on the message path?** If yes, `GLOBAL` is wrong regardless of how attractive the read side looks.

### The alternative, stated properly

Keep the table `REGIONAL` — homed where the entity that owns it lives — and replicate the *read-side view* over NATS so other regions can render without a WAN read. Argon already has the shape: `NatsRegionIntentChannel` publishes on a subject per region with everyone subscribed to the wildcard, and the cache-invalidation path already does this for real.

**What an HLC buys that a wall clock does not.** `HybridLogicalClock.OnReceive` advances to `max(now, lastLocal, remote)` and increments the logical counter, so two events produced in two regions with no shared clock get a **total order** that every region agrees on, with a `NodeId` tiebreak in `HybridTimestamp.CompareTo`. Concretely that gives a replica three things a wall clock cannot: it can discard an out-of-order delivery instead of applying it; it can decide last-writer-wins deterministically, so two regions converge on the same winner rather than on whoever's message arrived last; and it can hand a client a cursor that means the same thing in every region, which is exactly what a resumable event stream needs across a re-home.

**What it does not buy, and this is the load-bearing half.** An HLC **orders; it does not make anything atomic.** It cannot make two writes in two regions one transaction. It does not prevent a lost update — it only tells you afterwards which write should have won. It cannot answer "did this happen", only "which of these happened later". And it says nothing about *visibility*: a correctly-ordered event that has not arrived yet is still absent. So anything that must be **decided once** — a uniqueness constraint, a quota, a permission gate that protects money or safety — cannot move to an eventually-consistent replica however good the ordering is. For those, the authoritative row stays in one place and the gate reads the authority (or a lease); the replica is a cache that makes the *rendering* fast, not the *decision*.

Where a stale replica is acceptable: channel lists, `LastMessageId`, member-list rendering, display names, avatars, profile fields, invite previews, group ordering. Being 200ms behind is invisible to a human. Where it is not: the entitlement check that admits a message send or a moderation action (a revoked role must not still be honoured elsewhere), the invite `UsedCount` when `MaxUses` is enforced, and username/email uniqueness.

### The audit

Verdicts below are about the **write** side. Read-side needs are handled by NATS replication or by the ordinary regional read, and are noted per row.

| Table (real identifier) | Written by | Write rate | Read by | Declared today | Verdict | Better placement | What the application then owes |
|---|---|---|---|---|---|---|---|
| `Users` | registration; profile edit; `ITotpKeyStore` secret set/clear; `UltimaGrain` `HasActiveUltima` flip; lockdown; `AccountDeletionGrain` | per user lifecycle | every auth, every bootstrap, every member list | GLOBAL | **keep GLOBAL** | GLOBAL | nothing. This is the reference-data shape the feature is for. |
| `UserProfiles` | created with the user; edited by the user | per user lifecycle | profile view, bootstrap | GLOBAL | **keep GLOBAL** | GLOBAL | nothing. |
| `Spaces` | create / rename / settings | per space lifecycle | bootstrap, every routing decision | GLOBAL | **keep GLOBAL** | GLOBAL | nothing. Space metadata being readable everywhere is load-bearing for `ArgonId.NewIn`. |
| `Archetypes` | role create / edit / delete | per moderation action, low | **every permission evaluation** | GLOBAL | **keep GLOBAL, with a caveat** | GLOBAL | nothing today. If role editing ever becomes interactive-frequency, this moves with `MemberArchetypes`. |
| `Channels` | create / rename / move — **and `LastMessageId` on every message sent** (`ChannelGrain.UpdateLastMessageIdAsync`, fired from `SendMessage:846`) | **one write per message in the product** | bootstrap, channel list | GLOBAL | **WRONG — the worst one** | `REGIONAL BY TABLE` in the space's region | Take `LastMessageId` off this table. It is a hot counter on a cold row and it does not belong on the same row as the channel's name. Either a regional side table, or Redis, or publish it over NATS as derived state — it is already fire-and-forget, so it is already not a correctness signal. |
| `UsersToServerRelations` (`SpaceMemberEntity`) | join / leave / soft-delete (`SpaceGrain:171`) | per join — a user-facing action | **every bootstrap, every member list, every permission check** | GLOBAL | **WRONG at scale** | `REGIONAL BY TABLE` in the space's region | The read is the hard part: a user's space list spans regions. Replicate `(userId → spaceId, region)` over NATS as a per-user index — small, derived, HLC-ordered, and tolerant of being 200ms stale because the bootstrap then fetches each space from its own region anyway. Authority for "is this user a member" stays with the space's region. |
| `MemberArchetypes` (`SpaceMemberArchetypeEntity`) | every role grant / revoke (`EntitlementGrain:453`, `SpaceGrain:1015`) | bursty, interactive | **every permission evaluation** | GLOBAL | **WRONG** | `REGIONAL BY TABLE` in the space's region | Replicate for rendering; **do not** replicate for the gate. `InvalidateMemberPermissions` already exists and already publishes — extend that, HLC-stamped, so a stale replica can be detected and refused rather than trusted. A revoked role honoured for 200ms in another region is a security bug, not a latency bug. |
| `ChannelEntitlementOverwrites` | `EntitlementGrain.UpsertArchetypeEntitlementForChannel` / `UpsertMemberEntitlementForChannel` / delete | one write per toggle in a permissions UI | every channel access check | GLOBAL | **WRONG** | `REGIONAL BY TABLE` in the space's region | Same as above, and same refusal: cache for rendering, read authority for the gate. |
| `Invites` (`SpaceInvite`) | create; **`UsedCount` increment on every accepted invite** (`InviteGrain:34`) | per join | invite resolution | GLOBAL | **WRONG** | `REGIONAL BY TABLE` in the space's region | Nothing, and that is the point: the increment is already written as a guarded conditional update (`WHERE MaxUses = 0 OR UsedCount < MaxUses`) — a compare-and-swap that is only correct against **one** authoritative copy. It cannot be replicated. Making it regional is strictly an improvement: it keeps the CAS and drops the commit-wait. |
| `ChannelGroupEntity` | create / rename / **reorder** (`FractionalIndex`, `SpaceGrain:480–520`) | reorder writes several sibling rows in a burst | channel list | GLOBAL | **wrong, mildly** | `REGIONAL BY TABLE` in the space's region | Replicate the ordering for rendering. Reordering is drag-and-drop; a burst of commit-waits is exactly the interaction that feels broken. |
| `Messages` | every send (batched, `MessageWriteBuffer`) | highest in the product | history reads | REGIONAL BY ROW | **correct in intent, expensive to reach** | see §6 | §6. |

**Summary: six of ten are wrong, one is borderline, three are right.** The three that survive — `Users`, `UserProfiles`, `Spaces` — are exactly the ones the documentation describes: reference data, written per lifecycle, read on every request. `Archetypes` survives on the strength of its read side and should be watched.

Note that the repository's own architecture document already reached this conclusion and it was not acted on: `multi-region.md` argues for `REGIONAL BY ROW` on space-scoped tables (*"spaces, channels, members, archetypes, invites"*) and `GLOBAL` only on *"the small, read-everywhere, write-almost-never tables: feature flags, system archetypes, the system user and space, entitlement templates, and the home-region override"*. The placement block in `ApplicationDbContext` and that paragraph disagree, and the block is the one that is wrong.

**Consequence for the design:** the audit is a prerequisite for Tier A, not a follow-up to it. Ship the reconciler in `Report` mode, land the audit in `ArgonTablePlacement`, then enable apply. In that order.

---

## 6. The `Messages` table

### The three findings, stated plainly

**(a) It is not a metadata change — it is a full physical rewrite.** `ALTER TABLE … SET LOCALITY REGIONAL BY ROW` is implemented as an `ALTER PRIMARY KEY` locality swap. Source comment, `release-25.3` `pkg/sql/alter_table_locality.go`: *"We re-use ALTER PRIMARY KEY to do the the work for us."* And `shouldRewriteIndex` short-circuits to `return true` for **every** index whenever a locality swap is in play, before any of the usual "does this index need rewriting" tests run. Argon's `Messages` has PK `(SpaceId, ChannelId, MessageId)` **and** a covering index on `(SpaceId, ChannelId, CreatedAt)` `STORING (Text, Entities)` — roughly a second copy of the payload. So the cost is proportional to primary index bytes *plus* every secondary index's bytes, which here is about twice the naive estimate, and Cockroach documents that `ALTER PRIMARY KEY`-class changes *"may temporarily require up to three times more storage space for the range size"* and that `SET LOCALITY` to/from `REGIONAL BY ROW` is among the schema changes that **pause** if a node runs out of disk. Budget it like rebuilding every index on the largest table in the product.

**(b) The homing of existing rows — the premise in the placement block's own comment is wrong, and the truth is worse in the long run.** Rows are *not* homed to "the gateway region of whoever ran the ALTER". Cockroach sets the implicit column's default to the **primary region literal** so the backfill uses that, and swaps to `default_to_database_primary_region(gateway_region())` only after the backfill completes. Verified, `release-25.3` `pkg/sql/alter_table_locality.go`: *"Note we initially set the default expression to be primary_region, so that it is backfilled this way. When the backfill is complete, we will change this to use gateway_region."*

So the one-shot risk is: **every historical row is stamped with the primary region**, whatever region it was actually written in. Today — one region, primary `ru-central`, `ArgonId.OriginalRegionIndex` — that is *correct*, and it is the only moment at which it will ever be correct for free. From the day a second region exists, the plain ALTER mis-homes 100% of message history, Cockroach has no way to know better, and the documented remedy is manual: make `crdb_region` visible and `UPDATE` the rows.

**(c) New rows are homed by the gateway, and re-homed by any update.** v25.3 docs: *"Each row's home region is specified in a hidden `crdb_region` column, which defaults to the region of the gateway node that inserted the row"*, and *"A row's home region will be automatically set to the gateway region of any `UPDATE` or `UPSERT` statements that write to those rows."* Argon soft-deletes messages with `ExecuteUpdateAsync` (`ChannelGrain:626`), so a delete would silently re-home the row. With today's single `ConnectionStrings:Default` every pod in every region enters through one gateway, so the default hidden column would be **strictly worse than what Argon has now**: no locality benefit at all, plus every non-unique read fanning out N ways across region partitions, plus a permanent region prefix on every key.

### So: is a region column derived from Argon's own UUIDv7 tag viable? Yes, and it is the right answer

Everything lines up:

- `SpaceId` is a region-tagged UUIDv7 minted by `ArgonId`, and `ChannelId` inherits the space's region via `ArgonId.NewIn(spaceId)`.
- The message PK leads with `SpaceId`.
- **Every** read predicate has `SpaceId` as an equality (`PgSqlMessagesLayout.QueryMessages` is `SpaceId == … AND ChannelId == … [AND MessageId < …]`), which is what lets the optimizer constant-fold the computed expression from the parameter and keep the history read a **single-span reverse scan** instead of an N-way `UNION ALL`, one scan per region.
- It homes rows by *where the space was created* — which is exactly what the placement block's comment already claims happens, and which is immune to who ran the ALTER, which gateway inserted the row, and whether a later `UPDATE` touched it.
- v25.3 docs sanction it: *"you can specify any column definition you like for the `REGIONAL BY ROW AS` column, as long as the column is of type `crdb_internal_region` and is not nullable"*, and a `STORED` computed column added to an existing populated table is the documented data-domiciling shape.
- `sql_safe_updates` refuses the *implicit-column* conversion (`if params.p.SessionData().SafeUpdates && !n.tableDesc.Adding()` in `release-25.3`) — which is what a DBA hits running it by hand in `cockroach sql`, where `--safe-updates` defaults to true for interactive sessions. The `REGIONAL BY ROW AS <column>` path is not affected. One more structural reason to prefer it.

The expression must reproduce `ArgonId.RegionIndexOf` exactly: version nibble is 7; timestamp (first 48 bits) below the epoch ⇒ original region; otherwise `tag = rand_a`, and `tag == 0 ⇒ original`, else `tag - 1`. In UUID text form, characters 1–8 and 10–13 are the 48-bit timestamp, character 15 is the version nibble, and characters 16–18 are `rand_a` (`tag = regionIndex + 1`, so `001` is index 0, `002` index 1, `003` index 2):

```sql
ALTER TABLE "Messages" ADD COLUMN "crdb_region" crdb_internal_region NOT VISIBLE
  AS (CASE
        -- not a v7: no timestamp to judge, so it predates tagging by construction
        WHEN substring("SpaceId"::STRING FROM 15 FOR 1) <> '7'          THEN 'ru-central'
        -- pre-epoch: made when there was one region, whatever the bits say
        WHEN substring("SpaceId"::STRING FROM 1 FOR 8)
          || substring("SpaceId"::STRING FROM 10 FOR 4) < '019b76daa800' THEN 'ru-central'
        WHEN substring("SpaceId"::STRING FROM 16 FOR 3) = '002'          THEN 'eu-central'
        WHEN substring("SpaceId"::STRING FROM 16 FOR 3) = '003'          THEN 'us-east'
        ELSE 'ru-central'
      END) STORED;

ALTER TABLE "Messages" ALTER COLUMN "crdb_region" SET NOT NULL;
ALTER TABLE "Messages" SET LOCALITY REGIONAL BY ROW AS "crdb_region";
```

The `WHEN` arms are **generated from the database's own region set** (`SHOW CREATE DATABASE`, cross-checked against the index ledger below), never from a static list. That gives a free safety rail: a `CASE` yielding a region the enum lacks fails at `ADD COLUMN` with `22P02 invalid input value for enum crdb_internal_region`, so an unreachable region cannot be baked in.

**The one honest divergence from the C#, and it must be written down.** For a post-epoch id whose tag names a region the database does not have, `ArgonId.RegionIndexOf` returns `tag - 1` and `ArgonRegionRegistry` throws `UnroutableIdException`; SQL cannot throw inside a stored column, so the `ELSE` folds to the primary region. The rule is therefore: **the column agrees with the C# wherever the C# answers, and answers "primary region" wherever the C# throws.** Pin it with a test that mints an id for every configured index plus a pre-epoch id plus a v4 and asserts both readers agree on all of them, and that documents the `ELSE` as the intended disagreement.

### The costs of the computed path, stated honestly

- **Two passes over the table, not one.** `ADD COLUMN … STORED` is its own schema-change job with its own backfill and its own GC job; `SET LOCALITY` is then the primary-key swap and index rewrite. The default hidden column fuses them and pays one. Roughly double the disk churn and wall time, and that is the price of correct homing.
- **It is close to one-way.** `ALTER TABLE … ALTER COLUMN … SET EXPRESSION` does not exist in v25.3. Changing the formula means: add a second computed column, atomically rename-swap, re-issue `SET LOCALITY REGIONAL BY ROW AS`, drop the old — and pointing `SET LOCALITY` at a different column changes the locality config and puts `shouldCreateIndexes` back on the `return true` path, i.e. **another full index rewrite**. So `Argon:Regions:IdEpoch` and the index↔name mapping must be final before conversion, and every future region added to the `CASE` costs another full rewrite of the biggest table — unless the column is later replaced by a real, application-written `crdb_internal_region` column, which trades the rewrite-per-region for application-side work on every insert path.
- **Rolling back is a second full rewrite**, and the column survives it as an ordinary `NOT VISIBLE NOT NULL` column. There is no cheap undo. Decide before starting.
- **PostgreSQL.** The column must *not* become an EF-modelled property, or it materialises on PostgreSQL where `crdb_internal_region` does not exist. It is created and maintained entirely outside EF's model by the reconciler, is `NOT VISIBLE` so `SELECT *` does not surface it, and no entity, migration or snapshot needs to know it exists. Verified that Argon has no bulk `COPY` path and no FK on `Messages`, and the only `FromSqlRaw("SELECT * …")` in the codebase targets `FileCounters`.

### Therefore: do not convert `Messages` now

The staged plan, and the reconciler's refusal:

1. **Now.** Declare `Messages` as `REGIONAL BY TABLE IN PRIMARY REGION` — its actual current physical state, made explicit and reconciled. Free, reversible, and it makes the declaration honest for the first time.
2. **Prerequisite, config only.** Per-region `Database:ConnectionString` in each region's `conf.d` bundle. Each region already ships its own bundle (`deploy/dev/conf.d/database.json` sets the key), so this is a deployment change with no code change — but nothing enforces or checks it today, and it must be checked.
3. **Prerequisite.** `Argon:Regions:IdEpoch` set and past; the index↔name ledger (§9) written; the region added to the database.
4. **Prerequisite, measured.** `SHOW RANGES FROM TABLE "Messages" WITH DETAILS, KEYS, INDEXES` on every node, and 3× the total in free disk on each. `gc.ttlseconds` known, because pre-conversion index copies occupy disk until it elapses, long after the job reports success.
5. **Then, and only then:** an operator-triggered Job, in an off-peak window, running the three statements above, watching `fraction_completed` in `SHOW JOBS` and p99 message-send latency.

**The reconciler will never issue step 5 on its own, in any mode, with any flag.** It detects the divergence, reports it, prints the exact SQL, and stops. A pod restart must never be able to start a re-partition of the largest table in the product.

A note on the staged path that is worth knowing before someone proposes it as an optimisation: `REGIONAL BY TABLE → GLOBAL → REGIONAL BY TABLE` transitions are genuinely free (descriptor + zone config, no backfill), but they buy **no discount** on the eventual `REGIONAL BY ROW` conversion, because `shouldRewriteIndex` triggers on any locality-config change regardless of the starting locality. Stage it because staging is safe and defers risk, not because it makes the expensive step cheaper.

---

## 7. Observability, dry-run, and drift reporting

### It is invisible to every probe, and this is a hard rule

Not `startup`, not `liveness`, not `readiness`. Three reasons, two of them specific to this codebase:

- Readiness answers *"should traffic come here"*, and a background schema change does not change the answer. `ReadinessHealthCheck` fails only on drain state and Orleans status, and its own comment makes exactly this argument for `OrleansClusterHealthCheck`.
- `deploy/k8s-probes.md` specifies `failureThreshold: 1` on silo readiness. Every pod runs the same converger, so if it made readiness false, **every pod would leave the Service at the same instant.**
- Liveness is worse still: its only remedy is a restart, and a Cockroach schema change runs in the cluster and survives the restart. You would lose the observer and keep the work.

The right surface already exists and needs no new plumbing:

```csharp
.AddCheck<SchemaReconcileHealthCheck>(
    "schema-placement",
    failureStatus: HealthStatus.Degraded,
    tags: ["diagnostic", "schema", "placement"]);
```

Checks tagged `diagnostic` never run on a probe, because `MapProbeEndpoints` filters on `startup`/`liveness`/`readiness`. And the detailed `/health` endpoint's `WriteHealthResponse` already refuses to emit per-check `data` dictionaries to anything but a loopback caller, so the full drift list can go in `data` safely. Exactly the pattern `OrleansClusterHealthCheck` established.

The check reads a **cached verdict** from a singleton and never queries. Otherwise a loopback scrape becomes a `SHOW CREATE TABLE` storm.

Verdicts:

| verdict | meaning |
|---|---|
| `Healthy` | catalog matches the declaration, no in-flight job |
| `Degraded` | drift exists, or a job is in flight, or an item is awaiting operator approval, or an item is refused, **or the state could not be determined** |
| `Unhealthy` | never |

"Could not determine" must render distinctly from "converged". It is the failure that looks like success.

### Metrics

On `Instruments.Meter` (`new Meter("Argon")`), under the documented `argon-{feature}-{metric}` kebab-case convention:

| instrument | type | labels |
|---|---|---|
| `argon-schema-drift-items` | ObservableGauge | `table`, `tier` |
| `argon-schema-reconcile-passes` | Counter | `outcome` ∈ converged / applied / skipped-lock / adopted / refused / undetermined / failed |
| `argon-schema-reconcile-duration` | Histogram | — |
| `argon-schema-job-fraction` | ObservableGauge | `job_id`, `table` — scraped from `SHOW JOBS` |
| `argon-schema-lease-age` | ObservableGauge | — |

Alert on `outcome=refused` or `tier=approval` persisting past a deploy window, on `outcome=undetermined` at all, and on `argon-schema-lease-age` exceeding the TTL (a holder died mid-change). **Never alert on `drift > 0` alone** — drift during a rollout is normal and an alert that fires on every deploy is an alert that gets muted.

### Logging

One `Information` line per pass, and only when the diff is non-empty or the verdict changed (`Debug` otherwise). One `Warning` per *issued* statement carrying the full SQL, the returned `job_id`, the actor (boot / CLI / Job), the lease holder and fence, and the observed region set. One line per refusal naming the item and the tier and printing the SQL a human would run.

The precedent is `ClusterClientStatus`, which logs the transition once *"because it runs every few seconds and logging its verdict would turn a long outage into a long log saying the same thing."* A refusal that is not logged is indistinguishable from a reconciler that did not notice, and that is the difference between "the tool protected us" and "the tool was broken" at the incident review.

### Dry-run — same code, different argv

```
argon --schema-plan     # read-only; prints the ordered statements with a tier per statement; exit 0/1
argon --schema-apply    # executes Tier A and Tier B; refuses Tier C; exit 0/1
```

Both go through `ArgonClusterCli.TryHandleCommand`, which already runs before the host is built and already returns a process exit code, and which already assembles configuration exactly as the host would via `FeatureConfigurationProbe.Build(role)` for `--validate-config`. So the dry-run surface and the Kubernetes Job entry point are the same thing: same image, different argv, no second artifact to drift.

**The plan function must be the same function `TablePlacementTests` calls.** If the acceptance test and the production planner are separate code, a green test and a wrong plan are perfectly compatible — which is exactly the failure this whole exercise exists to avoid.

### Default posture

`Database:Reconcile:Mode = Off | Report | Apply`, defaulting to **`Report`**. The first release ships a reconciler that can only tell you things, and the production evidence that the diff is correct is gathered before anything is allowed to act on it.

### Tests

No container needed (`ArgonSharedLogicTest`, ~11s, already references `Argon.Api`):

- desired-state computation over the **real** `ApplicationDbContext` model — the audited GLOBAL set by table name, so renaming a table out from under `ArgonTablePlacement` goes red with a name attached;
- TPH / table-sharing: two entity types on one table produce one desired row, and a genuine conflict throws;
- normalisation: observed `REGIONAL BY TABLE IN PRIMARY REGION` compares equal to undeclared and to declared `REGIONAL BY TABLE`; region-name quoting and case round-trip;
- statement text, including mixed-case identifier quoting;
- plan safety: the plan contains only the statements the tier allows, only for declared tables, and is **empty when declared == observed** — idempotency proven with no database anywhere near it;
- guard: a plan computed for `DatabaseProviderKind.PostgreSql`, or with an empty primary region, is empty;
- the `crdb_region` expression against `ArgonId.RegionIndexOf` for every configured index, a pre-epoch id, and a v4.

Needs the container (`ArgonComplexTest`, `ARGON_TEST_DB=Cockroach`): that a real server accepts the ALTERs and afterwards reports the declared placement. `TablePlacementTests` is already that test and is already red for the right reason. Two additions: run the reconciler twice and assert the second plan is empty; and on the default `ARGON_TEST_DB=Postgres` assert *positively* that nothing was attempted, rather than `Assume.That`-skipping.

---

## 8. What this replaces

### The migration squash: yes, it is removed

The squash existed for one purpose — to make `CREATE TABLE … LOCALITY` reach a database that already exists, by resetting `__EFMigrationsHistory` against a live schema. The reconciler reaches the same end state with `ALTER`, and it is better on four axes that all matter for a production database:

| | squash | reconciler |
|---|---|---|
| repeatable | no, one-way | yes, every boot |
| verifiable before running | no | yes — `--schema-plan` prints the statements |
| touches migration history | yes, resets it | no |
| partial failure | leaves history and schema disagreeing | leaves a committed prefix and re-derives |

`TablePlacementTests` was written as the acceptance criteria for the squash — its own comment says *"Run them the day the migrations are squashed and they should go green without any other change."* They become the acceptance criteria for the reconciler, unchanged, and the file's guidance about right-reason vs wrong-reason red stays exactly as valid.

### What remains of `DbLocalityExtensions`' `CreateTable` override

Two sources of truth for placement would be a defect, so the override **stays and stops being a source**. Both sides read the same `Regional:Locality` annotation; the generator renders it into `CREATE TABLE`, the reconciler renders it into `ALTER TABLE`, and both call the same `TablePlacement → clause` function. One declaration, two renderers.

The override is still the only correct thing for a database created from scratch — and it must remain so, because `CREATE TABLE … LOCALITY` in one statement is strictly better than create-then-alter, which would rewrite indexes on a table that did not need to exist in the wrong shape first.

Three changes to it, in the same work:

1. **Fix the model-source asymmetry, which is the actual root cause and is one line.** In the `CreateTableOperation` override, `Job:Expiration` is looked up in `model ?? Dependencies.CurrentContext.Context.Model` but `Regional:Locality` is looked up in bare `model`. `MigrateArgonDatabase` always passes `migration.TargetModel` — the design-time snapshot, non-null — so the `??` never fires for either, and the snapshot has no `Regional:Locality`. By contrast `NpgsqlCreateDatabaseOperation` is generated with `model == null`, so its `??` *does* fall through to the live model, which is precisely why `CREATE DATABASE … PRIMARY REGION` works and `CREATE TABLE … LOCALITY` never has. Giving the entity-level lookup the same fallback makes every **new** database correct immediately, with no migration and no squash. It does nothing for the existing production database — which is what the reconciler is for. The two are complementary, not alternatives, and shipping only one of them leaves half the problem.
2. **Replace `FirstOrDefault` with a grouped lookup that refuses a conflict**, for the TPH reason in §1.
3. **Make `Regional:Locality` structured**, per §1.

### What stops being consulted

- `Regional:MultiRegion` keeps its `CREATE DATABASE` job and stops being a reconciliation target. Its `Regions` field stores `additionalRegions` verbatim and can contain the primary (today's shipped config produces `PRIMARY REGION "ru-central" REGIONS "ru-central"`), which is harmless at creation and would be a source of phantom diffs if reconciled.
- `Database:Regions:ReplicateRegion` — the JSON region list — stops being read. Regions are observed; adding one is an operator command.
- `Database:Regions:PrimaryRegion` becomes an assertion checked against `SHOW CREATE DATABASE`, not a source.
- The `Regional:MultiRegion` payloads frozen in every `.Designer.cs` are inert and stay inert.

---

## 9. Migration path for the database that exists in production today

Assumed starting state, per the brief: no regions configured, no `LOCALITY` on any table. Steps 0–3 change nothing.

**Step 0 — preflight, read-only, run once by a human.** Record all of it somewhere tracked:

```sql
SELECT version();                                  -- the FULL patch level, not the major
SHOW CREATE DATABASE "argon";                      -- is it multi-region already?
SHOW ZONE CONFIGURATION FROM DATABASE "argon";     -- a manually-set zone config BLOCKS
                                                   -- SET PRIMARY REGION outright, and must be
                                                   -- removed with CONFIGURE ZONE DISCARD first
SELECT owner FROM crdb_internal.databases WHERE name = current_database();  -- one-off, by a DBA
SHOW GRANTS ON DATABASE "argon";                   -- does the app role own it / hold CREATE?
SHOW LOCALITY;                                     -- do the nodes carry --locality at all?
SHOW REGIONS FROM CLUSTER;                         -- admin; what regions exist
```

The patch level matters, not just the major: the governing `sql_safe_updates`/`REGIONAL BY ROW` guard landed in a patch release. If `SHOW LOCALITY` is empty, every step below is a designed no-op and the correct action is to fix the node flags first.

**Step 1 — deploy the reconciler in `Report` mode.** Ship it. Change nothing. Read the drift list off `/health` on a pod (loopback). This converts an invisible eleven-table divergence into a startup line and a metric, and it is worth shipping even if nothing is ever applied.

**Step 2 — land the placement audit** from §5b in `ArgonTablePlacement`, and take `LastMessageId` off the `Channels` row. This is a code change, reviewed normally, and it must precede any apply path. Re-read the report; the drift list is now the *right* drift list.

**Step 3 — set the region ledger.** One row per region, written by an operator, append-only, created outside migration history like `__MigrationLock`:

```sql
CREATE TABLE IF NOT EXISTS "__ArgonRegionIndex" (
    name       TEXT PRIMARY KEY,   -- the CockroachDB region name, e.g. 'ru-central'
    idx        INT  NOT NULL UNIQUE,
    added_at   TIMESTAMPTZ NOT NULL DEFAULT now(),
    retired_at TIMESTAMPTZ NULL     -- retired, never deleted: an index is never reused
);
INSERT INTO "__ArgonRegionIndex" (name, idx) VALUES ('ru-central', 0)
ON CONFLICT (name) DO NOTHING;
```

This replaces `Argon:Regions:Nodes:<name>:Index` as the authority for the region tag, and it replaces the *"paper"* ledger `region-lifecycle.md` currently relies on — whose named failure mode is *"Validation cannot see history"* and *"Nothing detects reuse."* A table can. `TEXT` not `STRING` so the same DDL replays on PostgreSQL, where it is simply unused.

**Step 4 — maintenance window, operator-run Job, Tier B.** This is the expensive one:

```sql
ALTER DATABASE "argon" SET PRIMARY REGION "ru-central";
```

It converts every table in the database to `REGIONAL BY TABLE` in the primary region and moves all voting replicas and leaseholders there. Expect elevated CPU, IOPS and network for as long as rebalancing takes, with **no completion signal** — zone-config-driven data movement is continuous background work by the replication layer and does not appear in `SHOW JOBS`. Do not build a health check that waits for it.

**Step 5 — verify.** `SHOW CREATE DATABASE "argon"` reports `PRIMARY REGION "ru-central" REGIONS = "ru-central" SURVIVE ZONE FAILURE`. `argon --schema-plan` now prints the per-table statements and nothing else.

**Step 6 — switch to `Apply`.** The database has exactly one region, so Tier A is live: ten-ish `ALTER TABLE … SET LOCALITY …`, metadata plus zone config, seconds each, on the boot path under the lease. Re-run `--schema-plan`: **it must print an empty plan.** If it does not, the normalisation is wrong and the mode goes back to `Report`.

**Step 7 — the acceptance test.** `TablePlacementTests` on the Cockroach lane goes green, for the right reason. The declared placement is now real for the first time.

**Step 8 — `Messages` stays put.** Declared and reconciled to `REGIONAL BY TABLE IN PRIMARY REGION`. Everything about converting it is §6 and belongs to a later, separately-approved operation.

**Rollback.** After step 4, going back means dropping the last region, which is gated by `sql.multiregion.drop_primary_region.enabled` (defaults true) and costs another full repartition of every regional-by-row table. It is reversible in principle; treat it as one-way for planning.

---

## 10. Risks and open questions

### Cannot be answered from this repository

- **What CockroachDB version production runs.** `deploy/pconf.d/*` and `appsettings.Production.json` are gitignored and were not read; no Kubernetes or Helm manifest is tracked (`git ls-files deploy/` shows only compose files and `k8s-probes.md`). Every behavioural claim here is stated against **v25.3 documentation and `release-25.3` source**. The integration suite defaults to `cockroachdb/cockroach:latest-v24.3` — a *floating* tag, not a pin, overridable via `ARGON_TEST_DB_IMAGE` — and `deploy/docker-compose.local.yml` pins `v25.3.3` on a three-node local rig. **Neither is evidence about production.** Run `SELECT version()` and record the full patch level before signing anything off. The `sql_safe_updates` guard on ALTER-to-`REGIONAL BY ROW` landed in a patch release, so the major alone is not enough.
- **A version-support caveat worth raising with the owner separately:** v25.3 is an Innovation release and, on Cockroach Labs' published schedule, its maintenance support ended in February 2026; v24.3's GA-patch assistance ended May 2026. Both versions visible in this repository are out of support as of today. Whatever production runs should be a currently-supported Regular/LTS line, and both the test container and the local compose should be pinned to it.
- **The CI coverage gap this creates.** The Cockroach lane is the only place the Cockroach-specific DDL is exercised at all, and it runs the *older* major than local dev. A reconciler validated in CI is validated against an engine that lacks the guard it will actually meet.
- **Whether the app role owns the `argon` database.** Tier A needs owner-or-`CREATE` on each table; Tier B needs owner-or-`CREATE` on the database, and `ADD REGION` additionally needs it on every `REGIONAL BY ROW` table (the privilege check fires *before* the `IF NOT EXISTS` short-circuit). `WarmUpExtensions` creates the database itself via `IRelationalDatabaseCreator.CreateAsync()`, which **suggests** the connecting role is the owner — but the credential is Vault-rotated and conventionally least-privilege, and *suggests is not knows*. If it is not the owner, the design splits cleanly: detection and reporting under the app role, apply under an operator credential. Detect-and-report is worth shipping either way.
- **Whether `SHOW REGIONS` is genuinely admin-gated on the production version.** Docs say yes; `release-25.3`'s `Regions` RPC has no privilege check where its neighbours do. **The design is deliberately indifferent** — nothing automatic reads it — but the operator path should catch 42501 and say so rather than reporting an empty region set.
- **Whether a manually-applied zone configuration exists on the production database.** It blocks `SET PRIMARY REGION` outright and step 0 checks it. If one exists, `CONFIGURE ZONE DISCARD` must fully complete first.
- **Whether production sits behind a connection pooler.** None appears in any tracked deploy manifest and `AddPooledDatabase` points Npgsql straight at the connection string. The recommended engine probe is pooler-safe by construction (a backend function call, not a `SHOW`), but if a pooler in transaction-pooling mode exists, `PostgresParameters` could be missing `crdb_version` and the reconciler must not assume session state survives between statements. It does not — every statement here is independent by design.
- **Whether production is self-hosted Cockroach or Cockroach Cloud.** On Cloud, region management is a console/API operation and the SQL path may be unavailable to the application role, which would move Tier B out of Argon entirely and make everything database-level report-only.

### Measured nowhere, and load-bearing

- **The size of `Messages`, per index.** `SHOW RANGES FROM TABLE "Messages" WITH DETAILS, KEYS, INDEXES` gives `range_size_mb`. Without it, the 3× transient-disk requirement cannot be checked and nobody can say whether the declared `REGIONAL BY ROW` placement is achievable on the current cluster at all.
- **`gc.ttlseconds` on this database.** It decides how long pre-conversion copies of every index occupy disk after the job reports success.
- **The actual commit-wait cost on this cluster.** Documented as dependent on `--max-offset` (default 500ms; 250ms recommended). The audit's *direction* does not depend on the number — a per-message write to a `GLOBAL` table is wrong at any of these values — but its *urgency* does, and the number is worth measuring before the audit is argued about.
- **Whether `SET LOCALITY GLOBAL` on a single-region database is genuinely metadata-only at Argon's data volume.** Source reading says descriptor + zone config with no backfill, and that is the load-bearing assumption behind Tier A. Rehearse it against a restored snapshot before enabling `Apply`, not after.

### Known unknowns in the mechanism

- **`schema_locked`.** Could not confirm the v25.3 default, nor whether auto-unlock (`sql.schema.auto_unlock.enabled`) covers `SET LOCALITY`. The v25.3 `ALTER TABLE` docs carry a worked example where a schema-locked table must be unlocked by hand. The preflight already reads `SHOW CREATE TABLE` for every declared table, so check for the parameter there and refuse with a clear message rather than emitting a statement that bounces.
- **Concurrent `CREATE TABLE IF NOT EXISTS` from N pods.** Not verified from a primary source whether CockroachDB raises a descriptor/namespace error rather than silently no-opping. It affects the lock table's own bootstrap under a simultaneous N-pod rollout, which is exactly what a hard reboot produces. Worth ten minutes in the test container.
- **`plan_cache_mode`.** The single-span read plan for the computed-column design depends on a *custom* plan. Forcing `force_generic_plan` degrades the same prepared query to a lookup join that decodes the whole channel. The v25.3 default is `auto`, which keeps the custom plan — but if a future default flips, Argon's message-history read silently becomes a full-channel scan. Worth a startup assertion or a regression test, not a comment.
- **The computed-column `ELSE` divergence** from `ArgonId.RegionIndexOf`, described in §6. Unavoidable inside a stored column; must be pinned by a test rather than remembered.

### The HLC detail, folded in

`HybridTimestamp.CompareTo` gives a total order with a `NodeId` tiebreak, but `ToString()` emits `{PhysicalMillis}:{LogicalCounter}:{NodeId}` with **no zero padding**, so lexical string order does not match `CompareTo` — `"…:9:node"` sorts after `"…:10:node"`. Harmless in a log line. A trap the moment it becomes a key or a cursor, which is precisely what the NATS-replicated read-model in §5b would want: a replication cursor shipped as that string sorts wrongly the first time a logical counter rolls from 9 to 10, and the symptom is a replica that silently skips or replays a window of events. Fix it before it becomes a key — zero-pad the physical component to 13 digits and the counter to a fixed width, or ship the three fields rather than the string. Do not fix it afterwards, because by then the padding change is itself a cursor-format migration.

### Two questions the design does not settle

- **Is message content residency-bound?** `multi-region.md` offers three answers (follow the space / follow the author / split id+metadata from content) and notes it is a legal question, not an engineering one. Option 2 or 3 puts a different column in the region expression, and the expression cannot be changed later without another full rewrite of the table. This has to be answered before step 5 of §6, not during it.
- **Should the reconciler also own the `Job:Expiration` (row-level TTL) annotations?** They have the identical only-emitted-in-`CreateTableOperation` defect, are declared on three entities, and `ALTER TABLE … SET (ttl = 'on', …)` is comparatively cheap with a much smaller blast radius. Same machinery, and arguably the right *first* change to prove the loop on before touching placement at all.