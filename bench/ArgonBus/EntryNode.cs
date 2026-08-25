namespace Argon.Bus;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System.Diagnostics;

/// <summary>
/// The hub a client connects to. Every option in this bench keeps it — the leg from the entry node
/// to the browser is SignalR either way, and it is the leg between nodes that is in question.
/// </summary>
public sealed class BusHub(IBusReceiver receiver) : Hub
{
    public const string Method = "e";

    public async override Task OnConnectedAsync()
    {
        var query = Context.GetHttpContext()?.Request.Query["g"].ToString() ?? "";

        foreach (var group in query.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // Local dispatch, always. With the backplane this is also what issues the Redis
            // SUBSCRIBE for the group; without it, it is a dictionary insert and the carrier below
            // does the subscribing.
            await Groups.AddToGroupAsync(Context.ConnectionId, group);
            await receiver.JoinAsync(group, Context.ConnectionAborted);
        }
    }
}

/// <summary>One node clients connect to: Kestrel, the hub, and whatever feeds it from elsewhere.</summary>
public sealed class EntryNode : IAsyncDisposable
{
    private WebApplication app     = null!;
    private IBusReceiver   carrier = null!;

    public required Uri                   Url { get; init; }
    public required IHubContext<BusHub>   Hub { get; init; }

    public static async Task<EntryNode> StartAsync(int port, Carrier carrier, RunOptions options, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        var receiver = CreateReceiver(carrier, options);
        builder.Services.AddSingleton(receiver);

        var signalr = builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);

        if (carrier == Carrier.SignalR)
            signalr.AddStackExchangeRedis(o =>
            {
                o.Configuration               = ConfigurationOptions.Parse(options.Redis);
                o.Configuration.ChannelPrefix = new RedisChannel("bench-bus", RedisChannel.PatternMode.Literal);
            });

        if (options.MessagePack)
            signalr.AddMessagePackProtocol();

        var app = builder.Build();
        app.MapHub<BusHub>("/bus");
        await app.StartAsync(ct);

        var hub  = app.Services.GetRequiredService<IHubContext<BusHub>>();
        var node = new EntryNode
        {
            Url = new Uri($"http://127.0.0.1:{port}"),
            Hub = hub
        };
        node.app     = app;
        node.carrier = receiver;

        await receiver.StartAsync(
            (group, payload) => new(hub.Clients.Group(group).SendAsync(BusHub.Method, payload.ToArray(), ct)),
            ct);

        return node;
    }

    private static IBusReceiver CreateReceiver(Carrier carrier, RunOptions options) => carrier switch
    {
        Carrier.RedisPubSub   => new RedisPubSubCarrier(options.Redis, "bench-bus"),
        Carrier.RedisStreams  => new RedisStreamsCarrier(options.Redis, options.Shards, options.StreamMaxLen),
        Carrier.NatsCore      => new NatsCoreCarrier(options.Nats),
        Carrier.NatsJetStream => new NatsJetStreamCarrier(options.Nats, options.JetStreamName),

        // The backplane has no receiving half of ours: the hub lifetime manager is it.
        _                     => new NullReceiver()
    };

    public async ValueTask DisposeAsync()
    {
        await carrier.DisposeAsync();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

/// <summary>Stands in for the receiving half the SignalR backplane provides itself.</summary>
public sealed class NullReceiver : IBusReceiver
{
    public ValueTask JoinAsync(string group, CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask StartAsync(Func<string, ReadOnlyMemory<byte>, ValueTask> deliver, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>A client holding a hub connection and stamping what arrives on it.</summary>
public sealed class Listener : IAsyncDisposable
{
    private readonly HubConnection connection;

    private Listener(HubConnection connection)
        => this.connection = connection;

    public static async Task<Listener> ConnectAsync(
        EntryNode node, string group, RunOptions options, Action<ReadOnlyMemory<byte>> onReceived, CancellationToken ct)
    {
        var builder = new HubConnectionBuilder()
           .WithUrl($"{node.Url}bus?g={group}", o => o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets);

        if (options.MessagePack)
            builder.AddMessagePackProtocol();

        var connection = builder.Build();

        connection.On<byte[]>(BusHub.Method, payload => onReceived(payload));

        await connection.StartAsync(ct);
        return new Listener(connection);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>
/// The publishing node — a silo. It hosts no endpoint and accepts no connections; all it does is
/// raise events, which is exactly the position <c>ChannelGrain</c> is in.
/// </summary>
public static class SiloNode
{
    public static async Task<IBusPublisher> CreateAsync(Carrier carrier, RunOptions options, EntryNode local, CancellationToken ct)
    {
        switch (carrier)
        {
            case Carrier.Local:
                // The control: publish on the node that already holds the client. Whatever this
                // measures is the client leg and SignalR itself, and every other row includes it.
                return new HubContextPublisher(local.Hub);

            case Carrier.SignalR:
            {
                var services = new ServiceCollection();
                services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));

                var signalr = services.AddSignalR()
                   .AddStackExchangeRedis(o =>
                    {
                        o.Configuration               = ConfigurationOptions.Parse(options.Redis);
                        o.Configuration.ChannelPrefix = new RedisChannel("bench-bus", RedisChannel.PatternMode.Literal);
                    });

                if (options.MessagePack)
                    signalr.AddMessagePackProtocol();

                var provider = services.BuildServiceProvider();
                return new HubContextPublisher(provider.GetRequiredService<IHubContext<BusHub>>());
            }

            case Carrier.RedisPubSub:
                return new RedisPubSubCarrier(options.Redis, "bench-bus");

            case Carrier.RedisStreams:
                return new RedisStreamsCarrier(options.Redis, options.Shards, options.StreamMaxLen);

            case Carrier.NatsCore:
                return new NatsCoreCarrier(options.Nats);

            case Carrier.NatsJetStream:
            {
                var js = new NatsJetStreamCarrier(options.Nats, options.JetStreamName);
                await js.EnsureStreamAsync(ct);
                return js;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(carrier));
        }
    }
}
