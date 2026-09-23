# Argon server — instructions for agents

## Ion services never touch the main database (mandatory)

An Ion service — anything registered with `x.AddService<IContract, Impl>(...)` (see
`src/Argon.Api/Clustering/Features/IonFeatures.cs`), its Ion interceptors, and every helper or
"service" class it calls — **must not read or write `ApplicationDbContext`**, the main CockroachDB
database. Not through `IDbContextFactory<ApplicationDbContext>`, not through a scoped
`ApplicationDbContext`, and not through a repository that wraps one. Database access lives in
grains; the Ion layer maps contracts, checks the caller, writes the audit line and calls grains.

How to do it instead:

- Put the query in the grain that owns the data. If nothing owns it (a console, a directory lookup),
  use a `[StatelessWorker]` grain keyed with `Guid.Empty`, like `IDevTeamsGrain`,
  `IIdentityDirectoryGrain` or the `IAdmin*Grain` family behind the operator console.
- Return the Ion contract types straight from the grain. They cross the grain boundary as they are
  (the Orleans serializer carries the Ion converters), so there is no need for a parallel set of
  records.
- Ambient request context (`ArgonRequestContext`, `OperatorRequestContext`) **does not cross a grain
  call**. Pass the caller's id to the grain as an argument, and let the grain do the permission check
  next to the write it guards.
- Cache invalidation that belongs to a write (lockdown, operator app access) goes in the grain,
  beside the write.

What guards it:

- No client role — `entrypoint`, `botapi`, `admin`, `account`, `aegis` — runs `DatabaseFeature`. Don't
  add it back, and don't add `Requires<DatabaseFeature>()` to a feature those roles take: requirements are
  pulled in transitively, so a feature that needs the database gives the database to every role that
  hosts it. That is exactly how `admin` kept a connection pool through `EmailJournalFeature`.
  `ProductionTopologyTests.The_consoles_and_the_identity_server_carry_no_database` (unit, no
  containers) and `RoleStartupTests.The_*_without_a_database` fail when the pool comes back.
- The one sanctioned connection on a client role is `AegisKeyRingDbContext` on `aegis`, a context
  that maps the data-protection key ring and nothing else. It is not the main database.

## Banned APIs (enforced by the build)

`ExecuteSql*`, `SqlQuery*` and `FromSql*` are banned in `src/` (`src/BannedSymbols.txt`, analyzer
RS0030, severity error in `.editorconfig`): raw SQL does not follow a renamed property. Use
`ExecuteUpdateAsync(s => s.SetProperty(x => x.N, x => x.N + d))`, `ExecuteDeleteAsync`, LINQ, and
tracked `Attach` + one `SaveChanges` for batches. SQL that LINQ cannot express (system catalogs,
`AS OF SYSTEM TIME`) goes into a typed helper in `src/Argon.Core/Features/EF/EngineSpecificQueries.cs`
— that folder and `Migrations/` are the only places the rule is switched off.

The same file bans legacy .NET Framework APIs and footguns: `BinaryFormatter` and friends,
`System.Timers.Timer`/`System.Threading.Timer` (use `PeriodicTimer` or grain timers), `WebClient`/
`WebRequest`, non-generic collections, `*CryptoServiceProvider`/`*Managed`, `DateTime.Now`/
`DateTimeOffset.Now` (use `UtcNow`), sync-over-async `.Result`/`.Wait()`, `GC.Collect`. Each error
says what to use instead. Add to the list rather than suppressing a hit.

## A new integration fixture must be added to the shard partition (mandatory)

`tests/ArgonComplexTest` is run in parallel shards, and the fixture-to-shard partition is a literal
in `scipts/test-shards.ps1`, not something discovered at run time. `run-tests.ps1` verifies it before
every sharded run and **fails the whole job** when a `[TestFixture]` in the tree is in no shard —
CI then produces no test results at all, in every shard, which reads as "everything is broken"
rather than "one list is out of date":

```
The shard partition and tests/ArgonComplexTest disagree — in the tree
and in no shard: InviteCardEndpointTests. Fix scipts/test-shards.ps1
```

So, in the same change that adds, renames or deletes a fixture:

- Add its class name to `$generalShards` in `scipts/test-shards.ps1` — to the shard with the smaller
  measured total, which is written in the comment above each list.
- A `[NonParallelizable]` fixture, or one that pins ports or changes the schema, goes in
  `$topologyFixtures` instead; a `Presence*` fixture goes in `$presenceFixtures`.
- A fixture that is deliberately never run (every test `[Explicit]`) goes in `$excludedFixtures`
  **with its reason** — the check counts it as accounted for, not as unassigned.
- Then confirm it locally, which needs no containers and takes a second:

  ```
  ./scipts/test-shards.ps1 -Verify
  ```

  It prints the fixture counts per shard and exits non-zero on an unassigned, doubly-assigned or
  vanished fixture. After a run whose timings have shifted materially, re-balance instead of
  hand-editing: `./scipts/test-shards.ps1 -FromTrx artifacts/test-results -Shards 4` prints a fresh
  `$generalShards` literal to paste over the old one.
