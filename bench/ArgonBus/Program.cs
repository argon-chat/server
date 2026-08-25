namespace Argon.Bus;

/// <summary>Everything a run needs, after the command line has been read.</summary>
public sealed record RunOptions
{
    public Carrier[] Carriers { get; init; } =
        [Carrier.Local, Carrier.SignalR, Carrier.RedisPubSub, Carrier.RedisStreams, Carrier.NatsCore, Carrier.NatsJetStream];
    public string    Redis         { get; init; } = "127.0.0.1:6379";
    public string    Nats          { get; init; } = "nats://127.0.0.1:4222";
    public string    JetStreamName { get; init; } = "ARGONBENCH";

    public int    Nodes    { get; init; } = 2;
    public int    Clients  { get; init; } = 50;
    public int    Groups   { get; init; } = 1;
    public int    Messages { get; init; } = 300;
    public int    Payload  { get; init; } = 512;
    public double Rate     { get; init; }
    public int    Seconds  { get; init; } = 20;
    public int    Senders  { get; init; } = 8;

    public int Shards       { get; init; } = 64;
    public int StreamMaxLen { get; init; } = 100_000;

    /// <summary>Subscriptions to hold open in the <c>storm</c> scenario.</summary>
    public int Subs { get; init; } = 20_000;

    /// <summary>Send back to back instead of on a clock, to find the ceiling rather than hold a rate.</summary>
    public bool Saturate { get; init; }

    /// <summary>The protocol registered on the hub. Argon has this package referenced and commented out.</summary>
    public bool MessagePack { get; init; }

    public int BasePort { get; init; } = 47100;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Usage();
            return 0;
        }

        var scenario = "";
        var options  = new RunOptions();

        for (var i = 0; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException($"'{args[i - 1]}' needs a value");

            switch (args[i])
            {
                case "--scenario": scenario = Next(); break;
                case "--carrier":
                    options = options with
                    {
                        Carriers = Next().Split(',', StringSplitOptions.RemoveEmptyEntries)
                           .Select(ParseCarrier).ToArray()
                    };
                    break;
                case "--redis":    options = options with { Redis = Next() }; break;
                case "--nats":     options = options with { Nats = Next() }; break;
                case "--nodes":    options = options with { Nodes = int.Parse(Next()) }; break;
                case "--clients":  options = options with { Clients = int.Parse(Next()) }; break;
                case "--groups":   options = options with { Groups = int.Parse(Next()) }; break;
                case "--messages": options = options with { Messages = int.Parse(Next()) }; break;
                case "--payload":  options = options with { Payload = int.Parse(Next()) }; break;
                case "--rate":     options = options with { Rate = double.Parse(Next()) }; break;
                case "--seconds":  options = options with { Seconds = int.Parse(Next()) }; break;
                case "--senders":  options = options with { Senders = int.Parse(Next()) }; break;
                case "--shards":   options = options with { Shards = int.Parse(Next()) }; break;
                case "--subs":     options = options with { Subs = int.Parse(Next()) }; break;
                case "--msgpack":  options = options with { MessagePack = true }; break;
                case "--saturate": options = options with { Saturate = true }; break;
                case "--port":     options = options with { BasePort = int.Parse(Next()) }; break;
                default:
                    Console.Error.WriteLine($"unknown argument '{args[i]}'");
                    return 2;
            }
        }

        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            switch (scenario)
            {
                case "hop":
                    await Scenarios.HopAsync(options, cancel.Token);
                    return 0;
                case "publish":
                    await Scenarios.PublishAsync(options, cancel.Token);
                    return 0;
                case "storm":
                    await Scenarios.StormAsync(options, cancel.Token);
                    return 0;
                case "wire":
                    await Scenarios.WireAsync(options, cancel.Token);
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown scenario '{scenario}'");
                    Usage();
                    return 2;
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("cancelled");
            return 130;
        }
    }

    private static Carrier ParseCarrier(string name) => name.ToLowerInvariant() switch
    {
        "local"                     => Carrier.Local,
        "signalr" or "backplane"    => Carrier.SignalR,
        "pubsub"                    => Carrier.RedisPubSub,
        "streams" or "redis"        => Carrier.RedisStreams,
        "nats" or "nats-core"       => Carrier.NatsCore,
        "jetstream" or "nats-js"    => Carrier.NatsJetStream,
        _ => throw new ArgumentException($"unknown carrier '{name}'")
    };

    private static void Usage()
    {
        Console.WriteLine("""
            argon-bus — what it costs to move an event from the silo that raised it to the node
            holding the client.

              --scenario hop     latency and throughput of the hop, end to end through a real
                                 websocket, once per carrier
              --scenario publish the write side alone, with nothing listening
              --scenario storm   subscription cardinality: what an entry node pays to hold the
                                 subscriptions of everyone connected to it, and what that does to
                                 the hop
              --scenario wire    bytes on the wire for one event, per carrier

              --carrier local,signalr,pubsub,streams,nats,jetstream   default: all six
              --redis 127.0.0.1:6379        --nats nats://127.0.0.1:4222
              --nodes 2                     entry nodes to run
              --clients 50                  listeners spread over them
              --groups 1                    distinct groups the listeners are spread over
              --messages 300                one at a time, for latency
              --rate 0 --senders 8 --seconds 20   sustained instead, for throughput
              --saturate                    senders go flat out, to find the ceiling
              --payload 512                 bytes per event
              --subs 20000                  subscriptions for the storm scenario
              --shards 64                   redis streams to shard over
              --msgpack                     register the MessagePack hub protocol

            Both Redis and NATS have to be up. Everything runs in one process, so the clocks on both
            sides of every subtraction are the same one and the absolute numbers are a floor.
            """);
    }
}
