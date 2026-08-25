# argon-bus

What it costs to move an event from the silo that raised it to the node holding the client's socket.

```
dotnet run --project bench/ArgonBus -c Release -- --scenario hop --carrier signalr
```

Not a test project: it needs Redis and NATS to be up and it is pointed at them by hand. Nothing here
is collected by `dotnet test` or gated in CI.

## The question

`AppHubServer` publishes through `IHubContext<AppHub>`, so a grain on a `core` silo raising a
`MessageSent` hands it to SignalR's Redis backplane, and whichever entry node holds the recipient's
websocket delivers it. That decision was never measured. The alternatives — Redis streams as the
carrier, or NATS, which is already deployed for the Bot API — were never measured either.

## Method

Four scenarios, each isolating one thing:

| scenario | what it answers |
|---|---|
| `hop` | end to end: `PublishAsync` on a silo → a real websocket client, through a real hub |
| `publish` | the write side alone, with no entry node and nothing listening in the process |
| `storm` | what an entry node pays to hold the subscriptions of everyone connected to it |
| `wire` | bytes on the network for one event |

Everything runs in one process, so both sides of every subtraction read the same clock and no skew is
involved. That is also why the absolute numbers are a floor: there is no network between the silo and
the entry node here, and a real deployment has one.

The client leg is identical in every row by construction — the entry node runs the same hub and the
same websocket whichever carrier feeds it. `local`, where the publisher *is* the node holding the
client, is the control that says how much of a number is the leg rather than the hop: **0.28 ms**.

Five carriers. `signalr` is what ships. `pubsub` is Redis pub/sub addressed exactly the way the
backplane addresses it, with SignalR's implementation removed — the control that says which of the
two costs what. `streams` is sharded Redis streams read with a blocking `XREAD`. `nats` is core NATS,
one subject per group. `jetstream` is one JetStream stream, subject-filtered, consumed in full by
every node.

Deliberately **not** the shape the deleted `NatsContext` used. That one created a JetStream stream
per space and a durable consumer per client connection, with `MaxAckPending = 3` — three
unacknowledged messages and the consumer stalls — and deleted the consumer on disconnect. It lost
messages because that design loses messages.

## What it found: the carrier is not the problem

Publish throughput, 128 concurrent senders, 512-byte events, nothing else changed but the server the
Redis carriers point at:

| carrier | Redis 7.4 | Dragonfly 1.38 |
|---|---:|---:|
| signalr redis backplane | **86 756/s** | **1 622/s** |
| redis pub/sub, no signalr | 92 135/s | 1 652/s |
| redis streams | 79 848/s | 2 250/s |
| nats core | 151 445/s | — |
| nats jetstream | 65 688/s | — |

Fifty times. The dev stack runs Dragonfly (`deploy/docker-compose.local.yml`), and on it the shipped
path tops out at about **1 600 events a second** with publishes taking 77 ms at that depth. On Redis
it is 87 000 and 1 ms.

`pubsub` matching `signalr` to within noise is what rules SignalR out as the cause: the backplane's
publish is that `PUBLISH` with a frame around it, and the frame costs nothing measurable.

### It is not .NET either

`redis-benchmark`, inside each container, one connection, pipeline depth 16, one subscriber attached
to the channel:

| server | PUBLISH/s |
|---|---:|
| Redis 7.4 | **198 020** |
| Dragonfly 1.38 | **1 805** |

No StackExchange.Redis, no SignalR, no Argon. Dragonfly stops pipelining `PUBLISH` once a subscriber
exists — 1 805/s is one round trip per publish, at depth 16. Without a subscriber it pipelines
normally (282 000 `SET`/s), and across 128 *separate* connections it publishes at 37 000/s, which is
why nothing that opens a connection per operation would ever notice. Every .NET Redis client
multiplexes onto one connection. That is the whole of it.

The same defect shows up in the subscription path. Twenty thousand subscriptions on one node:

| carrier | to establish | per subscription | probes lost |
|---|---:|---:|---:|
| redis pub/sub, Redis 7.4 | 472 ms | 23.6 µs | 0 of 300 |
| nats core | 618 ms | 30.9 µs | 0 of 300 |
| redis pub/sub, Dragonfly | 21 600–24 800 ms | 1 100–1 240 µs | 87–216 of 300 |
| redis streams / jetstream | ~10 ms | — | 0 of 300 |

Not just slow on Dragonfly — **lossy**, reproducibly, with no connection failure reported by the
client. An entry node holding fifty thousand clients needs upwards of a hundred thousand
subscriptions, because the backplane subscribes one channel per connection and one per user on top of
one per group. On Redis that is two and a half seconds. On Dragonfly it is not survivable.

The stream carriers hold no subscriptions at all — a join is a hash-set insert — and pay on the other
side instead, by reading every event in the cluster on every node and discarding what they do not
hold.

## What the carriers actually cost

On Redis 7.4, so the comparison is about the carriers rather than about the server. Two entry nodes,
twenty websocket clients, 512-byte events, one at a time with the path idle:

| carrier | publish p50 | delivery p50 | delivery p99 |
|---|---:|---:|---:|
| local (no hop at all) | 0.07 | 0.28 | 0.46 |
| signalr redis backplane | 1.01 | 1.32 | 1.98 |
| redis pub/sub, no signalr | 0.92 | 1.19 | 1.72 |
| redis streams | 1.01 | 1.32 | 2.31 |
| nats core | **0.03** | 1.19 | **17.70** |
| nats jetstream | 0.84 | **1.08** | 1.95 |

Every carrier lands between 1.1 and 1.4 ms and the hop is essentially all of it — the client leg is
0.28. On latency there is nothing to choose between them.

Two rows are not what they look like. A core NATS publish returns in 0.03 ms because it returns
before the server has it: the client writes into a buffer it flushes on its own schedule, which is
also where its 17.7 ms p99 comes from — reproduced across separate runs, and about the size of a
Windows timer tick. Choosing core NATS means fixing that first. JetStream's 0.84 ms, by contrast, is
a publish the server acknowledged, and it still delivers fastest of anything measured.

### Bytes on the wire

One 512-byte event:

| carrier | bytes | overhead |
|---|---:|---:|
| signalr backplane frame, JSON protocol | 736 | 1.44× |
| signalr backplane frame, JSON + MessagePack | 1 276 | 2.49× |
| redis stream entry / NATS body | 512 | 1.00× |

A `byte[]` argument is base64 under the JSON hub protocol, and the backplane frame carries the
invocation pre-serialised **once per registered hub protocol**. Registering MessagePack does not
replace JSON, it adds a copy: `AddMessagePackProtocol()` for the clients' benefit would put 2.5× the
payload through the backplane unless JSON is removed at the same time. The package is already
referenced and the call is commented out in `AddRealtimeBus` — worth knowing before someone
uncomments it.

## What this means

**The backplane decision is sound.** It is the only option where the registry of who is in which
space and which channel is not something Argon has to build, hold correct across reconnects, and keep
in step with `Groups.AddToGroupAsync`. On a Redis that pipelines, it does 87 000 events a second at
1.3 ms, which is two orders of magnitude above what a node produces — `bench/ArgonLoad` puts the write
path at 1 000–1 500 messages a second.

**The Redis under it is not.** On Dragonfly the same path does 1 600 a second, which is *below* the
write path, and the fan-out backlog `ArgonLoad` recorded at 3 264 msg/s is exactly what that looks
like from the other end. Nothing else in this file matters until that is settled: either the
`Backplane` profile points at Redis rather than Dragonfly, or production is confirmed not to be
Dragonfly. `Redis:Backplane:ConnectionString` is its own profile precisely so it can be moved on its
own.

**One thing is worth changing regardless of the carrier.** Every space-scoped event is written twice:
`AppHubServer.BroadcastSpace` appends it to the replay stream with `XADD` and then publishes it over
the backplane, two round trips to the same server for one event. Redis streams as the carrier collapse
those into one and make delivery at-least-once instead of at-most-once, which is what the replay log
exists to paper over. That is an incremental change to `AppHubServer` — the streams and the cursors
are already there — and it costs 8% of the publish throughput. It is not a rewrite and it is not a
throughput win; it is one write instead of two and one mechanism instead of two.

**NATS is not worth a rework on these numbers.** It wins on throughput and ties on everything else,
it is already deployed, and none of that pays for building the group registry by hand. It becomes
interesting if entry nodes ever hold subscription counts where 23.6 µs each stops being free, and
that is a long way off.

## Caveats

- One box, one process, no network between the silo and the entry node. Every number is a floor.
- Dragonfly and NATS run in Docker Desktop on Windows; the Redis 7.4 comparison was run the same way,
  so the two Redis columns are comparable to each other.
- JetStream here is memory-backed. File storage was not measured.
- The `storm` scenario replaces SignalR with raw pub/sub on the same channel names, because fifty
  thousand websockets do not fit in one process. It is therefore a floor for what the backplane does,
  not an estimate of it.
- Nothing here measures Orleans streams. They were ruled out before this bench existed.

## Reproducing

NATS and Dragonfly come from `deploy/docker-compose.local.yml`. Redis for the comparison:

```
docker run -d --rm --name argon-bench-redis -p 6380:6379 redis:7-alpine
```

```
argon-bus --scenario hop     --carrier signalr --redis 127.0.0.1:6380 --nodes 2 --clients 20
argon-bus --scenario hop     --carrier signalr --redis 127.0.0.1:6380 --senders 128 --saturate
argon-bus --scenario publish --senders 128 --seconds 10
argon-bus --scenario storm   --subs 20000
argon-bus --scenario wire    --payload 512 [--msgpack]
```

`--carrier` takes any of `local,signalr,pubsub,streams,nats,jetstream`, comma separated; it runs them
in turn against the same load. Give each concurrent run its own `--port`, and ignore the first run
after a build the same way `bench/ArgonLoad` does.
