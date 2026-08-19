namespace Argon.Bus;

using Microsoft.AspNetCore.SignalR;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using StackExchange.Redis;

/// <summary>Which carrier moves an event from the silo that raised it to the node holding the client.</summary>
public enum Carrier
{
    /// <summary>No carrier at all: publisher and subscriber are the same node. The control.</summary>
    Local,

    /// <summary>What Argon ships today — SignalR's Redis backplane, which is Redis pub/sub.</summary>
    SignalR,

    /// <summary>
    /// Redis pub/sub directly, one channel per group — the same mechanism the backplane uses, with
    /// SignalR's implementation of it taken away. The control that says which of the two is the cost.
    /// </summary>
    RedisPubSub,

    /// <summary>Redis streams, sharded, read with a blocking XREAD.</summary>
    RedisStreams,

    /// <summary>NATS core pub/sub, one subject per group.</summary>
    NatsCore,

    /// <summary>NATS JetStream, one stream, subject-filtered.</summary>
    NatsJetStream
}

/// <summary>The publishing half — what a grain calls when it raises an event.</summary>
public interface IBusPublisher : IAsyncDisposable
{
    ValueTask PublishAsync(string group, ReadOnlyMemory<byte> payload, CancellationToken ct);
}

/// <summary>
/// The receiving half — what an entry node runs to learn about events raised elsewhere.
/// </summary>
/// <remarks>
/// The SignalR backplane has no implementation here on purpose: its receiving half is the hub
/// lifetime manager, which delivers straight into the hub with nothing of ours in between. That
/// asymmetry is not a gap in the bench, it is the actual difference between the options — the other
/// three need a local registry of who is here and a loop to feed it, and building that is part of
/// what they cost.
/// </remarks>
public interface IBusReceiver : IAsyncDisposable
{
    /// <summary>This node now holds at least one client in <paramref name="group"/>.</summary>
    ValueTask JoinAsync(string group, CancellationToken ct);

    /// <summary>Starts the loop. <paramref name="deliver"/> hands the payload to the local hub.</summary>
    ValueTask StartAsync(Func<string, ReadOnlyMemory<byte>, ValueTask> deliver, CancellationToken ct);
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// SignalR over the Redis backplane, and its degenerate case: no backplane at all.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Publishes through <c>IHubContext</c>, which is exactly what <c>AppHubServer</c> does today.
/// </summary>
/// <remarks>
/// Given a hub context wired to the Redis backplane this is the shipped path; given one that is not,
/// it is the control that says how much of the measured time is SignalR and the websocket rather
/// than the hop between nodes.
/// </remarks>
public sealed class HubContextPublisher(IHubContext<BusHub> hub) : IBusPublisher
{
    public ValueTask PublishAsync(string group, ReadOnlyMemory<byte> payload, CancellationToken ct)
        => new(hub.Clients.Group(group).SendAsync(BusHub.Method, payload.ToArray(), cancellationToken: ct));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Redis streams.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Sharded streams: the group name picks one of a fixed set of streams, and every node reads all of
/// them and drops what it does not hold.
/// </summary>
/// <remarks>
/// <para>A stream per group would be the obvious shape and it does not work: a blocking <c>XREAD</c>
/// names its keys when it is issued, so every join and every leave would have to cancel the read and
/// reissue it with a different key set. On an entry node holding tens of thousands of clients across
/// thousands of spaces that is a permanent storm of reissued reads.</para>
///
/// <para>The price of sharding is the mirror image: every node reads every event in the cluster and
/// discards most of them. That is the axis on which this option loses to pub/sub, and it is a
/// bandwidth cost that grows with node count rather than with traffic.</para>
/// </remarks>
public sealed class RedisStreamsCarrier(string connectionString, int shards, int maxLen) : IBusPublisher, IBusReceiver
{
    private const string GroupField   = "g";
    private const string PayloadField = "p";

    private ConnectionMultiplexer? writer;
    private ConnectionMultiplexer? reader;
    private readonly HashSet<string> joined = [];
    private readonly Lock            gate   = new();
    private Task?                    pump;

    private static string Key(int shard) => $"bench:rt:{shard}";

    /// <summary>Stable across runs and across processes, unlike <c>string.GetHashCode</c>.</summary>
    private int ShardOf(string group)
    {
        uint hash = 2166136261;
        foreach (var c in group)
            hash = (hash ^ c) * 16777619;
        return (int)(hash % (uint)shards);
    }

    private async ValueTask<IDatabase> WriterAsync()
        => (writer ??= await ConnectionMultiplexer.ConnectAsync(connectionString)).GetDatabase();

    public async ValueTask PublishAsync(string group, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var db = await WriterAsync();
        await db.ExecuteAsync("XADD", Key(ShardOf(group)), "MAXLEN", "~", maxLen, "*",
            GroupField, group, PayloadField, (RedisValue)payload.ToArray());
    }

    public ValueTask JoinAsync(string group, CancellationToken ct)
    {
        // No network call. Which is the one thing this option is unambiguously better at: a client
        // arriving costs nothing on the server that carries the events.
        lock (gate)
            joined.Add(group);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(Func<string, ReadOnlyMemory<byte>, ValueTask> deliver, CancellationToken ct)
    {
        // Its own multiplexer, and it has to be. StackExchange.Redis multiplexes every command over
        // one connection, so a blocking XREAD parked on it stalls everything else queued behind it.
        var options = ConfigurationOptions.Parse(connectionString);
        options.AsyncTimeout = 30_000;
        options.SyncTimeout  = 30_000;
        reader = await ConnectionMultiplexer.ConnectAsync(options);

        var db  = reader.GetDatabase();
        var ids = new string[shards];

        // '$' is "only what arrives after this point". Starting at 0 would replay the whole retained
        // stream into a node that just came up, which is a different feature and a different bench.
        Array.Fill(ids, "$");

        pump = Task.Run(async () =>
        {
            // COUNT n BLOCK ms STREAMS <key per shard> <id per shard>
            var args = new object[5 + shards * 2];

            while (!ct.IsCancellationRequested)
            {
                var i = 0;
                args[i++] = "COUNT";
                args[i++] = 512;
                args[i++] = "BLOCK";
                args[i++] = 2000;
                args[i++] = "STREAMS";
                for (var s = 0; s < shards; s++)
                    args[i++] = Key(s);
                for (var s = 0; s < shards; s++)
                    args[i++] = ids[s];

                RedisResult result;
                try
                {
                    result = await db.ExecuteAsync("XREAD", args);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Loudly. A read loop that dies quietly reports a carrier that delivers nothing
                    // and looks like a finding rather than a bug in the bench.
                    Console.Error.WriteLine($"redis streams reader failed: {e.Message}");
                    throw;
                }

                if (result.IsNull)
                    continue;

                foreach (RedisResult perStream in (RedisResult[])result!)
                {
                    var pair    = (RedisResult[])perStream!;
                    var key     = (string)pair[0]!;
                    var shard   = int.Parse(key[(key.LastIndexOf(':') + 1)..]);
                    var entries = (RedisResult[])pair[1]!;

                    foreach (var entry in entries)
                    {
                        var parts  = (RedisResult[])entry!;
                        ids[shard] = (string)parts[0]!;

                        var fields = (RedisValue[])parts[1]!;
                        string? group   = null;
                        byte[]? payload = null;

                        for (var f = 0; f + 1 < fields.Length; f += 2)
                        {
                            if (fields[f] == GroupField)
                                group = fields[f + 1]!;
                            else if (fields[f] == PayloadField)
                                payload = fields[f + 1]!;
                        }

                        if (group is null || payload is null)
                            continue;

                        bool mine;
                        lock (gate)
                            mine = joined.Contains(group);

                        if (mine)
                            await deliver(group, payload);
                    }
                }
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (pump is not null)
            try { await pump; } catch { /* the run ended, which is how this loop stops */ }
        if (writer is not null) await writer.DisposeAsync();
        if (reader is not null) await reader.DisposeAsync();
    }
}

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// NATS.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Core NATS: one subject per group, and a node subscribes to exactly the groups it holds.
/// </summary>
/// <remarks>
/// The same shape as the Redis backplane — subject-addressed, at-most-once, no durability — with a
/// different implementation of "who wants this". Redis keeps a channel registry and a client list
/// per channel; NATS routes on a subject trie built for the case where the subject count is large
/// and mostly cold. That difference is the whole reason this option is in the bench, and it does not
/// show up at all until the subscription count is realistic.
/// </remarks>
public sealed class NatsCoreCarrier(string url) : IBusPublisher, IBusReceiver
{
    private NatsConnection?                                connection;
    private Func<string, ReadOnlyMemory<byte>, ValueTask>? sink;
    private CancellationToken                              token;
    private readonly List<Task>                            pumps  = [];
    private readonly HashSet<string>                       joined = [];
    private readonly Lock                                  gate   = new();

    public static string Subject(string group) => $"bench.rt.{group}";

    private async ValueTask<NatsConnection> ConnectionAsync()
    {
        if (connection is not null)
            return connection;
        connection = new NatsConnection(new NatsOpts { Url = url, Name = "argon-bus" });
        await connection.ConnectAsync();
        return connection;
    }

    public async ValueTask PublishAsync(string group, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var nats = await ConnectionAsync();
        await nats.PublishAsync(Subject(group), payload.ToArray(), cancellationToken: ct);
    }

    public ValueTask StartAsync(Func<string, ReadOnlyMemory<byte>, ValueTask> deliver, CancellationToken ct)
    {
        sink  = deliver;
        token = ct;
        return ValueTask.CompletedTask;
    }

    public async ValueTask JoinAsync(string group, CancellationToken ct)
    {
        // One subscription per group per node, not per connection — the second client in a space
        // costs nothing. Which is also what the Redis backplane does for groups, and is not what it
        // does for users and connections.
        lock (gate)
            if (!joined.Add(group))
                return;

        var nats  = await ConnectionAsync();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        pumps.Add(Task.Run(async () =>
        {
            try
            {
                await foreach (var message in nats.SubscribeAsync<byte[]>(Subject(group), cancellationToken: token))
                {
                    // The first iteration is reached only once the SUB has been written and the
                    // connection flushed, so signalling here is what makes a join observable.
                    ready.TrySetResult();
                    if (message.Data is { } data && sink is { } deliver)
                        await deliver(group, data);
                }
            }
            catch (OperationCanceledException)
            {
                // The run ended.
            }
            finally
            {
                ready.TrySetResult();
            }
        }, token));

        // NATS acknowledges nothing for a plain SUB, so the honest wait is a round trip on the same
        // connection: PING comes back after the SUB the server has already parsed.
        await nats.PingAsync(ct);
        _ = ready;
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
    }
}

/// <summary>
/// JetStream: one stream for all realtime traffic, every node consuming all of it.
/// </summary>
/// <remarks>
/// <para>Deliberately not the shape the deleted <c>NatsContext</c> used. That one created a
/// JetStream stream per space and a durable consumer per client connection, with
/// <c>MaxAckPending = 3</c> — three unacknowledged messages and the consumer stalls — and deleted the
/// consumer on disconnect. It lost messages because that design loses messages, not because
/// JetStream does.</para>
///
/// <para>What this one buys over core NATS is the thing the Redis replay log is currently a separate
/// mechanism for: a cursor a reconnecting node or client can resume from. What it costs is that
/// every event is written before it is delivered.</para>
/// </remarks>
public sealed class NatsJetStreamCarrier(string url, string stream) : IBusPublisher, IBusReceiver
{
    private NatsConnection? connection;
    private INatsJSContext? js;
    private readonly HashSet<string> joined = [];
    private readonly Lock            gate   = new();
    private Task?                    pump;

    private static string Subject(string group) => $"bench.js.{group}";

    private async ValueTask<INatsJSContext> ContextAsync()
    {
        if (js is not null)
            return js;
        connection = new NatsConnection(new NatsOpts { Url = url, Name = "argon-bus-js" });
        await connection.ConnectAsync();
        js = new NatsJSContext(connection);
        return js;
    }

    /// <summary>Removes the stream so a run never reads what the previous one left.</summary>
    public async ValueTask DeleteStreamAsync(CancellationToken ct)
    {
        var context = await ContextAsync();
        try
        {
            await context.DeleteStreamAsync(stream, ct);
        }
        catch (NatsJSApiException)
        {
            // It was not there, which is the state being asked for.
        }
    }

    public async ValueTask EnsureStreamAsync(CancellationToken ct)
    {
        var context = await ContextAsync();
        await context.CreateOrUpdateStreamAsync(new StreamConfig(stream, ["bench.js.>"])
        {
            Retention   = StreamConfigRetention.Limits,
            Storage     = StreamConfigStorage.Memory,
            MaxAge      = TimeSpan.FromMinutes(5),
            MaxMsgs     = 200_000,
            Discard     = StreamConfigDiscard.Old,
            AllowDirect = true
        }, ct);
    }

    public async ValueTask PublishAsync(string group, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var context = await ContextAsync();
        var ack     = await context.PublishAsync(Subject(group), payload.ToArray(), cancellationToken: ct);
        if (ack.Error is not null)
            throw new InvalidOperationException($"jetstream publish failed: {ack.Error.Description}");
    }

    public ValueTask JoinAsync(string group, CancellationToken ct)
    {
        lock (gate)
            joined.Add(group);
        return ValueTask.CompletedTask;
    }

    public async ValueTask StartAsync(Func<string, ReadOnlyMemory<byte>, ValueTask> deliver, CancellationToken ct)
    {
        var context  = await ContextAsync();
        var consumer = await context.CreateOrderedConsumerAsync(stream,
            new NatsJSOrderedConsumerOpts { FilterSubjects = ["bench.js.>"] }, ct);

        pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
                {
                    var group = message.Subject["bench.js.".Length..];

                    bool mine;
                    lock (gate)
                        mine = joined.Contains(group);

                    if (mine && message.Data is { } data)
                        await deliver(group, data);
                }
            }
            catch (OperationCanceledException)
            {
                // The run ended.
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (pump is not null)
            try { await pump; } catch { /* the run ended */ }
        if (connection is not null)
            await connection.DisposeAsync();
    }
}


// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Redis pub/sub on its own — the mechanism the SignalR backplane is built on.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One channel per group, which is what <c>RedisHubLifetimeManager</c> does for a group send.
/// </summary>
/// <remarks>
/// Not a proposal — nobody would write this instead of using the backplane. It exists so the
/// cardinality question can be asked without a websocket per subscription: the backplane's
/// behaviour at fifty thousand connected clients is a property of Redis pub/sub, and this measures
/// that property with SignalR's framing taken off, which makes it a floor rather than an estimate.
/// </remarks>
public sealed class RedisPubSubCarrier(string connectionString, string prefix) : IBusPublisher, IBusReceiver
{
    private ConnectionMultiplexer? connection;
    private Func<string, ReadOnlyMemory<byte>, ValueTask>? sink;
    private readonly HashSet<string> joined = [];
    private readonly Lock            gate   = new();

    private RedisChannel Channel(string group)
        => new($"{prefix}:group:{group}", RedisChannel.PatternMode.Literal);

    private async ValueTask<ConnectionMultiplexer> ConnectionAsync()
    {
        if (connection is not null)
            return connection;

        connection = await ConnectionMultiplexer.ConnectAsync(connectionString);

        // Loss on a pub/sub carrier is almost always a dropped connection rather than a dropped
        // message: the subscription list lives on the connection, so a reconnect re-subscribes and
        // everything published in between is simply gone. Saying which one happened is the whole
        // difference between a number and a diagnosis.
        connection.ConnectionFailed   += (_, e) => Console.Error.WriteLine($"  [redis] {e.ConnectionType} connection failed: {e.FailureType}");
        connection.ConnectionRestored += (_, e) => Console.Error.WriteLine($"  [redis] {e.ConnectionType} connection restored");
        connection.ErrorMessage       += (_, e) => Console.Error.WriteLine($"  [redis] error: {e.Message}");

        return connection;
    }

    public async ValueTask PublishAsync(string group, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var redis = await ConnectionAsync();
        await redis.GetSubscriber().PublishAsync(Channel(group), payload.ToArray());
    }

    public ValueTask StartAsync(Func<string, ReadOnlyMemory<byte>, ValueTask> deliver, CancellationToken ct)
    {
        sink = deliver;
        return ValueTask.CompletedTask;
    }

    public async ValueTask JoinAsync(string group, CancellationToken ct)
    {
        lock (gate)
            if (!joined.Add(group))
                return;

        var redis = await ConnectionAsync();

        // The async overload waits for the SUBSCRIBE to be acknowledged, which is the cost being
        // measured. Fire-and-forget would report the cost of queueing it instead.
        await redis.GetSubscriber().SubscribeAsync(Channel(group), (channel, value) =>
        {
            if (sink is { } deliver && (byte[]?)value is { } data)
                _ = deliver(group, data).AsTask();
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (connection is not null)
            await connection.DisposeAsync();
    }
}
