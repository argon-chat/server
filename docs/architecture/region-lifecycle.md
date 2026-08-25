# Region lifecycle

Joining a region, retiring one, and taking one down for a few hours. Companion to
[multi-region.md](multi-region.md), which is the design; this is what an operator does and what
happens if a step is missed.

Written from a review of the code rather than of the design. Where a step has no implementation it
says so — those are listed because skipping them is what makes the rest unsafe, not because they can
be done today.

## Three facts that govern everything below

**Routing does not exist.** `IArgonRegionRegistry` has no consumers in `src/`. `RegionOf`,
`TryGetClient`, `StatusOf` and `PeersInZone` are never called. Every request is served by the local
cluster whatever the id says, so most failure modes here are latent rather than live.

**Cross-region drain does not exist and cannot be added alone.** `SiloDrainService` migrates
activations to other silos *in the same cluster*; destinations come from the local
`siloStatusOracle`. Moving a grain between regions needs a re-homing authority that is not built.

**Cockroach placement is declared and inert.** The eleven `ArgonTablePlacement` declarations produce
no DDL until the migrations are regenerated. The proving tests are no longer `[Explicit]`: the
fixture now starts its Cockroach node with a matching `--locality` and gives the database a primary
region, so `TablePlacementTests` fails with an assertion diff naming the missing `LOCALITY` clause
rather than erroring on a statement the node cannot accept. That red is the acceptance criterion for
the regeneration — it turns green when, and only when, the placement reaches a database.

---

## Join a region

Additive, and the only one of the three that is safe today.

| # | Layer | Step | If skipped |
|---|---|---|---|
| 1 | paper | Allocate an `Index`, a `Snowflake:DataCenterId` and a fresh `Argon:Cluster:Id`, from a record of every value ever used. `ServiceId` stays `argon`. | Validation only sees the *current* node set. A reused index makes every id the retired region minted resolve to the new cluster. |
| 2 | config | Roll the tagging build everywhere, then set `Argon:Regions:IdEpoch` in **every** region, with an explicit offset. | Missing epoch is fatal only where the region feature is declared. A mistyped one now throws; before, it silently meant "tagging off". |
| 3 | Cockroach | Start the region's nodes with `--locality=region=<name>`. Confirm with `SHOW REGIONS FROM CLUSTER`. | `ADD REGION` fails — the region does not exist in the cluster. |
| 4 | Cockroach | `ALTER DATABASE "argon-db" ADD REGION "<name>";` | Nothing can be homed there. **No file in this repository emits this DDL** — it is entirely the cluster operator's. |
| 5 | Cockroach | Wait for the rebalance to settle. | Reads from the new region cross the WAN until it completes, and the join looks broken rather than unfinished. |
| 6 | Redis | Provision all five profiles. `OrleansStorage` and `Orleans` must not be shared with another region. | Grain state has **no region separation in the key** — see the correction in [multi-region.md](multi-region.md). Two regions on one `OrleansStorage` overwrite each other. A shared `Backplane` merges SignalR groups, because the channel prefix is the literal `argon-bus`. |
| 7 | NATS | Stand up the region's server; confirm the four invalidation subjects resolve. | The hybrid caches never receive invalidations and serve stale rosters, archetypes and **permissions** for up to 48 hours. Silent. |
| 8 | Orleans | Deploy the silos with the region's **own** Cockroach connection string. Boot in isolation. Wait for `/health/startup`. | Row homing follows the connection, not the grain. Production has one connection string, so today every row would be homed the same way whatever region inserted it. |
| 9 | DNS | Point `Nodes:{r}:Gateway` at a name fronting **only** the `core` pods. | `RegionGatewayListProvider` returns an empty list and the peer stays `Connecting` forever — safe, but indistinguishable from a region that is down. |
| 10 | config | Add the node to every other region's `Nodes` and roll. | Until then the region exists and nothing routes to it, which is the correct order. |

---

## Unjoin a region

Steps 1, 2 and 4 have no implementation. This sequence is not currently executable.

| # | Layer | Step | If skipped |
|---|---|---|---|
| 1 | routing — **none** | Establish that nothing still addresses ids carrying the region's index. | There is no override table, no re-homing, no id rewrite. Once routing exists those ids throw `UnroutableIdException`, and the only handler downgrades it to "no client" — a configuration mistake becomes indistinguishable from an outage. |
| 2 | status — **none** | Mark the region not-a-routing-target while it still accepts grains. | There are three statuses and `TryGetClient` is binary on `Online`. The only way to stop routing to a region is for it to go `Offline`, which is what failure looks like. |
| 3 | Cockroach | Stop the region producing rows. | The row drain below races new inserts and never converges. |
| 4 | data — **none** | Write the home-region override and mark the old region evicted, in one transaction. | Two regions can activate the same space's grains and write the same rows. |
| 5 | realtime | Force every attached client to a full resync. | The replay buffer judges a cursor by wall-clock age and then ranges the **local** stream. A recent cursor from another region returns "you missed nothing". No error, no log line. |
| 6 | Orleans | Drain silos one pod at a time, ≥2 of each role alive throughout. | Draining all at once is worse than useless: migration is skipped when a silo is the only active one, the placement filter returns an empty candidate list, and the stability heuristic reports success after ~15 s while activations are still resident. |
| 7 | writes | Let `MessageWriteBuffer.StopAsync` drain. | Queued rows whose senders were already told the message landed are lost. |
| 8 | Cockroach | In order: drop the survival goal if fewer than three regions remain; move the primary region if dropping it; `UPDATE "Messages" SET crdb_region = …` until zero; then `DROP REGION`. | `DROP REGION` fails outright — the enum value is still in use by `Messages`. This is hand-run SQL rewriting the largest table across the WAN, with no tooling. |
| 9 | Cockroach | Only then `cockroach node decommission`, one at a time. | Decommissioning first leaves the database holding a region whose nodes are gone. |
| 10 | config | Remove the node from every other region's `Nodes` — after, not before, its ids stop being addressed. Never shrink `Nodes` to one entry while those ids exist. | Peers retry a dead gateway name forever at the 30 s ceiling. And `RegionOf` takes the no-peers fast path and returns `Self` **before reading the id**, silently claiming the retired region's data. |
| 11 | paper | Retire the `Index` and the `DataCenterId` permanently. | Nothing detects reuse. |
| 12 | accept | Lost with the region: presence (120 s TTL, self-heals), replay streams, message dedup keys, OTP codes **and OTP rate-limit counters**, in-flight QR logins, the SignalR group registry, in-memory JetStream bot streams. | Only presence self-heals. Losing the OTP rate-limit counters resets abuse protection — a security loss. Losing the dedup window means retries within two minutes double-post. |

---

## Region maintenance

A few hours, planned, region returns. Most of this does not exist; it is written so the gaps are
addressable.

The distinction that matters first: **`cockroach node drain`** moves leases off one node and takes
seconds; **Orleans silo drain** hands activations to peers *in the same cluster*; **region quiesced**
has no implementation at all. An operator who reads "drained" as "safe" and drains a whole Cockroach
region takes the database down instead of performing maintenance.

| # | Layer | Step | State today |
|---|---|---|---|
| 1 | authority | Write the region's target state somewhere durable and global. | **Partial.** A region can now say what it intends: `RegionIntents` holds one declaration per region and `StatusOf` merges it with reachability, taking the lower of the two, so a region can report itself as `Draining`. Not durable — the replica is in memory, and a region whose pods all restart comes back `Active` with nobody left to repeat the declaration. Re-declare after a rollout; a window that outlives a full restart still needs a store. |
| 2 | announcement | Tell peers to stop choosing this region for new work while it still serves what is homed here. | **Partial.** The channel exists — a NATS subject per region, wildcard subscription, repeated declarations, last-writer-wins — and an announcement can only lower a region's status, never raise it. What it reaches today is the region's own pods, because every role reads one `ConnectionStrings:nats`: declare on any pod and the region converges. Reaching the **other** regions needs their NATS servers gatewayed into a supercluster; that is a server-side link and no code change. Until it exists a peer still infers status from its own gateway count, and maintenance still looks like a crash from outside. |
| 3 | minting | Steer new space and user creation to a peer in the same zone. | **None.** Nothing consults `PeersInZone`. Every space created during the window is permanently stamped with the region being taken down — **the one omission that finishing the maintenance does not repair.** |
| 4 | read-only | Reject writes, keep reads. | **None.** There is no state between fully serving and gone. |
| 5 | entry point | Stop admitting new websockets, keep existing ones. | **Partial.** Client roles now register their own checks and the same four paths, and preStop no longer stops the process on the spot: it sets `ClientStopSignal`, readiness turns unhealthy, and the pod waits `HostHooks:PreStopLeadTime` to leave the Service before exiting — so sockets are shed by Kubernetes routing away rather than severed. The wait is measured from the first withdrawal, so a retried hook does not multiply it. Still missing the finer half: no gate that admits nothing while keeping existing sockets, since the only lever is all-or-nothing removal from the Service, and no way back — a client stop is a countdown, not a state. |
| 6 | realtime | Move clients off with a cursor the destination can honour. | **Partial.** `Resume`/`NeedFullResync` exists; the cursor carries no region, so a foreign cursor is silently accepted. |
| 7 | Orleans | Per silo: loopback preStop, one pod at a time, `maxUnavailable: 1`. | Works. Note the built-in wait before migrating is 10 s against a 30 s peer refresh. |
| 8 | Cockroach | Drain nodes one at a time. **Never drain a whole region as maintenance.** | Under `SURVIVE ZONE FAILURE` every regional range homed there has all three voters in that region and loses quorum. Read the real goal with `SHOW SURVIVAL GOAL`, not the one inferred from configuration. |
| 9 | k8s | Plan the window knowing Argon barely notices. | Silo readiness consults drain state and Orleans membership; client readiness consults the stop signal alone. There is still no Redis, NATS or database probe. `OrleansClusterHealthCheck` is no longer mistagged — it carries `diagnostic` rather than `ready`, which is what it always was; the mislabelling that made it look like a readiness gate is gone, but nothing new gates on it. Npgsql retries transient failures five times, so a bad region still turns one call into five 30-second waits on a pod that reports Ready. |
| 10 | all | Never run a region change concurrently with a deployment or migration. | The migration lock is application-level and each statement auto-commits on its own. |
| 11 | reversal | Clear read-only, resume minting, re-admit sockets, tell peers. | **Partial.** Two of four. Silos: `CancelDraining` accepts `Drained` as well as `Draining`, reachable at `GET /internal/undrain` (loopback). Peers: declare `Active` again and the announcement supersedes the old one by `DeclaredAt`. Read-only and minting still have nothing — and minting is the one that does not repair, since a space stamped during the window keeps that region forever. Client pods have no reversal by design: `/internal/undrain` answers 404 there, and cancelling a client rollout means stopping the rollout. |

---

## Invariants

Enforced: no two configured regions share an `Index` or a `ClusterId`; `Self`'s ids match
`Argon:Cluster`; `IdEpoch` is present when there is more than one region; a process mints for its own
region; a topology with client roles has a gateway silo; configuration errors are fatal.

Not enforced by anything:

- **An index or a `DataCenterId`, once used, is never reused.** Validation cannot see history.
- **The epoch is identical in every region and never moves.** Not persisted, not compared, not
  required to be in the past.
- **The epoch used to decode equals the one used to encode.** `RegionOf` decodes with the bound
  options; `ArgonId.NewIn` encodes with the process-wide value read from raw configuration.
- **`ARGON_REGION_DC` names the region the pod is actually in.** The startup guard compares two
  derivations of the same key, so a pod deployed into the wrong region passes it trivially. Only the
  ClusterId cross-check catches it.
- **`Database:Regions` and `Argon:Regions` agree.** Bound separately; validation never reads the
  database section. An Orleans region can exist with no Cockroach region behind it.
- **Each region's pods talk to their own Cockroach gateways.** Mis-homing is silent.
- **Region names are legal NATS subject tokens.** Nothing constrains the character set, and a name
  containing `.` or `>` breaks the stage-4 subjects.
- **`ArgonId.New()` is the only way to mint an identifier.** A convention in a doc comment across 85
  call sites, with no analyzer and no test. `ArgonId.Create(int)` is public and unguarded.
- **One activation per grain key across the deployment.** Held today only because nothing routes
  cross-region.

## One-way doors

1. **The region index in an id.** A pure decode of an immutable key. No override, remap or re-key
   exists. Once minted, an id names that region for life.
2. **Index 0 as a value.** Pre-epoch ids, non-v7 Guids and post-epoch ids whose tag reads zero all
   fold to it. **Some node must always claim index 0** — any region may, but retiring the value means
   re-homing every legacy id.
3. **The epoch, once ids exist on both sides.** Advancing it reclassifies everything minted in
   between; retreating it makes pre-tagging ids decode random bits as a real region. One config edit,
   no error, no undo.
4. **A space's residency**, decided by whichever region's entry node served `CreateSpace`.
5. **Moving message rows across a residency boundary.** If the legal answer is that they may not move,
   unjoin is export-and-delete rather than update — a different runbook.
6. **Per-region S3 buckets.** Splitting later strands every pre-split object, because every existing
   file id resolves to region 0.
7. **A `DataCenterId` inside a written `MessageId`** — part of the primary key of `Messages`.

## What needs code, as opposed to configuration

Routing consumers; the home-region override and its eviction protocol; an epoch equality check at
startup; any surface exposing `RegionStatus`.

Every piece of Cockroach region DDL — the generator overrides exactly two operations, create-database
and create-table. Tooling to rewrite `crdb_region`. Cross-region activation drain. Durable intent, so
a maintenance window survives every pod of the region restarting. Read-only mode at any layer.
Websocket admission gating — the readiness signal exists now, but it removes the pod outright rather
than admitting nothing while keeping what it holds. A server-initiated "reconnect
elsewhere". Region in the replay cursor. Presence replication — there is no `presence.*` subject at
all, and a missing key reads as *offline* rather than *unknown*, so a foreign-region user appears
offline. Leader election for deployment-wide sweepers. SFU selection by participant — the SFU config
is a single instance, its `Region` field is read nowhere, and every `IVoiceControlGrain` call site
keys on `Guid.Empty`. Region resolution in the LiveKit webhook. Cross-region invalidation for read
state and mute settings, which delete a local Redis key and publish nothing. A region story for direct
messages, which are absent from the placement block entirely.

Configuration only: the region list and its fields, per-region cluster id, snowflake datacenter id,
five Redis strings, NATS, the Cockroach connection string, gateway DNS, SFU URLs, database regions,
and the probe wiring already documented in [k8s-probes.md](../../deploy/k8s-probes.md).

## Worth fixing regardless of how many regions there are

- `SpaceInvite` is declared `PlacementGlobal()` in the placement block and has a commented-out
  `PlacementRegionalByRow()` beside the entity. Whichever wins at regeneration decides invite
  behaviour, and nothing flags the disagreement.
- The model snapshot carries `"Survive":"REGION FAILURE"`, stale against the derivation.
- `LocalUserSessionDiscoveryService` hardcodes `ru-3` / `ru-spb-3`. Not user-visible — both fields are
  read by nothing — but the remarks claim the notifier routes on them and it does not. They become
  wrong the moment a region-aware notifier consults them.
- The `region` parameter of `OrleansClientFactory.Builder` is still unused for everything but the
  retry filter's log messages.

Fixed since this was written: the drain state machine no longer returns a half-drained silo to
service and can be reversed; the legacy retry filter is off the local client path; the peer supervisor
promotes unconditionally after a successful connect; the peer map is frozen and no longer cleared out
from under readers during shutdown; `SpaceInvite` has one placement rather than two; the cluster
health check is tagged as the diagnostic it always was rather than as a readiness gate it never ran
as; every silo role validates its region configuration rather than only `entrypoint`; client roles
have health checks and a graceful stop that leaves the Service before the process ends; and the
detailed `/health` report is served to loopback callers only, since on a client role that listener is
the public one.
- "Draining" is already taken at the session layer, and `UserSessionMeta.Region` already means the
  client's country — both will collide with a canonical region identity.
