namespace Argon.Bus;

using NATS.Client.JetStream;
using StackExchange.Redis;
using System.Diagnostics;

public static class Scenarios
{
    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // hop — what the event costs between the silo that raised it and the client that reads it.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    public static async Task HopAsync(RunOptions options, CancellationToken ct)
    {
        Console.WriteLine(options.Rate > 0 || options.Saturate
            ? $"hop — {options.Nodes} node(s), {options.Clients} client(s), {options.Groups} group(s), " +
              $"{options.Senders} sender(s) {(options.Saturate ? "as fast as they can" : $"at {options.Rate:0.#}/s")} " +
              $"for {options.Seconds}s, {options.Payload}B events"
            : $"hop — {options.Nodes} node(s), {options.Clients} client(s), {options.Groups} group(s), " +
              $"{options.Messages} messages one at a time, {options.Payload}B events");

        var port = options.BasePort;

        foreach (var carrier in options.Carriers)
        {
            await PrepareAsync(carrier, options, ct);

            // Local means publisher and subscriber are the same node by definition; running it over
            // several would silently measure something else.
            var nodeCount = carrier == Carrier.Local ? 1 : options.Nodes;
            var nodes     = new EntryNode[nodeCount];

            for (var i = 0; i < nodeCount; i++)
                nodes[i] = await EntryNode.StartAsync(port++, carrier, options, ct);

            await using var publisher = await SiloNode.CreateAsync(carrier, options, nodes[0], ct);

            var publish  = new Measurement("publish (what the grain waits for)");
            var delivery = new Measurement("delivery (raised → on the client)");

            long delivered = 0;
            var  listeners = new List<Listener>(options.Clients);

            for (var i = 0; i < options.Clients; i++)
            {
                var group = $"g{i % options.Groups}";
                listeners.Add(await Listener.ConnectAsync(nodes[i % nodeCount], group, options, payload =>
                {
                    delivery.Record(Probe.Age(payload.Span));
                    Interlocked.Increment(ref delivered);
                }, ct));
            }

            // Nothing here is warm on the first event: the JIT has not run the publish path, the
            // carrier has not opened its connections, and Kestrel has not touched the sockets. A
            // handful of throwaway events buys a number that describes the steady state instead of
            // the first second of it.
            for (var i = 0; i < 20; i++)
                await publisher.PublishAsync("g0", Probe.Create(-1, options.Payload), ct);

            await Task.Delay(500, ct);
            Interlocked.Exchange(ref delivered, 0);
            publish  = new Measurement("publish (what the grain waits for)");
            delivery = new Measurement("delivery (raised → on the client)");

            long sent    = 0;
            var  started = Stopwatch.GetTimestamp();

            if (options.Rate > 0 || options.Saturate)
                sent = await SustainAsync(publisher, publish, options, ct);
            else
                for (var i = 0; i < options.Messages; i++, sent++)
                {
                    var probe   = Probe.Create(i, options.Payload);
                    var stopped = Stopwatch.GetTimestamp();
                    await publisher.PublishAsync("g0", probe, ct);
                    publish.Record(Stopwatch.GetElapsedTime(stopped));

                    // One at a time and idle in between: this row is the latency of an empty path,
                    // not of a queue.
                    await Task.Delay(5, ct);
                }

            var elapsed = Stopwatch.GetElapsedTime(started);

            // Only listeners in g0 hear the probes, which is every listener when --groups is 1.
            var perMessage = options.Rate > 0 || options.Saturate
                ? options.Clients / (double)options.Groups
                : Math.Ceiling(options.Clients / (double)options.Groups);
            var expected   = (long)(sent * perMessage);

            var deadline = Stopwatch.GetTimestamp();
            while (Interlocked.Read(ref delivered) < expected && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(10))
                await Task.Delay(50, ct);

            var lost = expected - Interlocked.Read(ref delivered);

            Report.Print($"{Name(carrier)} — {sent} events, {Interlocked.Read(ref delivered)} deliveries",
                elapsed, [publish, delivery]);

            if (options.Saturate)
                Console.WriteLine($"achieved {sent / elapsed.TotalSeconds:0.#} events/s with {options.Senders} concurrent senders");
            else if (options.Rate > 0)
                Console.WriteLine($"offered {options.Rate:0.#} events/s, achieved {sent / elapsed.TotalSeconds:0.#} events/s");
            if (lost > 0)
                Console.WriteLine($"{lost} of {expected} deliveries never arrived — the row above describes only the ones that did");

            foreach (var listener in listeners)
                await listener.DisposeAsync();
            foreach (var node in nodes)
                await node.DisposeAsync();

            // The carriers share Redis and NATS. Overlapping their teardown with the next one's
            // measurement is the easiest way to blame one option for another's shutdown.
            await Task.Delay(1000, ct);
        }
    }

    /// <summary>Every sender on its own clock, which is how a roomful of people behaves.</summary>
    private static async Task<long> SustainAsync(IBusPublisher publisher, Measurement publish, RunOptions options, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(options.Seconds));

        long sent = 0;

        // Windows timer resolution is about fifteen milliseconds, so a PeriodicTimer cannot hold a
        // rate above roughly sixty a second per sender and silently holds a slower one instead.
        // Above that the only honest question is the open-loop one: send as fast as the carrier
        // accepts and report what came out.
        var interval = options.Saturate
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(options.Senders / options.Rate);

        if (!options.Saturate && interval < TimeSpan.FromMilliseconds(1))
            throw new ArgumentException(
                $"{options.Rate:0.#}/s across {options.Senders} senders is {interval.TotalMilliseconds:F2} ms apart, " +
                "which no timer on this platform will hold. Use --saturate, or more senders.");

        await Task.WhenAll(Enumerable.Range(0, options.Senders).Select(async index =>
        {
            using var ticks = options.Saturate ? null : new PeriodicTimer(interval);
            var       probe = 0L;

            try
            {
                while (ticks is null ? !window.Token.IsCancellationRequested : await ticks.WaitForNextTickAsync(window.Token))
                {
                    var payload = Probe.Create(index * 1_000_000 + probe++, options.Payload);
                    var started = Stopwatch.GetTimestamp();
                    await publisher.PublishAsync("g0", payload, window.Token);
                    publish.Record(Stopwatch.GetElapsedTime(started));
                    Interlocked.Increment(ref sent);
                }
            }
            catch (OperationCanceledException)
            {
                // The window closed. That is how the run ends, not a failure.
            }
        }));

        return Interlocked.Read(ref sent);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // publish — the write side alone, with nothing listening.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What one silo can hand the carrier, with no entry node, no hub and no websocket in the
    /// process to compete for its CPU.
    /// </summary>
    /// <remarks>
    /// The hop scenario answers a different question and answers it honestly, but it runs the
    /// publisher, both entry nodes and every listener in one process, so a low number there could be
    /// the carrier or could be the bench. This one removes everything but the publisher. If the two
    /// agree, the ceiling is the carrier.
    /// </remarks>
    public static async Task PublishAsync(RunOptions options, CancellationToken ct)
    {
        Console.WriteLine($"publish — {options.Senders} concurrent senders, {options.Payload}B events, " +
                          $"{options.Seconds}s, nothing listening");
        Console.WriteLine();
        Console.WriteLine($"{"carrier",-28}{"events/s",12}{"p50",10}{"p95",10}{"p99",10}");
        Console.WriteLine(new string('-', 70));

        foreach (var carrier in options.Carriers)
        {
            if (carrier == Carrier.Local)
                continue;

            await PrepareAsync(carrier, options, ct);

            var publisher = StormPublisher(carrier == Carrier.SignalR ? Carrier.RedisPubSub : carrier, options);
            var latency   = new Measurement("publish");

            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(TimeSpan.FromSeconds(options.Seconds));

            for (var i = 0; i < 50; i++)
                await publisher.PublishAsync("g0", Probe.Create(-1, options.Payload), ct);

            long sent    = 0;
            var  started = Stopwatch.GetTimestamp();

            await Task.WhenAll(Enumerable.Range(0, options.Senders).Select(async _ =>
            {
                try
                {
                    while (!window.Token.IsCancellationRequested)
                    {
                        var at = Stopwatch.GetTimestamp();
                        await publisher.PublishAsync("g0", Probe.Create(sent, options.Payload), window.Token);
                        latency.Record(Stopwatch.GetElapsedTime(at));
                        Interlocked.Increment(ref sent);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The window closed.
                }
            }));

            var elapsed  = Stopwatch.GetElapsedTime(started);
            var snapshot = latency.Take();

            Console.WriteLine($"{Name(carrier),-28}{sent / elapsed.TotalSeconds,12:F0}" +
                              $"{snapshot.P50,9:F2}ms{snapshot.P95,9:F2}ms{snapshot.P99,9:F2}ms");

            await publisher.DisposeAsync();
            await Task.Delay(1000, ct);
        }

        Console.WriteLine(new string('-', 70));
        Console.WriteLine(
            "The signalr row is raw Redis pub/sub: SignalR's own publish is the same PUBLISH with a\n" +
            "frame around it, and the hop scenario shows the two within noise of each other.\n\n" +
            "Only the two Redis rows and jetstream are round trips. A core NATS publish is a write\n" +
            "into a buffer the client flushes on its own schedule, so its number is what one process\n" +
            "can hand the client, not what the server confirmed.");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // storm — what an entry node pays for the subscriptions of everyone connected to it.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The axis the hop scenario cannot reach.
    /// </summary>
    /// <remarks>
    /// <para>A hop measured with fifty websockets says nothing about an entry node holding fifty
    /// thousand, and the difference between these carriers is almost entirely in what that costs.
    /// So this one drops SignalR and the sockets and asks the carrier directly: hold N
    /// subscriptions, then deliver.</para>
    ///
    /// <para>The Redis row is raw pub/sub over the channel names the backplane uses, not the
    /// backplane itself. That is the mechanism under it with SignalR's framing removed, which makes
    /// it a lower bound for what the backplane does rather than an estimate of it.</para>
    /// </remarks>
    public static async Task StormAsync(RunOptions options, CancellationToken ct)
    {
        Console.WriteLine($"storm — {options.Subs} subscriptions per node, {options.Payload}B events");
        Console.WriteLine();
        Console.WriteLine($"{"carrier",-24}{"subscribe",12}{"per sub",10}{"deliver p50",13}{"p99",9}{"lost",7}");
        Console.WriteLine(new string('-', 75));

        foreach (var carrier in options.Carriers)
        {
            if (carrier is Carrier.Local or Carrier.RedisPubSub)
                continue;

            await PrepareAsync(carrier, options, ct);

            var receiver  = StormReceiver(carrier, options);
            var publisher = StormPublisher(carrier, options);

            if (publisher is NatsJetStreamCarrier prepared)
                await prepared.EnsureStreamAsync(ct);

            var latency   = new Measurement("deliver");
            long arrived  = 0;

            await receiver.StartAsync((_, payload) =>
            {
                latency.Record(Probe.Age(payload.Span));
                Interlocked.Increment(ref arrived);
                return ValueTask.CompletedTask;
            }, ct);

            // Concurrently, because that is how clients arrive. One at a time would measure the
            // round-trip latency of a subscribe and call it the cost of holding subscriptions, which
            // is the difference between "1.4 ms each" and what a node actually waits.
            var started = Stopwatch.GetTimestamp();
            await Parallel.ForAsync(0, options.Subs,
                new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct },
                async (i, token) => await receiver.JoinAsync($"g{i}", token));
            var subscribing = Stopwatch.GetElapsedTime(started);

            // Warm the publish path at this cardinality before measuring it.
            for (var i = 0; i < 20; i++)
                await publisher.PublishAsync("g0", Probe.Create(-1, options.Payload), ct);
            await Task.Delay(500, ct);
            Interlocked.Exchange(ref arrived, 0);
            latency = new Measurement("deliver");

            const int probes = 300;
            for (var i = 0; i < probes; i++)
            {
                await publisher.PublishAsync($"g{i % Math.Min(options.Subs, 64)}", Probe.Create(i, options.Payload), ct);
                await Task.Delay(5, ct);
            }

            var deadline = Stopwatch.GetTimestamp();
            while (Interlocked.Read(ref arrived) < probes && Stopwatch.GetElapsedTime(deadline) < TimeSpan.FromSeconds(10))
                await Task.Delay(50, ct);

            var snapshot = latency.Take();

            Console.WriteLine($"{Name(carrier),-24}{subscribing.TotalMilliseconds,11:F0}ms" +
                              $"{subscribing.TotalMilliseconds * 1000 / Math.Max(options.Subs, 1),9:F1}µs" +
                              $"{snapshot.P50,12:F2}ms{snapshot.P99,8:F2}ms{probes - Interlocked.Read(ref arrived),7}");

            await receiver.DisposeAsync();
            await publisher.DisposeAsync();
            await Task.Delay(1000, ct);
        }

        Console.WriteLine(new string('-', 75));
        Console.WriteLine("""
            'subscribe' is the whole cost of holding those subscriptions, which for the stream
            carriers is a hash-set insert and no network call at all — they pay on the other side
            instead, by reading every event in the cluster on every node.

            SignalR needs more of them than this row shows: the backplane subscribes one channel per
            connection and one per user on top of one per group, so an entry node holding N clients
            issues roughly 2N + groups subscriptions, not one per group.
            """);
    }

    private static IBusReceiver StormReceiver(Carrier carrier, RunOptions options) => carrier switch
    {
        Carrier.SignalR or
        Carrier.RedisPubSub   => new RedisPubSubCarrier(options.Redis, "bench-bus"),
        Carrier.RedisStreams  => new RedisStreamsCarrier(options.Redis, options.Shards, options.StreamMaxLen),
        Carrier.NatsCore      => new NatsCoreCarrier(options.Nats),
        Carrier.NatsJetStream => new NatsJetStreamCarrier(options.Nats, options.JetStreamName),
        _                     => throw new ArgumentOutOfRangeException(nameof(carrier))
    };

    private static IBusPublisher StormPublisher(Carrier carrier, RunOptions options) => carrier switch
    {
        Carrier.SignalR or
        Carrier.RedisPubSub   => new RedisPubSubCarrier(options.Redis, "bench-bus"),
        Carrier.RedisStreams  => new RedisStreamsCarrier(options.Redis, options.Shards, options.StreamMaxLen),
        Carrier.NatsCore      => new NatsCoreCarrier(options.Nats),
        Carrier.NatsJetStream => new NatsJetStreamCarrier(options.Nats, options.JetStreamName),
        _                     => throw new ArgumentOutOfRangeException(nameof(carrier))
    };

    // ─────────────────────────────────────────────────────────────────────────────────────────────
    // wire — how many bytes each option puts on the network for one event.
    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the backplane actually ships, measured rather than reasoned about.
    /// </summary>
    /// <remarks>
    /// SignalR serialises the invocation once per registered hub protocol and puts every version of
    /// it into the backplane frame, so the frame carries the message as many times as there are
    /// protocols. With the JSON protocol a <c>byte[]</c> argument is base64, which the two stream
    /// carriers do not do because they carry bytes.
    /// </remarks>
    public static async Task WireAsync(RunOptions options, CancellationToken ct)
    {
        Console.WriteLine($"wire — one {options.Payload}B event, {(options.MessagePack ? "json + messagepack" : "json only")}");
        Console.WriteLine();

        await using var redis = await ConnectionMultiplexer.ConnectAsync(options.Redis);

        // By pattern rather than by name: the backplane's channel is not the configured prefix, it
        // is the prefix with the hub's full type name appended, and printing what it actually
        // subscribed to is worth more than asserting what it should have been.
        var seen    = new TaskCompletionSource<(string Channel, int Bytes)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pattern = new RedisChannel("bench-bus*", RedisChannel.PatternMode.Pattern);

        await redis.GetSubscriber().SubscribeAsync(pattern,
            (channel, value) => seen.TrySetResult((channel.ToString(), ((byte[])value!).Length)));

        var node = await EntryNode.StartAsync(options.BasePort + 900, Carrier.SignalR, options, ct);
        await using var publisher = await SiloNode.CreateAsync(Carrier.SignalR, options, node, ct);

        await publisher.PublishAsync("g0", Probe.Create(1, options.Payload), ct);

        var observed = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

        await node.DisposeAsync();

        Console.WriteLine($"backplane channel: {observed.Channel}");
        Console.WriteLine();
        Console.WriteLine($"{"carrier",-34}{"bytes per event",18}{"overhead",12}");
        Console.WriteLine(new string('-', 64));
        Line("SignalR backplane frame", observed.Bytes, options.Payload);
        Line("Redis stream entry (XADD value)", options.Payload, options.Payload);
        Line("NATS message body", options.Payload, options.Payload);
        Console.WriteLine(new string('-', 64));
        Console.WriteLine("""
            The stream rows are the payload itself: both carry bytes, so what goes on the wire is
            what was handed to them plus the protocol's own framing, which is tens of bytes.

            The backplane row is the whole frame SignalR publishes, which contains the invocation
            pre-serialised once per registered hub protocol. Run this with and without --msgpack:
            registering a second protocol does not replace the first, it adds a copy.
            """);

        return;

        static void Line(string name, int bytes, int payload)
            => Console.WriteLine($"{name,-34}{bytes,18}{(double)bytes / payload,11:F2}x");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Leaves nothing of the previous carrier's run behind for this one to read.</summary>
    private static async Task PrepareAsync(Carrier carrier, RunOptions options, CancellationToken ct)
    {
        switch (carrier)
        {
            case Carrier.RedisStreams:
            {
                await using var redis = await ConnectionMultiplexer.ConnectAsync(options.Redis);
                var             db    = redis.GetDatabase();
                for (var shard = 0; shard < options.Shards; shard++)
                    await db.KeyDeleteAsync($"bench:rt:{shard}");
                break;
            }

            case Carrier.NatsJetStream:
            {
                var carrierInstance = new NatsJetStreamCarrier(options.Nats, options.JetStreamName);
                await carrierInstance.DeleteStreamAsync(ct);
                await carrierInstance.EnsureStreamAsync(ct);
                await carrierInstance.DisposeAsync();
                break;
            }
        }
    }

    public static string Name(Carrier carrier) => carrier switch
    {
        Carrier.Local         => "local (no hop at all)",
        Carrier.SignalR       => "signalr redis backplane",
        Carrier.RedisPubSub   => "redis pub/sub, no signalr",
        Carrier.RedisStreams  => "redis streams",
        Carrier.NatsCore      => "nats core",
        Carrier.NatsJetStream => "nats jetstream",
        _                     => carrier.ToString()
    };
}
