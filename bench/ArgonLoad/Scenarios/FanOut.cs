namespace Argon.Load.Scenarios;

using Argon.Load.Client;
using Argon.Load.Harness;
using ArgonContracts;
using System.Collections.Concurrent;
using System.Diagnostics;

/// <summary>
/// Scenario B — one person types, everybody hears it.
/// </summary>
/// <remarks>
/// Scenario A measured arrival. This measures the rest of the day: once a client is up it barely
/// calls the server again, and almost everything it learns arrives over the hub. A message travels
/// <c>SendMessage</c> → <c>ChannelGrain</c> → <c>AppHubServer</c> → the Redis backplane → every
/// connected member, and none of that path had ever been measured.
/// <para>
/// Two numbers come out and they answer different questions. <c>SendMessage</c> is what the sender
/// waits for before their own message appears. <c>DELIVERY</c> is send-to-arrival for every
/// (message, recipient) pair, which is what everybody else waits for and what grows with the size of
/// the room.
/// </para>
/// <para>
/// With <c>--rate</c> it stops being a latency test and becomes a throughput one: several senders on
/// their own clocks, held for a fixed window. The number that matters then is not the percentile but
/// whether the achieved rate matched the offered one — a room that cannot keep up reports beautiful
/// latencies for the few messages it managed.
/// </para>
/// <para>
/// Both clocks are this process's, so the two sides of the subtraction are comparable and no clock
/// skew is involved — the bench and the server being on one machine is what makes that true, and is
/// also why the absolute numbers are a floor rather than a forecast.
/// </para>
/// </remarks>
public sealed class FanOut(Uri target, int clients, int messages, int senders, double rate, int seconds, int channels, int listeners)
{
    private readonly Measurement send     = new("SendMessage");
    private readonly Measurement delivery = new("DELIVERY (send → each recipient)");

    /// <summary>Send timestamp per probe, written before the call and read by every recipient.</summary>
    private readonly ConcurrentDictionary<long, long> sentAt = new();

    private long issued;
    private long delivered;
    private long abandoned;

    private const string ProbePrefix = "probe:";

    /// <summary>A rate turns this from "how long does one message take" into "how many fit".</summary>
    private bool Sustained => rate > 0;

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine(Sustained
            ? $"target {target}, {clients} listener(s), {channels} channel(s), {senders} sender(s) at {rate:0.#}/s for {seconds}s"
            : $"target {target}, {clients} listener(s), {channels} channel(s), {messages} message(s), one sender");

        var invite    = await SpaceFixture.SeedAsync(target, clients, "load scenario B", channels, ct);
        var crowd = new LoadClient[clients];
        var hubs      = new List<HubListener>();

        Console.WriteLine($"preparing {clients} client(s)…");

        await Parallel.ForAsync(0, clients,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (i, token) => crowd[i] = await SpaceFixture.JoinAsync(target, invite, i, token));

        try
        {
            // Not every client has to listen. MessageSent is space-scoped, so each listener multiplies
            // every message by one more delivery — with the fan-out unbounded there is no way to see
            // what the write path alone can take. Fewer listeners, more senders, and the number that
            // comes out is the node's write capacity rather than its delivery capacity.
            var listening = Math.Clamp(listeners, 1, clients);

            Console.WriteLine($"connecting {listening} of {clients} client(s) to the hub…");

            foreach (var client in crowd.Take(listening))
            {
                var hub = await HubListener.ConnectAsync(target, client, ct);
                hub.Received += OnReceived;
                hubs.Add(hub);
            }

            // Senders are drawn from the crowd, not added beside it: a real room has its authors in
            // it, and their own clients are recipients the backplane still has to reach.
            var targets = await ChannelsAsync(crowd[0], ct);
            var wall    = Stopwatch.GetTimestamp();

            if (Sustained)
                await SustainAsync(crowd, targets, ct);
            else
                for (var i = 0; i < messages; i++)
                    await SendProbeAsync(crowd[0], targets[0], ct);

            var elapsed = Stopwatch.GetElapsedTime(wall);
            var lost    = await DrainAsync(ct);

            Report.Print(Sustained
                    ? $"fan-out — {clients} listeners, {channels} channels, {senders} senders at {rate:0.#}/s"
                    : $"fan-out — {clients} listeners, {messages} messages",
                elapsed, [send, delivery]);

            Console.WriteLine();

            if (Sustained)
            {
                var sent     = Interlocked.Read(ref issued) - Interlocked.Read(ref abandoned);
                var achieved = sent / elapsed.TotalSeconds;

                Console.WriteLine($"offered {rate:0.#} msg/s, achieved {achieved:0.#} msg/s " +
                                  $"({sent} sent, {Interlocked.Read(ref delivered)} delivered)");

                if (achieved < rate * 0.95)
                    Console.WriteLine("the senders could not keep up — the server, not the offered rate, set " +
                                      "the pace, so DELIVERY describes a slower stream than was asked for.");
            }

            if (lost > 0)
                Console.WriteLine($"{lost} delivery/deliveries never arrived within 10s — DELIVERY " +
                                  "describes only what did.");

            Console.WriteLine(
                "DELIVERY is one sample per (message, recipient), so it is the number that grows with the room.");
            Console.WriteLine(
                "If it climbs with the listener count while SendMessage stays flat, the cost is the fan-out.");
        }
        finally
        {
            foreach (var hub in hubs)
                await hub.DisposeAsync();

            foreach (var client in crowd)
                client?.Dispose();
        }
    }

    /// <summary>
    /// Every sender on its own clock, so a slow call delays that sender's next message and nobody
    /// else's — which is how a room of people behaves and is not how a single loop does.
    /// </summary>
    private async Task SustainAsync(LoadClient[] crowd, IReadOnlyList<Guid> targets, CancellationToken ct)
    {
        using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
        window.CancelAfter(TimeSpan.FromSeconds(seconds));

        var interval = TimeSpan.FromSeconds(senders / rate);

        await Task.WhenAll(Enumerable.Range(0, senders).Select(async index =>
        {
            var       sender  = crowd[index % crowd.Length];
            var       channel = targets[index % targets.Count];
            using var ticks   = new PeriodicTimer(interval);

            try
            {
                while (await ticks.WaitForNextTickAsync(window.Token))
                    await SendProbeAsync(sender, channel, window.Token);
            }
            catch (OperationCanceledException)
            {
                // The window closed. That is how the run ends, not a failure.
            }
        }));
    }

    /// <summary>
    /// Waits for the tail. Anything still missing after this is not slow, it is lost, and waiting
    /// longer would only turn a missing delivery into a very large one.
    /// </summary>
    private async Task<long> DrainAsync(CancellationToken ct)
    {
        var started  = Stopwatch.GetTimestamp();
        var expected = (Interlocked.Read(ref issued) - Interlocked.Read(ref abandoned)) * Math.Clamp(listeners, 1, clients);

        while (Interlocked.Read(ref delivered) < expected &&
               Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            await Task.Delay(50, ct);

        return expected - Interlocked.Read(ref delivered);
    }

    private async Task SendProbeAsync(LoadClient sender, Guid channel, CancellationToken ct)
    {
        var probe = Interlocked.Increment(ref issued);

        // Registered before the call, not after: a recipient can see the message before the sender's
        // own call has returned, and a send time written afterwards would make that a negative.
        sentAt[probe] = Stopwatch.GetTimestamp();

        try
        {
            await send.TimeAsync<object?>(async () => await sender.Service<IChannelInteraction>()
               .SendMessage(sender.SpaceId, channel, $"{ProbePrefix}{probe}", [], Random.Shared.NextInt64(), null, ct));
        }
        catch
        {
            // A probe that was never sent cannot be delivered, so it must not be counted as a
            // delivery that went missing. Without this the end of a timed window — where the last
            // send from every sender is cancelled mid-flight — reports one lost delivery per
            // listener per sender, which reads exactly like the backplane dropping messages.
            Interlocked.Increment(ref abandoned);

            if (ct.IsCancellationRequested)
                throw;
        }
    }

    private void OnReceived(IArgonEvent @event, long arrived)
    {
        if (@event is not MessageSent sent || !sent.message.text.StartsWith(ProbePrefix))
            return;

        if (!long.TryParse(sent.message.text[ProbePrefix.Length..], out var probe))
            return;

        if (!sentAt.TryGetValue(probe, out var start))
            return;

        delivery.Record(Stopwatch.GetElapsedTime(start, arrived));
        Interlocked.Increment(ref delivered);
    }

    /// <summary>
    /// Every text channel of the space, so senders can be spread over more than one.
    /// </summary>
    /// <remarks>
    /// One channel measures what a room can take; many measure what the node can. They are different
    /// questions and the second is the one that sizes a deployment, because a channel is a grain and
    /// the node runs thousands of them.
    /// </remarks>
    private static async Task<IReadOnlyList<Guid>> ChannelsAsync(LoadClient client, CancellationToken ct)
    {
        var spaces  = await client.Service<IUserInteraction>().GetSpaces(ct);
        var spaceId = spaces.Values.First().spaceId;

        client.SpaceId = spaceId;

        var snapshot = await client.Service<IServerInteraction>().GetSpaceSnapshot(spaceId, null, ct);
        var channels = snapshot.channels!.Value.ToList();

        // Creating a space does not create any channels, so a run against one the fixture did not
        // furnish would measure a query that returns nothing and fans out to nobody.
        if (channels.Count == 0)
            throw new InvalidOperationException($"space {spaceId} has no visible channels");

        return channels.Where(c => c.channel.type == ChannelType.Text)
           .Select(c => c.channel.channelId).ToList();
    }
}
