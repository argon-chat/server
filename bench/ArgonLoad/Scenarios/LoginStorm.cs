namespace Argon.Load.Scenarios;

using Argon.Load.Client;
using Argon.Load.Harness;
using ArgonContracts;
using System.Collections.Concurrent;

/// <summary>
/// Scenario A — everyone arrives at once.
/// </summary>
/// <remarks>
/// What the desktop client does after signing in, taken from <c>poolStore.loadServerDetails</c>:
/// <code>
/// GetSpaces()
///   per space, five spaces in flight at a time:
///     GetServerArchetypes(spaceId)   → IEntitlementGrain(spaceId)
///     GetMembers(spaceId)            → ISpaceReadGrain(spaceId)
///     GetChannels(spaceId)           → ISpaceReadGrain(spaceId)
///     GetChannelGroups(spaceId)      → ISpaceReadGrain(spaceId)
/// </code>
/// So a user in N spaces costs 1 + 4N calls before the first screen renders, and three of every four
/// ask the same grain about the same space.
/// <para>
/// Those three used to land on <c>ISpaceGrain</c>, which is one activation running one turn at a
/// time, so they queued — per space, per user, and across every user of that space at once. The
/// first run of this scenario is what showed it: a clean staircase, 137 ms at 5 clients and 3969 ms
/// at 150, with <c>GetSpaces</c> flat throughout. They now go to a stateless-worker read grain over
/// the shared cache instead.
/// </para>
/// <para>
/// That is the shape this measures. Steady-state throughput is a different scenario: the client
/// keeps its state in IndexedDB and is fed by the event stream, so it barely calls the server again
/// once it is up. What it does do is arrive — after a deploy, after a network blip, at nine in the
/// morning — and arrive together.
/// </para>
/// <para>
/// Every virtual user joins the same space on purpose. Spreading them over many spaces spreads them
/// over many grain activations and the queue never forms, which would measure the wrong thing.
/// </para>
/// </remarks>
public enum BootstrapMode
{
    /// <summary>The four separate calls, as shipped clients still make them.</summary>
    Legacy,

    /// <summary>One versioned snapshot, by a client that has never seen the space.</summary>
    Snapshot,

    /// <summary>One versioned snapshot, by a client that already holds the space and says so.</summary>
    Returning
}

public sealed class LoginStorm(Uri target, int clients, BootstrapMode mode)
{
    private readonly Measurement getSpaces  = new("GetSpaces");
    private readonly Measurement archetypes = new("GetServerArchetypes → commerce?");
    private readonly Measurement members    = new("GetMembers");
    private readonly Measurement channels   = new("GetChannels");
    private readonly Measurement groups     = new("GetChannelGroups");
    private readonly Measurement snapshot   = new("GetSpaceSnapshot");
    private readonly Measurement presence   = new("GetMemberPresence");
    private readonly Measurement bootstrap  = new("TIME TO FIRST SCREEN");

    /// <summary>What each virtual user believes it already has, by space.</summary>
    private readonly ConcurrentDictionary<LoadClient, Dictionary<Guid, SpaceVersions>> known = new();

    /// <summary>The client fetches five spaces' worth of detail concurrently; so does this.</summary>
    private const int SpaceBatchSize = 5;

    public async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"target {target}, {clients} client(s), all in one space");

        var invite = await SpaceFixture.SeedAsync(target, clients, "load scenario A", channels: 10, ct);

        var wall = await Herd.RunAsync(clients,
            prepare: (index, token) => SpaceFixture.JoinAsync(target, invite, index, token),
            run: BootstrapAsync,
            ct,
            settle: mode == BootstrapMode.Returning ? CaptureVersionsAsync : null);

        Measurement[] steps = mode == BootstrapMode.Legacy
            ? [bootstrap, getSpaces, archetypes, members, channels, groups]
            : [bootstrap, getSpaces, snapshot, presence];

        Report.Print($"login storm ({mode.ToString().ToLowerInvariant()}) — {clients} clients arriving together",
            wall, steps);

        Console.WriteLine();
        Console.WriteLine(mode switch
        {
            BootstrapMode.Legacy =>
                "Four calls per space. The three space calls are answered by ISpaceReadGrain, a pool.",
            BootstrapMode.Snapshot =>
                "One versioned call per space, by clients that hold nothing — every part is sent.",
            _ =>
                "One versioned call per space, by clients that hold everything — no part should be sent."
        });
        Console.WriteLine(
            "If a step's p99 climbs with the client count while GetSpaces stays flat, something is serialising.");
    }

    /// <summary>
    /// One un-measured bootstrap, after everyone has joined, so a returning client starts the run
    /// holding what a real one would have in IndexedDB.
    /// </summary>
    private async Task CaptureVersionsAsync(LoadClient client, CancellationToken ct)
    {
        var held   = new Dictionary<Guid, SpaceVersions>();
        var server = client.Service<IServerInteraction>();

        foreach (var space in (await client.Service<IUserInteraction>().GetSpaces(ct)).Values)
            held[space.spaceId] = (await server.GetSpaceSnapshot(space.spaceId, null, ct)).versions;

        known[client] = held;
    }

    /// <summary>Exactly what the desktop client does between "signed in" and "first screen".</summary>
    private async Task BootstrapAsync(LoadClient client, CancellationToken ct)
    {
        await bootstrap.TimeAsync<object?>(async () =>
        {
            var user   = client.Service<IUserInteraction>();
            var server = client.Service<IServerInteraction>();

            var spaces = await getSpaces.TimeAsync(async () => await user.GetSpaces(ct));

            foreach (var batch in spaces.Values.Chunk(SpaceBatchSize))
                await Task.WhenAll(batch.Select(space => mode == BootstrapMode.Legacy
                    ? LegacyAsync(server, space.spaceId, ct)
                    : SnapshotAsync(client, server, space.spaceId, ct)));

            return null;
        });
    }

    private Task LegacyAsync(IServerInteraction server, Guid id, CancellationToken ct)
        => Task.WhenAll(
            archetypes.TimeAsync<object?>(async () => await server.GetServerArchetypes(id, ct)),
            members.TimeAsync<object?>(async () => await server.GetMembers(id, ct)),
            channels.TimeAsync<object?>(async () => await server.GetChannels(id, ct)),
            groups.TimeAsync<object?>(async () => await server.GetChannelGroups(id, ct)));

    private Task SnapshotAsync(LoadClient client, IServerInteraction server, Guid id, CancellationToken ct)
    {
        var held = known.TryGetValue(client, out var versions) && versions.TryGetValue(id, out var v) ? v : null;

        return Task.WhenAll(
            snapshot.TimeAsync<object?>(async () => await server.GetSpaceSnapshot(id, held, ct)),
            presence.TimeAsync<object?>(async () => await server.GetMemberPresence(id, ct)));
    }
}
