# Multi-region: where it stands

Paused deliberately, not abandoned. This records what is built and proven, what is open, and what
blocks what, so picking it back up does not start with archaeology.

Everything below was verified by running it, unless it says otherwise.

---

## Built and proven

**Region identity in identifiers.** `ArgonId` writes a region index into the UUIDv7 `rand_a` field, with
an epoch cutover so nothing in the existing database has to be migrated: an identifier older than the
epoch belongs to the original region, which is what it was. `ArgonId.New()` is the mint for anything
persisted, and an EF value generator on `ArgonEntity.Id` makes that the default rather than a habit —
covering all 62 tables instead of the eight that were found missing it by hand.

**Registry of cluster clients.** One `IClusterClient` per region, supervised, built over a child
container so a peer that will not connect cannot take the process down. A region that is not usable is
refused rather than handed out — the old path handed out a client that accepted calls and let them time
out. Proven against two real Orleans clusters in `RegionRegistryClusterTests`.

**Routing by identifier.** `ServiceEx.GetGrain<T>(Guid)` is the one place every Ion call turns an id into
a grain. A foreign id is resolved through the owning region's client; an owner that cannot take the call
throws rather than falling back to the local cluster, because falling back is the silent failure the
whole thing exists to prevent — it writes rows into the wrong region's database and looks healthy.
The local path costs two static reads and no container lookup.

**Table placement and row-level TTL reaching the database.** Both were declared in the model for months
and had never reached any table: EF emits those clauses only inside `CREATE TABLE`, and an annotation
change on an existing table produces no migration operation at all. A post-migration step now reads the
declarations from the live model, probes the engine, and issues the `ALTER`s. Placement is gated on the
database having two or more regions — below that `LOCALITY GLOBAL` charges a commit-wait on every write
and repays it only in cross-region reads, which is a pure regression.

**The placement audit.** Nine tables global, three regional. The first audit was wrong on five of six
and was redone against write frequency per row rather than "is a human involved" — the criterion that
had demoted `Channels` for one hot column instead of moving the column.

**A local multi-region stand.** `ARGON_TEST_CRDB_TOPOLOGY` shapes the CockroachDB fixture as
`<regions>x<nodes per region>`; `3x1` by default, `2x3` for the shape a two-region deployment really has.
`ClusterTopologyTests` asserts the cluster is what was asked for, because a fixture that quietly stood up
one region would make the placement assertions pass for the wrong reason.

---

## Open, in the order that matters

### 1. Realtime does not cross a region boundary — this blocks launch

The SignalR backplane lives in its region's Redis, and the region is now part of its channel prefix, so
the isolation is explicit. Nothing bridges it. A user connected in region A who belongs to a space homed
in region B receives no events at all: no messages, no typing, no voice joins.

Call routing is the other half and is done — a request reaches the right region and writes to the right
database. Nothing comes back to the sockets of the region the caller is connected to.

This was attempted once, rejected, and never redone. What it needs: local backplane traffic republished
onto NATS subjects, re-injected into the receiving region's backplane, and an origin tag on the envelope
so a re-injected event does not loop back out. The tag can ride as a NATS header rather than inside the
Ion union, which keeps the whole of it server-side.

It also needs the region NATS servers gatewayed into a supercluster. That is a deployment change and no
code: the publisher, the subscriber, the wildcard subscription and the repeat announcer are all wired,
and what they deliver today is convergence *within* a region.

### 2. Message rows all live in the primary region — this breaks residency

`Messages` is still `REGIONAL BY TABLE`, because converting an existing populated table to
`REGIONAL BY ROW` is refused by the apply step in every mode. That refusal is correct: the conversion
adds a hidden `crdb_region` column into the primary key, repartitions the table and every index, backfills,
and homes every existing row to the **primary region literal** — not to the gateway that inserted it.

The staged conversion is worked out: a computed column derived from the region index already inside our
own UUIDv7, so rows are homed by where the space was created rather than by who ran the `ALTER`. Every
read predicate has `SpaceId` as an equality, which is what keeps history a single reverse scan instead of
one scan per region. Before it can run, `Argon:Regions:IdEpoch` and the index-to-name mapping must be
final: `ALTER COLUMN … SET EXPRESSION` does not exist, so changing the formula later costs another full
rewrite of the largest table in the product.

`TablePlacementTests.Messages_are_regional_by_row` is red on purpose and is the acceptance criterion.

### 3. Presence lies rather than degrades

`GetAggregatedStatusAsync` returns `Offline` when the Redis key is absent. With a second region every
member connected elsewhere renders as confidently offline — not unknown, not stale, wrong, and
undetectable from the outside.

Two halves, and they should ship in this order: replicating presence transitions between regions is
server-only work; a `UserStatus.Unknown` in the contract is a coordinated client release.

---

## Blocked on nothing but a decision

**RTT emulation for the local stand.** The recipe is verified: `netem` works in this kernel, the
CockroachDB image has no `tc`, and `alpine` + `apk add iproute2` in a sidecar sharing the node's network
namespace with `NET_ADMIN` does. What it needs is the delay filtered to the peer region's addresses —
without that it also delays the suite's own connection through the published port and every
intra-region packet, which makes the run slower and proves nothing.

The payoff is a number: the cost of a write to a `LOCALITY GLOBAL` table is currently taken from
CockroachDB's documentation and has never been measured on our hardware.

---

## Loose ends that are not multi-region but were found by it

- `SpaceEntity.DefaultChannelId` is written nowhere in the product, so the "user joined the space" system
  message returns early every time and has never been sent. The `MessageId = 0` collision in it is fixed,
  which makes the feature correct as of the day something starts setting that field.
- The second layer of dead code — `Licensing/`, `InMemoryArgonCacheDatabase`, `SentryGrainCallFilter`,
  `CacheSubscriber` — plus `IClusterClientFactory`, `RecurringWorkerService<T>` and
  `ArgonHybridDcRegistry`, orphaned by the first sweep. All verified unreferenced; kept deliberately,
  because "looks unused" is a weaker claim for infrastructure than for a region layer nobody registered.
- The `__EFMigrationsHistory` model snapshot now carries placement and the repaired TTL column, but only
  affects tables created by the migration that wrote it. Existing tables still depend on the apply step.
