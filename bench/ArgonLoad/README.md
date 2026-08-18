# argon-load

Load scenarios pointed at a **running** server. Not a test project: nothing here is collected by
`dotnet test` or gated in CI, and it never starts a host of its own — the whole question is how a
real deployment behaves, and an in-process `TestServer` would answer a different one.

```
dotnet run --project bench/ArgonLoad -- --scenario login-storm --target http://localhost:5002 --clients 200
```

It drives the generated Ion client from `Argon.CodeGen`, so it speaks the same CBOR framing as the
desktop client and stays in step with the contracts by construction.

## Scenario A — login storm

What the desktop client does between "signed in" and "first screen", taken from
`poolStore.loadServerDetails`:

```
GetSpaces()
  per space, five in flight at a time:
    GetServerArchetypes(spaceId)   → IEntitlementGrain(spaceId)
    GetMembers(spaceId)            → ISpaceReadGrain(spaceId)
    GetChannels(spaceId)           → ISpaceReadGrain(spaceId)
    GetChannelGroups(spaceId)      → ISpaceReadGrain(spaceId)
```

A user in N spaces pays 1 + 4N calls before anything renders, and three of every four ask the same
grain about the same space.

Every virtual user joins the same space deliberately. Spreading them over many spaces spreads them
over many activations and the queue never forms, which measures the wrong thing.

### Why a herd and not a ramp

The client keeps its state in IndexedDB and is fed by the event stream; once it is up it barely calls
the server again. What it does is *arrive* — after a deploy, after a network blip, at nine in the
morning — and arrive together. Ramping the same arrivals over a minute hides exactly the effect worth
measuring.

### What it found

The three space calls used to go to `ISpaceGrain`, which is one activation running one turn at a
time. They queued behind each other, and behind every other client asking the same thing:

| clients | first screen | GetSpaces | GetMembers | GetChannels | GetChannelGroups |
|---:|---:|---:|---:|---:|---:|
| 5 | 137 | 24 | 60 | 86 | 106 |
| 25 | 622 | 64 | 307 | 498 | 549 |
| 50 | 836 | 100 | 378 | 679 | 735 |
| 150 | 3969 | 348 | 2103 | 3409 | 3612 |

A clean staircase in the three, with `GetSpaces` flat — which is what a queue looks like and what a
slow database does not. `GetChannels` was worst because it also fanned out to every `IChannelGrain`
for realtime state while still holding the space's turn.

They now go to `ISpaceReadGrain`, a `[StatelessWorker]` over the shared `HybridCache`, and the writes
invalidate it by tag. Same box, same run, p50 time to first screen:

| clients | before | after |
|---:|---:|---:|
| 5 | 137 | 45 |
| 25 | 622 | 105 |
| 50 | 836 | 230 |
| 150 | 3969 | 1080 |

Measure a warm process. The first run after a start pays JIT, EF query-plan compilation and the
first Redis connections, and reads eight to ten times worse than the next one — it is not the number
to compare against anything. It is, however, the number a real deployment serves right after a
restart, which is exactly when everybody arrives at once. Nothing warms the process today.

### What did not help

Two things were tried against the remaining time at 150 clients and neither moved it. Both are
recorded because the next person will think of them too.

**A bigger worker pool.** `[StatelessWorker(256)]` instead of the default `ProcessorCount` failed
every call. Orleans reported one activation executing a single request for 26 seconds with 276
queued behind it, and Npgsql out of connections. The bound is not the bottleneck — it is the thing
stopping 150 simultaneous cache misses from becoming 150 simultaneous database connections.

**Removing the last per-caller query.** The caller's own membership row was a database read per
distinct arriving user; it now comes from joining the cached roster with the cached archetype list,
so a storm makes no per-caller query at all. Measured at 150 clients: no change, inside run-to-run
noise. Worth keeping anyway — it is what made the oversized pool fail — but it is not where the time
goes.

### Where the time actually goes

`GetServerArchetypes` answers in 3-5 ms under the same 150 concurrent clients, from a cache, on a
single ordinary activation. `GetChannelGroups` answers the same way from the same cache and takes
800 ms. The difference is not the grain and not the transport; both are idle by then.

What scales is the payload. Every arriving client is sent the entire roster, so the run serialises
`clients × members` records — 2,500 at 50 clients and 22,500 at 150. `GetMembers` p50 goes 88 ms to
510 ms across that step, a factor of 5.8 where the client count grew 3× and the record count 9×. The
three space calls converge to the same latency because they are issued together and finish when the
process is done encoding.

That is a protocol question rather than a caching one: the client keeps its state in IndexedDB and
is fed by the event stream, so shipping the full roster on every login is a choice, not a
requirement.

### The versioned bootstrap

`GetSpaceSnapshot` takes the versions the caller already holds and returns only the parts that moved,
which for a client signing in again is usually none of them. `--mode` picks which bootstrap the
scenario runs, so the three are measurable side by side:

| mode | what it models | p50 first screen, 150 clients |
|---|---|---:|
| `legacy` | four calls per space, as shipped clients make them | 1210 |
| `snapshot` | one versioned call, client holding nothing | 740 |
| `returning` | one versioned call, client holding the space already | 59 |

`returning` is the case a real client is in every time after the first. Two things get it there: the
snapshot sends nothing and skips the per-channel realtime fan-out entirely, and presence — which had
become the whole remaining cost once the roster stopped being re-encoded — is held for one second, so
a crowd arriving together reads it once rather than once each.

Run the modes interleaved, several rounds each. The first run after a restart is inflated by JIT no
matter which mode it is, and comparing a cold run of one against a warm run of another will tell you
whichever answer you were hoping for.

Every number in this file up to and including the tables above was re-measured after the fixture
started creating channels. Creating a space does not create any — `ServerRepository.CreateAsync`
writes the space, its owner and its archetypes and stops — so until the fixture made them, every run
had measured `GetChannels` against an empty list, which is a query returning nothing and fanning out
to nobody. The correction moved `snapshot` and left the other two roughly where they were: the
roster, not the channels, is what a cold bootstrap pays for.

## Scenario B — fan-out

N clients hold a live hub connection to one space and one of them sends. `DELIVERY` is one sample
per (message, recipient) — send-to-arrival across `ChannelGrain` → `AppHubServer.BroadcastChannel` →
the Redis backplane → every connected member.

```
dotnet run --project bench/ArgonLoad -- --scenario fanout --clients 150 --messages 20
```

| listeners | samples | SendMessage p50 | DELIVERY p50 | DELIVERY p99 |
|---:|---:|---:|---:|---:|
| 25 | 500 | 8.6 | 8.0 | 15.9 |
| 50 | 1000 | 7.8 | 7.3 | 16.6 |
| 150 | 3000 | 7.9 | 7.3 | 19.3 |

Flat. Six times the room for the same delivery time, and delivery lands slightly *before* the
sender's own call returns — the backplane has already pushed by the time the RPC replies. Nothing
here needs work, which is worth knowing precisely because scenario A found the opposite about a path
that looked equally innocent.

### Throughput — the same scenario with `--rate`

`--rate` holds a total send rate across `--senders` for `--seconds`, each sender on its own clock.
The percentiles are the least interesting part of the output; the line to read is the achieved rate.

150 listeners, 600 msg/s offered, 20s:

| senders | achieved | SendMessage p50 |
|---:|---:|---:|
| 10 | 99 | 96 |
| 25 | 94 | 259 |
| 50 | 90 | 522 |

Throughput was pinned at **90–100 messages per second into one channel** and adding senders did not
move it — it only made each sender wait longer, close to linearly. That is the signature of a single
server-side queue, and here the queue is correct: `ChannelGrain` is turn-based because that is what
orders messages and assigns their ids. The ceiling is therefore the length of its turn.

Two guesses were wrong before the measurement was right, so both are recorded.

**Not the logging.** `SendMessage` writes five `LogInformation` lines per message, one of which
builds a joined string unconditionally — exactly what a hot-path mistake looks like. Silencing the
category gave 62 and 91 msg/s against 99 and 94 with it on: no improvement, inside the noise.

**It was the broadcast.** Timing the turn by phase under load gave medians of 0.93 ms for the
duplicate check, 2.09 ms for the insert, and **3.19 ms for the publish** — the one part whose result
the sender does not need, since the id comes from the insert. It now goes out after the turn instead
of inside it (`ChannelGrain.FireDetached`).

150 listeners, 600 msg/s offered, 20s:

| senders | publish awaited in the turn | detached |
|---:|---|---|
| 10 | 99 msg/s, send 96 ms, delivery 96 ms | **245 msg/s, send 37 ms, delivery 40 ms** |
| 25 | 94 msg/s, send 259 ms, delivery 258 ms | **270 msg/s, send 86 ms, delivery 89 ms** |
| 50 | 90 msg/s, send 522 ms | **274 msg/s, send 179 ms, delivery 182 ms** |

Roughly three times the ceiling, throughput rises with senders instead of standing still, and
delivery now tracks the send call to within a few milliseconds — there is no queue behind the turn
at all.

Two wrong turns on the way there, both worth knowing.

The publish was first put on an ordered chain, one link per activation, to keep two messages sent a
millisecond apart from reaching clients out of order. A few hundred milliseconds of drift between two
messages is fine — the sender knows its own sequence from the `randomId` it chose before the call,
and `messageId` is roughly time-ordered — so the chain bought nothing and became the next bottleneck:
same throughput, and delivery at saturation cost 333 ms against 89 ms without it.

One thing does not tolerate the drift, and it is not the ordering of messages. The desktop client's
cursor is a high-water mark, so a `broadcastSpace` arriving with a lower replay entry id than one
already seen is discarded outright and never replayed. That has to become a dedupe by id in the
client; until it does, drift here is occasional silent loss. The window was never zero — two channels
in one space have always published concurrently from separate activations — but it is wider now.

The chain's first version also used `TaskContinuationOptions.ExecuteSynchronously`, which runs the
continuation inline on the calling thread whenever the chain is idle — that is the grain's turn, so
the publish never moved and the phase still measured its full 3.2 ms.

What remains is a weaker promise: `SendMessage` returns once the message is stored, not once the
backplane has it, and a publish that fails afterwards is logged rather than surfaced. That is the
guarantee the mention and last-message updates beside it already had.

Per channel, not per space: ten busy channels are ten activations and ten times the ceiling.

### Past a thousand

Three hundred a second was still the wrong order of magnitude, and the remaining turn was 0.93 ms of
duplicate-check round trip and 1.87 ms of insert. Three changes, each measured on its own:

**The insert leaves the turn.** Ids come from the snowflake generator instead of the `unique_rowid()`
column default — waiting for the database to say what the id is was the reason the insert had to be
awaited — and rows go into `MessageWriteBuffer`, which commits them in batches of up to 256 or every
5 ms, whichever comes first. A single-row insert into CockroachDB costs the same as a two-hundred-row
one. 270 → 650 msg/s.

**The duplicate check leaves the hot path.** An activation is the only writer for its channel, so
what it remembers is the whole truth about what it accepted; the shared cache only covers an
activation that moved, and only until its entries expire. `ChannelGrain` now answers from memory and
consults the cache for the first two minutes after activation. A channel that has been live longer
than that — which is every channel in production and none in this bench — pays nothing. 650 → 936.

**The logging leaves.** `SendMessage` wrote five lines per message, one of them a `LogWarning` on
every message that had no entities. This was measured at 95 msg/s earlier in this file and dismissed;
that measurement was simply too far below the ceiling to see it. At a thousand a second it was the
largest single cost left. 936 → ~1500.

| | send path | delivery to 150 listeners |
|---|---:|---:|
| before | 95 msg/s | 96 ms |
| after | **1000–1500 msg/s** | 23 ms p50 at 600 msg/s, sustained 2 min |

**What the send number does not mean.** 150 listeners at 1400 msg/s is 210,000 deliveries a second,
and the fan-out does not sustain that: a three-minute run at that rate ended with delivery 7.4 s
behind. At 600 msg/s — 90,000 deliveries a second — a two-minute run held 22.7 ms p50 and 47 ms p99
with no backlog. Writes are no longer the limit for a room this size; delivery is, somewhere between
600 and 1400.

### Keeping the guarantee, and capping the result

Returning before the commit was measured and then given up. It is faster — 1500 msg/s against 340 —
but it means a silo lost inside the window has swallowed messages it acknowledged, and the
integration suite said so immediately: three delete-message tests failed with `MESSAGE_NOT_FOUND`,
because the row was not there yet. Senders now wait for their batch. The batching still pays: a
hundred senders share one round trip instead of buying one each.

Waiting exposed a mistake in the batcher. It gathered rows for a 5 ms window before committing, which
taxes a quiet channel the full window per message — one channel measured 115 msg/s. The window is
gone: each pass commits whatever is queued, and the batch grows on its own, because everything that
arrives while one insert is in flight is picked up by the next. Self-tuning, and 340 msg/s for a
single channel.

| | one channel | node, 200 channels |
|---|---:|---:|
| before any of this | 95 msg/s | — |
| batched, durable, 1 writer | 340 | 786 |
| 8 writers | 340 | 1626 |
| 24 writers | 340 | 3264 |

A channel is one grain ordering its own messages, so writers do not raise it — they raise what the
node can take across many channels at once. Past twenty-four the delivery path gives out first: every
message also appends to the space's replay stream, and at 3264 msg/s the listeners were eight seconds
behind with deliveries dropped.

`Messages:PerChannelPerSecond` caps a channel at 200 a second by default — well under the 340 it can
do, and far above anything a room of people types. Measured with it on: 199.9 achieved, the rest
refused. `Messages:WriteConcurrency` is the writer count. The suites set the cap to 0, because
sending as fast as possible is exactly what they do and exactly what it refuses.

### Reading the output

`TIME TO FIRST SCREEN` is the number a person feels. The per-call rows say where it went.

If `GetMembers` / `GetChannels` / `GetChannelGroups` grow at p99 with the client count while
`GetSpaces` stays flat, that is the grain queue and not the database. If all of them grow together,
look at the database or the connection pool instead.

Run it twice — once against `--role dev` (everything in one process) and once against a distributed
deployment — and the difference is what the role split costs on the path a person waits on. That
number has never been measured; the split was argued from a static call graph.

## Preparing

The scenario seeds itself: it registers an owner, creates a space, mints an invite, then registers
and joins one user per client before the barrier releases. Registration is itself load, so it is
done ahead of the measured window and at limited concurrency.

A run leaves its users and its space behind. Point it at a throwaway database.

## Scenario C — signup rush

N clients create an account at the same moment. Registration is the one request that spends real CPU
rather than waiting on something: it hashes a password, and a password hash is expensive on purpose.

```
dotnet run --project bench/ArgonLoad -- --scenario signup --clients 400
```

| hashing | registrations/s | p50 |
|---|---:|---:|
| unsalted SHA-256, one pass | 450 | 306 ms |
| PBKDF2-HMAC-SHA-512, 210k iterations | 156 | 1808 ms |

Three times fewer, and that is the change working rather than failing — the old scheme was fast
because it did nothing. What the number is for is choosing `auth:passwordHashing:Iterations`: it is
the one setting that trades login capacity for the cost of guessing a stolen digest, and it should be
revisited on new hardware rather than left where it was set.

Ignore the first run after a restart, as everywhere else in this file: 100 clients measured 36/s cold
against 156/s warm.
