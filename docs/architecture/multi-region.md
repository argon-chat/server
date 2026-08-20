# Multi-region

What it would take to run Argon in more than one region, what is already built for it, and what
is built and does nothing.

## What exists today

More than it looks like, and none of it runs.

**Working, and already the right shape.** Cross-node cache coherence goes over NATS:
`HybridSpaceReadCache`, `HybridArchetypeCache` and `HybridPermissionCache` each publish an
invalidation subject and every node subscribes ([HybridSpaceReadCache.cs](../../src/Argon.Core/Features/Cache/L1L2/HybridSpaceReadCache.cs)).
That is the pattern the rest of this document extends across regions, because it is the pattern that
already works between nodes.

**Built and dead, and now deleted.** `DcWatcherService` and `DataCenterConnectionService` were
registered nowhere, `ArgonRegionalBus.GetAllDcsAsync` returned a list of one, and
`ClusterRouter.ComputeRouteAsync` returned `null!` while `ComputeEffectivity` returned `1`. That
layer is gone — `Features/RegionalUnit`, and the datacenter observer and retry filter under
`Features/Orleanse/Client`. `Features/RegionAware` is the one exception and was kept deliberately:
`HybridLogicalClock` and `HybridTimestamp` have no callers today, but cross-region ordering over NATS
is the direction the placement audit points at, and an HLC is the primitive that work needs. They are
scaffolding on purpose, not survivors of an incomplete deletion. The two paragraphs below are kept in the present
tense they were written in, because they are the record of why the approach was abandoned rather
than repaired.

Two pieces of it outlived the deletion, because they are still wired up.
[OrleansClientFactory](../../src/Argon.Core/Features/Orleanse/OrleansClientFactory.cs) still
configures the local client, and `IArgonDcRegistry` is still registered on both the client and the
silo path — `GetNearestClusterClient()` returns the local cluster with a comment saying multi-region
is not implemented, and with the watcher gone nothing moves an entry in it past `WAIT_CONNECT`.
`LocalUserSessionDiscoveryService` hardcodes `Region: "ru-3"`, `ServerId: "ru-spb-3"`.

The skeleton assumed one Orleans cluster per datacenter plus an Orleans client from each region into
every other. Half of that is right — a cluster per region — and the other half is a dead end, for two
reasons that are properties of the approach rather than of this implementation of it.

**The child container is not fixable.** `AddOrleansClient` needs its own service provider, and a
child provider cannot see its parent. `CreateClusterClient` therefore hand-proxies five services into
it with `new ServiceDescriptor(..., (_, _) => provider.GetRequiredService(...))` — exactly the five
that happened to be needed. Every dependency the client acquires later is another such proxy, found
at runtime. The method is still in the tree and no longer has a caller; it goes with
`IArgonDcRegistry` at stage 5.

**A region that will not connect takes the process with it.** `ClusterClientRetryFilter` retried
`SiloUnavailableException` and returned `false` for everything else, so the client gave up and
`StartAsync` threw — and `DcWatcherService.OnClusterRegistered` awaited that `StartAsync` inside a
subscription callback with no `try`/`catch`. Its `attempt` counter was also only reset on the
non-retryable path, so after a few outages the backoff sat permanently at its 30-second ceiling.

Both are fixed rather than worked around, in
[Features/Clustering/Regions](../../src/Argon.Core/Features/Clustering/Regions/) — see *The model*
below. The discovery half was genuinely dead and has gone: `DcWatcherService`,
`DataCenterConnectionService`, `IArgonClusterRouter` and its traceroute-based `effectivity`, which
computed a number nothing read.

**Built, correct, and unused.** [DbLocalityExtensions](../../src/Argon.Core/Features/EF/DbLocalityExtensions.cs)
generates CockroachDB `PRIMARY REGION` / `REGIONS` / `LOCALITY` DDL, including `REGIONAL BY ROW` and
`GLOBAL`. When this was written exactly one entity referenced it, with the call commented out, and
the `SURVIVE` clause was built and then not emitted. Both are fixed: `ArgonTablePlacement` names
eleven tables and the survival goal is derived from the region count. What has not changed is the
outcome — every table is still `REGIONAL BY TABLE IN PRIMARY REGION` in a real database, because the
declarations are inert until the migrations are regenerated. See stage 3.

**Not there at all.** Neither `SpaceEntity` nor `UserEntity` has a region. Nothing is homed anywhere.

## Orleans gives nothing here

Orleans had multi-cluster support — geo-distributed clusters with a gossip channel and
`MultiClusterOptions`. It was removed in Orleans 4.0 and Argon is on 10.2.2. There is no
"multi-region Orleans"; there is one cluster, and anything spanning clusters is ours to write.

Nor should one cluster span regions. Orleans membership assumes a LAN: failure detection is
timeout-based, every silo probes its neighbours, and placement has no concept of distance — the
resource-optimized director would happily activate a Moscow user's grain in Frankfurt because it had
more free memory. One cluster per region is the only shape that works.

**Two settings used to be the wrong way round**, and fixing them was stage 1.
[ArgonClusterEndpoints](../../src/Argon.Core/Features/Clustering/Hosting/ArgonClusterEndpoints.cs)
gave every region the same `ClusterId` and derived a per-region `ServiceId` from the datacenter.
Orleans means the opposite by those names: `ClusterId` identifies one cluster deployment, `ServiceId`
identifies the logical service and must stay stable, because storage providers and reminders key on
it. As written, two regions sharing a clustering store would have formed one cluster by accident, and
grain state written in one region was unreachable from another by construction.

`ServiceId` is now the constant `argon` — which is what `appsettings.json` had said all along through
a key that never took effect, because the silo builder binds the `Orleans` section first and the
explicit `Configure<ClusterOptions>` overwrote both properties afterwards. `ClusterId` stays a
per-deployment setting, and the local region's entry in `Argon:Regions` is checked against it.

## What was decided

All three of latency, data residency and availability, and a space stays in the region it was
created in.

Those are the expensive answers, and two of them contradict each other in a way worth stating before
anything is built.

### Residency and availability do not compose

If a Russian user's data may not leave Russia, then when the Russian region fails that user cannot be
served from Frankfurt. Not "should not" — the failover that would save them is the thing residency
forbids. The same is true of a space homed in a region that is down, once its rows are pinned there.

So availability cannot be bought by failing over *across* residency boundaries. It has to be bought
*inside* them, which means the topology is not three regions:

```
ru-a  ru-b        one residency zone, two regions, failover between them
eu-a  eu-b        one residency zone, two regions
us-a  us-b
```

Cockroach's `SURVIVE REGION FAILURE` needs three regions per database to place its replicas, so a
zone that must survive its own region loss needs three, not two — or it accepts `SURVIVE ZONE
FAILURE` inside a region and treats region loss as data-intact-but-unavailable. That is a cost
decision and it is the largest one in this document. It follows directly from picking all three
drivers and it is not visible from any single one of them.

Everything below assumes the zone-with-several-regions shape. Where it says "region" and the
distinction matters, it says which.

### What is pinned, and what is everywhere

The unit of pinning is not the space. It is the space's *content*, and everything else about the
space is replicated:

| | where it lives | why |
|---|---|---|
| user profiles, space metadata, archetypes, membership | replicated to every region | small, read constantly, written rarely — the shape a Cockroach `GLOBAL` table is for |
| messages and channel content | the space's home region | the largest table and the hot write path, and the one thing that has to be ordered by a single activation |
| voice | wherever the participants are | the SFU is regional by nature and a call belongs where the people are, not where the space is |

**A space's channels all live in the space's home region** — the region it was created in. They are
not spread by traffic: a search across a space would become a fan-out, and a space's channels moving
independently is a lot of machinery for a latency difference the write path does not feel.

What this buys is the behaviour a person expects when a region goes down. Losing one takes out
*writing to the channels homed there* — not sign-in, not profiles, not roles, not friends, not the
space list, because all of that is replicated. A user reconnects to another region and finds
everything except the message history of the channels that were in the region that fell over. The
pinned surface is small on purpose, and that, rather than any rerouting machinery, is what makes the
outage survivable.

Half of it is already built. Reading space metadata goes through `ISpaceReadGrain`, a
`[StatelessWorker]` over `HybridCache`, so any region with a warm cache can serve it — no routing, no
lease, nothing to fail over. What it needs is cache coherence across the boundary, which is the NATS
subject work in stage 4 and is on the list anyway. `ChannelGrain` and `SpaceGrain` are already
decoupled: there is no `Channel → Space` edge in the call graph, and the channel takes permissions
from `HybridPermissionCache`.

So a channel id carries the *space's* region, not the region of whatever activation created it —
`ArgonId.NewIn(spaceId)`. Space metadata being replicated is exactly what makes that necessary: the
activation that creates a channel can be anywhere, and stamping its own region would put a channel's
messages somewhere the space is not.

### Neither is a space *or* a user

Latency wants the space to be the unit — that is where traffic converges. Residency wants the user —
the law is about people. Picking one globally is what forces the contradiction; the way out is that
**the unit is a property of each table and each grain, not of the system**.

- **space-homed** — Space, Channel, BotGateway, Entitlement, SpaceRead, invites, moderation. Homed
  where the space was created, which is where its traffic is.
- **user-homed** — User, UserSession, Security, Friends, UserChat, Inventory, Ultima, SpaceBoost,
  UserLevel, notifications, device history. Homed in the user's residency zone.
- **region-local, homed nowhere** — FileStorage, EmailManager, jobs, feature flags. One per region,
  serving whoever is there.

`PlacementRegionalByRow` is already per-entity, so this costs nothing structurally: an entity
declares which column carries its home region. Moving a table from one unit to the other is a
one-line change and a migration, which is the property that matters, because the boundary will move.

**Messages are the case that decides how expensive residency is.** A message row has an author, who
is residency-bound, and a channel, whose space is latency-bound. Three ways out:

- Follow the space, and a Russian user's message text sits in Frankfurt. Cheapest, and only legal if
  message content is not treated as personal data that must be localized.
- Follow the author, and reading a channel's history becomes a scatter-gather across every region its
  members came from. This is not survivable on the read path — `bench/ArgonLoad` measured what
  happens to a bootstrap when one extra thing goes remote.
- Split them: id, channel, author and timestamp follow the space; content follows the author. History
  reads scan locally and then fetch bodies grouped by region — one batched remote read per region
  present, cacheable, and it degrades to the first option when everyone is local.

Which of these is legally required is not an engineering question, and the answer changes the read
path materially. What engineering can do is make it a per-entity switch, so the answer is a
migration rather than a redesign.

## The model

A region is one Orleans cluster, one Redis, one NATS server in a supercluster, and a set of Kestrel
entry nodes. Regions are grouped into residency zones. All regions share one CockroachDB cluster,
because Cockroach is a multi-region database and re-solving consensus over NATS would be strictly
worse.

Every addressable thing has a **home region**, and it is derivable from its key.

### One cluster client per region

A call into another region is an ordinary Orleans call on an ordinary Orleans client pointed at that
region's gateway. `IArgonRegionRegistry` holds one such client per configured region and answers
which of them are usable right now; the local region resolves to the process's own `IClusterClient`,
so a caller never has to special-case being at home.

Four things make that safe, and each is something the previous attempt got wrong.

**The child container is self-sufficient.** An Orleans client needs a service provider of its own and
a child provider cannot see a parent, which invites bridging the two by proxying host services in one
at a time — and the proxy list then grows by one every time something throws at runtime. It is not
necessary: `AddOrleansClient` calls `AddLogging()` and `AddSerializer()` itself. The only borrowed
service is the host's `ILoggerFactory`, so a remote region logs where everything else does. Argon's
own types go in as *instances*, built outside and closing over what they need, so nothing inside ever
reaches back out.

**The grain interfaces and the serializer are both copied, not rediscovered.** The client has to agree
with the far silo about what `IChannelGrain` is, and that agreement comes from
`IConfigureOptions<TypeManifestOptions>`; the host's are registered into the child container, which is
harmless to apply twice because every collection behind that options type is a set or a dictionary.

The serializer is the half that is easy to miss, and the fixture caught it. Most types crossing a
grain boundary carry no `[GenerateSerializer]`, so Orleans has no generated codec for them and falls
through to the catch-all `AddNewtonsoftJsonSerializer(_ => true, …)` that `AddArgonSerializer`
registers — for the wire *and* for the deep copy. A client container without it cannot even construct
a grain reference: building the proxy asks for a copier per argument type and throws on the first one
it cannot find. So `AddArgonSerializer` is an `IServiceCollection` extension the host and every
region client both call. Two copies of that registration that drifted would not fail; they would
disagree about the wire, in one direction, between regions.

**A region that will not connect is not an error.** `OutsideRuntimeClient.StartAsync` blocks until it
reaches a gateway and rethrows the moment the connection retry filter answers false — so the filter
retries everything until cancellation. There is no failure of a *remote* region that this process
should treat as fatal. And nothing awaits that `StartAsync` on the startup path: each peer is
supervised on its own task, and the registry answers `Connecting` until it is up.

**Gateways are found by name, not through the far region's Redis.** A clustering provider would mean
every region's processes connecting to every other region's membership store. A DNS name resolved on
Orleans' own refresh timer gives the same list, exposes nothing extra, and follows a rolling
deployment on the far side.

The registry refuses to hand out a client for a region that is not online, which matters more than it
sounds: an Orleans client that has not connected does not refuse a call, it accepts it and lets it
time out. Remote calls also get their own, much shorter response timeout — a region that is slow
rather than down is the case that takes a caller with it.

What is *not* built is the part that decides which region a call belongs to. Until the home region is
in the id (stage 2), the registry has clients and nothing to route with.

### Home region belongs in the id

The naive routing rule is a lookup: given `IChannelGrain(channelId)`, find its space, find the
space's region, then pick a cluster client. That is a cache on every entry node, an invalidation
path, and a cache miss on the hot path — for a value that, since spaces do not move, never changes.

It is in the key instead. [ArgonId](../../src/Argon.Core/Features/Clustering/Regions/ArgonId.cs)
mints UUIDv7 with the region written into `rand_a`, the twelve bits immediately after the version
nibble. The id stays a valid v7, stays time-ordered, and keeps 62 of its 74 random bits. Routing is a
pure function of the key, with no lookup, no cache and nothing to invalidate.

**The epoch is what makes an existing deployment portable.** An id minted before this existed has a
random `rand_a`, so it does not read as "untagged" — it reads as an arbitrary region, 4095 times out
of 4096, and no bit pattern distinguishes the two. What does distinguish them is the timestamp the id
already carries: everything older than the cutover was made when there was one region, so it belongs
to that one. `Argon:Regions:IdEpoch` is that instant, and below it the tag bits are ignored.

So there is no migration. No column, no backfill, no re-keying — the identifiers already record when
they were made. The epoch is set once, to any time after the tagging build is everywhere and before a
second region exists, and never moved.

Channels carry a region too, and the reason is worth recording. Every Ion method on
`IChannelInteraction` takes the space id as its first argument, so a channel call could have been
routed on the space — but not every caller has one: the hub's `IAmTyping` and `IAmStopTyping` hold a
bare channel id, and so would the next thing written against them. Tagging the channel id costs one
mint site and no contract change at all, because the `channelId` parameter those methods appear to
accept was never read — `SpaceGrain` created the entity and let EF generate the key.

`ArgonId.New()` is the one way to mint an identifier, and it is static rather than injected for the
same reason `ArgonDatacenter.Current` is: the region is a property of the process, fixed before
anything is constructed, and threading it through thirty constructors would deliver a constant. Raw
`Guid.NewGuid()` stays correct for the things that are not identifiers — a nonce, a random suffix, a
throwaway grain key — and a v7 would be actively worse for those, since it is sortable and carries
the time it was made.

Two things keep the stamp honest. Configuration refuses two regions on one index, because they would
mint identifiers claiming each other's; and a process refuses to start when the region it stamps
disagrees with the region it is configured as, because every identifier it made would name the wrong
region permanently and nothing downstream could tell.

### Except when a region is down

Spaces not moving is what makes the id enough — and availability means that under failure they move
anyway, involuntarily. Both can be true if the id is the *default* and not the authority:

```csharp
IClusterClient For(GrainId id)
{
    var home = RegionOf(id);
    return registry.IsHealthy(home) ? registry[home] : registry[Override(id)];
}
```

`Override` reads a `GLOBAL` Cockroach table, consulted only when the home region is known down, so
the normal path stays a bit-shift and a dictionary lookup. Cockroach being linearizable is what makes
it safe: the override row *is* the fencing token, and two regions cannot both win it. Failing over
means writing that row, and the write is the thing that must be a single transaction with marking the
old region evicted — otherwise both regions activate the same space and Orleans' one-activation
guarantee, which only ever held within a cluster, quietly stops holding at all.

Failover is only ever to a region **in the same residency zone**. That is the constraint the routing
table has to encode, and the reason zones need more than one region each.

### Which silos are gateways

A client reaches grains only through a gateway silo, and a silo configured with proxy port 0 is never
one — every gateway list provider filters on `Status == Active && ProxyPort != 0`. Argon shipped with
`ExposesClusterGateway` false on every silo role, so the distributed topology had five client roles
and nothing to connect them to. It is not a multi-region problem; that topology could not work at
all. `ClusterValidator` rule E10 now refuses it.

The rule for who should be one: **a silo should be a gateway exactly when losing it already means the
calls it would forward are unavailable.** A gateway carries client connections and forwards each
message to whichever silo holds the activation, so it adds interactive work to a role; adding it
anywhere else buys a new way to lose client connections and nothing else. A gateway on `jobs` keeps
clients attached to a cluster whose channels they cannot call.

By that rule it is `core` and only `core`. Most of the call sites reached from outside — channel,
space, user, security — are grains it hosts, so most forwarding stays inside the role, and it runs the
most replicas, which is where gateway redundancy comes from without extra machinery. The cost is one
extra hop for a call to a grain that lives elsewhere, on calls already doing database or
object-storage work that dwarfs it.

An earlier draft said a draining silo leaves the gateway list on its own. It does not. Providers
filter on `Status == Active && ProxyPort != 0`, and draining deliberately never touches membership, so
a draining silo stays `Active` and keeps being handed to clients until the process stops. Readiness
going false removes the pod from the Kubernetes Service — which stops new HTTP traffic and does
nothing to Orleans clients, because they dial addresses read from the membership table.

Cross-region is a separate decision and can stay separate. `GatewayManager` re-asks its
`IGatewayListProvider` rather than falling back to membership, so a remote region only ever talks to
what that region's gateway name resolves to. Widening the in-region posture later would not widen the
cross-region one, and the service behind the name is a selector rather than a code change.

### Where the boundary can be drawn

The measured grain-to-grain call graph — every `GetGrain<I…>` call site in `src/Argon.Api/Grains`:

| edge | call sites |
|---|---:|
| `AccountDeletionGrain → EmailManager` | 5 |
| `UltimaGrain → SpaceBoostGrain` | 5 |
| `UserSessionGrain → UserGrain` | 5 |
| `ChannelGrain → VoiceControlGrain` | 4 |
| `UserGrain → FileStorageGrain` | 3 |
| `SpaceGrain → BotGatewayGrain` | 2 |
| `SpaceGrain → FileStorageGrain` | 2 |
| `ChannelGrain → FileStorageGrain` | 2 |
| `UserLevelGrain → InventoryGrain` | 2 |

Grains barely call each other, and that is what makes this tractable. Against the space/user/local
split above, exactly one edge of any weight crosses: `ChannelGrain → VoiceControlGrain` — and it
should not follow the space at all (see *Voice*). Everything else crossing is weight one:
`BotGatewayGrain → UserGrain`, `UserGrain → ContentModerationGrain`, `FileStorageGrain → UltimaGrain`.
The FileStorage edges do not cross, and that is the reason it is region-local rather than homed —
homing it either way would have made three of the heavier edges remote.

The traffic is not between grains. It is from the entry node into them:

| grain | call sites outside grains |
|---|---:|
| `IChannelGrain` | 39 |
| `ISpaceGrain` | 33 |
| `IUserGrain` | 33 |
| `IUltimaGrain` | 27 |
| `ISecurityGrain` | 21 |

That is the good case. A user in Moscow opening a space homed in Frankfurt pays **one** WAN round
trip per action, from their entry node into the eu cluster — not a chain of them, because the grains
on the far side call each other locally. The thing that must never happen is a grain in one region
awaiting a grain in another *inside a turn*, and the graph above says it does not have to.

## Layer by layer

### Data — Cockroach already does this

One database, `PRIMARY REGION` plus the others, and the `SURVIVE` clause actually emitted. Then the
annotations that exist and are unused:

- **`REGIONAL BY ROW`** on space-scoped tables (spaces, channels, members, archetypes, invites), homed
  by the space's region.
- **`REGIONAL BY ROW`** on user-scoped tables, homed by the user's region. This is where residency
  lands, and it is per-table by construction.
- **`GLOBAL`** on the small, read-everywhere, write-almost-never tables: feature flags, system
  archetypes, the system user and space, entitlement templates, and the home-region override. Global
  tables pay on write and are free to read from anywhere, which is exactly their access pattern.

Nothing about this needs NATS. It needs `PlacementRegionalByRow()` uncommented on the right entities,
a home-region column that means something, and the one commented-out line in the SQL generator.

#### Staying portable to PostgreSQL

Production is CockroachDB and a single-region deployment — and the integration suite — is vanilla
PostgreSQL. Both replay the *same* migration files, which only works because every CockroachDB-ism
goes through one of exactly two mechanisms, and there is no third:

| what it is | how it is handled | example |
|---|---|---|
| a function or expression | taught to PostgreSQL before the first migration runs | `unique_rowid()` |
| a DDL clause | emitted at apply time by a generator installed only for CockroachDB | `LOCALITY`, `SURVIVE`, the TTL parameters |

The first is [PostgresCompatibilityShims](../../src/Argon.Core/Features/EF/PostgresCompatibilityShims.cs),
which defines the missing built-ins so that ninety-seven column defaults calling `unique_rowid()`
execute byte for byte on both engines rather than being rewritten. The second is
`MultiregionalMigrationsSqlGenerator`, which `DatabaseFeature` installs only for Cockroach; on
PostgreSQL the stock Npgsql generator simply ignores the `Regional:*` and `Job:Expiration`
annotations the model carries.

The consequence, and the rule: **a migration file must never contain a clause PostgreSQL cannot
parse.** A clause has no off switch once it is written into a migration — unlike a function, which can
be defined. Clauses travel as model annotations; functions travel as SQL plus a shim.

Both halves are enforced rather than remembered. `MigrationPortabilityTests` scans the string literals
of every migration — literals only, because the first version of it found the word "survive" in a
comment — and fails if a Cockroach function appears without a matching shim, or if a Cockroach-only
clause appears at all. The shim list is read out of the shims themselves, so adding a shim is the
whole of adding a shim.

### Realtime — the one place NATS is load-bearing

Within a region the SignalR Redis backplane stays. It was benchmarked at 87 000 events/s and 1.3 ms
against Redis ([bench/ArgonBus](../../bench/ArgonBus/README.md)) and the constraint it hit was the
server it points at, not its design.

Across regions it cannot stay. The backplane's group registry lives in one Redis; a `MessageSent`
raised by a `ChannelGrain` in eu has to reach a socket held by an entry node in ru, and that is a
second tier.

NATS is genuinely the right tool for that tier, and specifically for one feature: **superclusters
with gateway connections propagate interest, not traffic.** A region only receives a subject if
something there is subscribed to it. So:

```
eu ChannelGrain raises MessageSent
  → local Redis backplane           (every eu socket in the space)
  → nats publish rt.space.{spaceId} (crosses a gateway only if ru/us have a subscriber)
      → ru entry node re-publishes into its own local backplane
```

Each entry node subscribes `rt.space.{id}` for the spaces it holds members of, and `rt.user.{id}` for
its connected users. That is the same subscription set the backplane already maintains locally, and
NATS holds it 25× cheaper than Redis pub/sub does — 30.9 µs per subscription against 790 µs, measured
at twenty thousand.

Three things this tier has to get right and the local one does not:

- **Loops.** An event re-published into the local backplane must not go back out over the gateway.
  A region tag on the envelope, dropped on receipt if it is our own.
- **Ordering and duplicates.** WAN reordering is real, and `FireDetached` already gave up ordering
  within a region. The desktop client's cursor is still a high-water mark, so a lower replay id is
  discarded outright — the client-side dedupe-by-id that
  [ChannelGrain](../../src/Argon.Api/Grains/ChannelGrain.cs) already flags as owed becomes mandatory
  before this ships, not optional.
- **The replay log.** `RedisRealtimeReplayBuffer` is per-region, so a client reconnecting to a
  different region than it left presents a cursor that region has never seen. Either the cursor
  carries its region and a foreign cursor forces a full resync, or replay entry ids become globally
  ordered. The first is a two-line change and the honest one — and with failover in the picture,
  reconnecting elsewhere stops being an edge case.

### Cache coherence — already NATS, one subject too coarse

All four invalidation subjects are flat constants — `space.read.invalidate`, `archetypes.invalidate`,
`permissions.member.invalidate`, `permissions.space.invalidate` — so every node gets every space's
invalidation. Fine inside a region, wasteful across a gateway, because a flat subject has interest
everywhere and defeats the one property that made NATS the right choice for the tier. They need the
id in the subject (`space.read.invalidate.{spaceId}`) before they cross a region boundary; a wildcard
subscription keeps the within-region behaviour identical.

### Presence — replicate it, do not query it

Presence lives in the region's Redis. A `SpaceReadGrain` in eu building a member list needs online
status for users connected in ru, and `bench/ArgonLoad` found that presence became the entire
remaining cost of a space bootstrap once the roster stopped being re-encoded. A cross-region read on
that path is not survivable.

So presence is replicated, not queried: heartbeats stay local and publish nothing; only *transitions*
publish, on `presence.{region}.{userId}`; every region subscribes and keeps a local replica. Same
shape as the cache invalidation that already works. Presence is derived state with a TTL, so
replicating it across a residency boundary is a question to ask a lawyer once rather than a design
problem — and if the answer is no, a space in eu shows ru members as unknown rather than offline.

### Voice — follows participants, not the space

LiveKit is regional by construction and already carries an `Sfu.Region`. A call in a space homed in
eu, between three people in Moscow, belongs on the Moscow SFU. This is the one place where the
space's home region is the wrong answer, and it is why `ChannelGrain → VoiceControlGrain` is the one
heavy edge that crosses. The fix is that it should not be a cross-region grain call at all:
`ChannelGrain` picks an SFU region from the participants and hands it over, rather than calling a
`VoiceControlGrain` that lives somewhere specific.

### Files

S3 with a bucket per region and the region in the file id. `FileStorage` is region-local in the grain
layout precisely so this stays a separate decision — and with residency in play, an avatar uploaded
by a Russian user is the same question as a message body, with the same per-entity answer.

## Corrections found by review

Two claims in earlier drafts of this document were wrong, and both mattered.

**Grain state is not isolated by `ServiceId`.** Argon does not use the Orleans Redis persistence
package; it has its own provider, and its key is
`@grains/{grainId.Type}/{grainId}:{stateName}` — [RedisStorage.cs:41](../../src/Argon.Core/Features/Orleanse/Storages/RedisStorage.cs).
No service id, no cluster id. `ClusterOptions` is injected into that class, assigned to a field and
never read. So two regions pointed at one `OrleansStorage` Redis overwrite each other's grain state
byte for byte, and nothing in configuration validation notices. Reminders are the other way round —
the official package keys on `{ServiceId}/reminders` — which is why changing the service id orphaned
reminder rows and orphaned no grain state at all.

**`REGIONAL BY ROW` does not home rows for free.** The claim was that a channel's messages land in the
space's region because the activation that inserts them runs there, and Cockroach defaults
`crdb_region` to `gateway_region()`. That holds only if each region's pods talk to their own Cockroach
nodes. Production has one `ConnectionStrings:Default` naming a single service, so every pod in every
region would enter through the same gateway and every row would be homed the same way. Per-region
connection strings are a requirement, not a detail, and nothing enforces or checks them.

## What breaks

- **A cross-region grain call is a WAN round trip, and it is not free at the caller either.** Calls
  through a client into a remote cluster carry no call-chain context, so
  `RequestContext.AllowCallChainReentrancy()` in `AppHub` stops meaning anything past the boundary.
  The remote response timeout is its own setting for the same reason: on the shared default a region
  that is merely slow holds every caller for thirty seconds a call.
- **The two clusters must agree on their identifiers, and nothing checks it.** A client configured
  with the wrong cluster id does not fail, it never finds a gateway it is allowed to talk to. A
  service id that differs between regions is silent in a different way — which is the second reason
  to make the service id constant and put the region in the cluster id.
- **Failover is where the one-activation guarantee is won or lost.** It only ever held within a
  cluster. Re-homing a space to a second region while the first is merely unreachable — not dead —
  gives two activations of the same grain writing the same rows. The Cockroach override row is the
  only thing standing between the design and that outcome, so it has to be the sole authority, taken
  transactionally, and re-read on activation rather than trusted from the caller.
- **Blue-green gets a second dimension.** The drain and probe work in
  [k8s-probes.md](../../deploy/k8s-probes.md) is per-cluster. Rolling six regions means six of those,
  and a region that is draining must stop being a routing target before it stops accepting grains —
  which is the same signal failover needs, arriving for a different reason and meaning something
  different. Draining is not failover, and routing has to tell them apart.
- **The integration suite boots real silos.** A second cluster in it is the only way any of this gets
  tested, and `GrainMigrationTests` already shows two silos in one cluster is at the edge of what one
  test process holds.

## The lifecycle of a region

Joining one, retiring one, and taking one down for a few hours are three different operations with
three different owners, and only the first is executable today. They have their own document:
[region-lifecycle.md](region-lifecycle.md) — ordered steps, what breaks if each is skipped, the
invariants nothing enforces, and the one-way doors.

## A staged order

Each stage is useful on its own, which is the point — none of them is "half of multi-region".

1. **Fix `ClusterId` / `ServiceId`** — done. `ServiceId` is the constant `argon` everywhere, which is
   what `appsettings.json` had been declaring through a key that never took effect. `ClusterId` keeps
   its default and is what a multi-region deployment sets per region. The dead `Orleans:ClusterId` and
   `Orleans:ServiceId` keys are gone, `ArgonClusterEndpoints.Resolve` no longer takes a datacenter it
   ignored, and `ArgonRegionOptions` now checks the local region's entry against the cluster identity
   this process actually runs as — two values from two sections whose disagreement is otherwise
   invisible.

   **It orphans state.** The service id is part of every grain-storage key and every reminder row, so
   anything written under the old derived `argon-region-{dc}` will not be found. Harmless before
   release; a migration after it.
2. **Put the home region in the ids** — done. `ArgonId.New()` mints every identifier Argon stores or
   addresses, across 80 call sites; the epoch makes everything older resolve to the original region
   with nothing migrated.
3. **Turn on Cockroach localities** — declared, and *not yet in effect*. `ArgonTablePlacement` names
   the eleven tables the decomposition decided; `DbLocalityTests` asserts the generator writes the
   right DDL for them; and `TablePlacementTests` reads the real database back and shows it did not
   arrive.

   The reason is worth knowing before anyone reaches for the model configuration again: **schema
   creation runs from the migration files, not from the model.** Those were generated before any
   entity declared a placement, so the snapshot carries `Regional:MultiRegion` and not one
   `Regional:Locality`, and EF emits no operation when a locality annotation changes — pinned by a
   test. The declarations become real when the migrations are regenerated, which is the squash that
   was planned anyway, and the two red tests in `TablePlacementTests` are its acceptance criteria.
   Run them with `ARGON_TEST_DB=Cockroach`: today they fail on a `LOCALITY` diff, and the day the
   squash lands they go green with nothing else changed. They are not marked explicit, on purpose —
   the squash is a one-way operation against production and a skipped test cannot tell a good one
   from a broken one.

   Two more things surfaced with it. The `SURVIVE` clause was generated and commented out because it
   was hard-coded to `REGION FAILURE`, which Cockroach cannot create a database with unless three
   regions exist; it is derived from the region count now. And the snapshot's copy of that annotation
   is stale — it says three regions and `REGION FAILURE` while the deployment configures one.

   **That staleness is not free, and an earlier version of this section was wrong about why.** It
   claimed `CREATE DATABASE` never runs. It does: `WarmUp<ApplicationDbContext>` asks
   `IRelationalDatabaseCreator` whether the database exists and creates it when it does not, and that
   path goes through the multiregional generator. So on the first boot against a cluster where nobody
   pre-provisioned the database, Argon itself emits
   `CREATE DATABASE … PRIMARY REGION … REGIONS … SURVIVE …`. CockroachDB only knows a region name
   because some node was started with `--locality=region=…`, so against a cluster brought up without
   matching localities that statement is rejected, no migration applies, and the pod restarts into the
   same failure forever. Provision the database yourself and the branch is never taken — which is what
   every deployment so far has done, and why nobody hit it.

   Two consequences. `WarmUpExtension` now turns that rejection into an error naming the locality
   requirement and the regions `Database:Regions` declares, because Cockroach's own message points at
   the database rather than at the node flags that are actually wrong. And the annotation in the
   snapshot has to be treated as live DDL the day someone points Argon at an empty cluster: it is the
   thing that decides `PRIMARY REGION`, `REGIONS` and `SURVIVE` for a database nobody can re-create
   without a migration.
4. **Make the subjects routable** — the id in the invalidation subject, a region tag on realtime
   envelopes, dedupe-by-id in the desktop client, a region in the replay cursor. All of it is correct
   within one region too, which is the test that it is the right change.
5. **The region registry** — built, in
   [Features/Clustering/Regions](../../src/Argon.Core/Features/Clustering/Regions/), with a fixture
   that stands up two clusters and calls a grain across them. What remains is routing: the rule that
   turns a grain key into a region, which needs stage 2 first. `DcWatcherService`,
   `DataCenterConnectionService`, `DcClusterConnectionListener`, `ClusterClientRetryFilter` and
   `IArgonClusterRouter` were superseded by it and have already gone. What is left of that layer is
   still registered, and goes when routing lands: `IArgonDcRegistry` with its `ArgonDataCenterStatus`
   enum, and `OrleansClientFactory.CreateClusterClient`, whose sibling `Builder` is the one part of
   that file the local client still needs.
6. **Cross-region realtime over a NATS supercluster**, and only then a space actually homed elsewhere.
7. **Failover.** The override table, the eviction protocol, and the drills. Last, because it is the
   only stage that can corrupt data if it is wrong, and because every stage before it is a
   prerequisite for testing it.

Stages 1 to 4 are worth doing whether or not there is ever a second region.

## Still open

- **Is message content residency-bound?** A legal answer, and it picks one of the three message
  layouts above. The read path differs by a scatter-gather.
- **How many regions per residency zone?** Two is enough to fail over; `SURVIVE REGION FAILURE` wants
  three. The gap between those two numbers is most of the infrastructure bill.
- **May presence and other derived state cross a residency boundary?** If not, cross-zone member
  lists show unknown rather than offline, and that is a product decision as much as a legal one.
- **Is membership cheap enough to be global?** It is read on every bootstrap, which argues for
  it; it is written on every join, and a `GLOBAL` table pays consensus across every region on write.
  Chat reads memberships far more than it writes them, so probably — but that is a measurement, not a
  deduction.
- **Does the bot API follow the space or stay central?** `BotEventPublisher` already publishes to
  NATS per space, so it may cost nothing either way.
