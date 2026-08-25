namespace ArgonComplexTest;

using Argon.Drains;
using Argon.Features.Clustering;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using System.Net;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Runtime;
using Orleans.Runtime.Placement;

/// <summary>
/// Moving a live activation from one silo to another without the people on it noticing.
/// </summary>
/// <remarks>
/// <para>This is the property a blue-green deploy rests on. Draining a silo used to mean waiting for
/// its activations to fall out on the idle timer, which never happens to the ones that matter: a
/// channel with a call in it pins itself for a day, so the drain gave up and the shutdown tore the
/// call down. Migration moves the activation instead — but only if the grain says what travels with
/// it, and only if it can tell a move from an ending.</para>
///
/// <para>Both halves fail silently. A migrated channel that forgets it is in a call looks exactly
/// like an empty channel; a migrated channel that runs its ordinary teardown broadcasts a departure
/// for every participant and tells the space the call emptied. Neither throws, and neither is
/// visible to a single-silo test, which is why this fixture runs a cluster of two.</para>
///
/// <para>The cluster is its own, separate from the functional suite's, but the database is shared —
/// so the space and the channel are created through the ordinary API and then driven through the
/// migration cluster's grain factory.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class GrainMigrationTests : TestBase
{
    // Clear of RoleStartupTests, which walks upward from 21111.
    private const int FirstSiloPort  = 22111;
    private const int SecondSiloPort = 22131;

    private const string MigrationCluster = "argon-test-migration";

    private RoleHost first  = null!;
    private RoleHost second = null!;

    private IGrainFactory Grains => first.Services.GetRequiredService<IGrainFactory>();

    /// <summary>
    /// Two silos of the role that hosts channels and spaces, in a cluster of their own.
    /// </summary>
    /// <remarks>
    /// Started one after the other rather than together: both run the database warm-up on the way up,
    /// and two of those racing on the same schema is a flake that has nothing to do with what is
    /// being tested.
    /// </remarks>
    [OneTimeSetUp]
    public async Task StartTwoSilos()
    {
        var settings = ArgonTestEnvironment.Instance.Host.Settings;

        first = new RoleHost(settings, ArgonRoleId.Core, FirstSiloPort, MigrationCluster);
        _ = first.Services.GetRequiredService<IGrainFactory>();

        second = new RoleHost(settings, ArgonRoleId.Core, SecondSiloPort, MigrationCluster);
        _ = second.Services.GetRequiredService<IGrainFactory>();

        await WaitForClusterAsync(2, TimeSpan.FromMinutes(2));
    }

    [OneTimeTearDown]
    public async Task StopSilos()
    {
        await second.DisposeAsync();
        await first.DisposeAsync();
    }

    /// <summary>
    /// A channel carrying a call keeps its participants when the activation changes silo.
    /// </summary>
    [Test, Order(1), CancelAfter(600_000)]
    public async Task A_voice_channel_survives_the_move_with_everyone_still_in_it(CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "migrating-room", ct);

        var channel = Grains.GetGrain<IChannelGrain>(channelId);
        var space   = Grains.GetGrain<ISpaceGrain>(spaceId);

        await channel.OnParticipantJoined(owner.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(GetMembersOf(channel).Result.Select(m => m.userId), Does.Contain(owner.UserId),
                "premise: the call has someone in it before the move");
            Assert.That(space.GetUserVoiceSlotAsync(owner.UserId).Result, Is.Not.Null,
                "premise: the space agrees they are in voice");
        });

        var before = await HostOf(channel, ct);
        await MoveAsync(channel, Elsewhere(before), ct);
        var after = await WaitForMoveAsync(channel, before, ct);

        Assert.That(after, Is.Not.EqualTo(before), "the activation did not move, so nothing was proven");

        var members = await channel.GetMembers();
        var slot    = await space.GetUserVoiceSlotAsync(owner.UserId);

        Assert.Multiple(() =>
        {
            // The roster travelling is the whole point: rebuilt from storage it would be empty,
            // because a fresh activation clears it on purpose.
            Assert.That(members.Select(m => m.userId), Does.Contain(owner.UserId),
                "the call lost its participants in the move");

            // And the space still holding the slot is how we know the move did not run the ordinary
            // teardown, which would have announced a departure to every client in the space.
            Assert.That(slot, Is.Not.Null,
                "the move was reported to the space as the participant leaving voice");
        });
    }

    /// <summary>
    /// Draining a silo carries a live call off it instead of hanging up on everyone.
    /// </summary>
    /// <remarks>
    /// <para>The test above names its destination, because with nothing draining the placement
    /// director is free to put the activation back where it was. This one names nothing: it puts a
    /// call on a silo, drains that silo through the same service the deployment calls, and expects
    /// the call to be somewhere else afterwards with the same people in it. That is the whole
    /// blue-green claim, and every piece of it is load-bearing — the drain sweep to ask, the
    /// placement filter to stop the activation coming home, and the grain hooks to keep the roster.
    /// </para>
    ///
    /// <para>Runs last, and is ordered rather than left to chance: the silo it drains stays drained,
    /// and a test that ran after it would be talking to a cluster of one.</para>
    /// </remarks>
    [Test, Order(2), CancelAfter(600_000)]
    public async Task Draining_a_silo_carries_a_live_call_off_it(CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "draining-room", ct);

        var channel = Grains.GetGrain<IChannelGrain>(channelId);
        var space   = Grains.GetGrain<ISpaceGrain>(spaceId);

        await channel.OnParticipantJoined(owner.UserId);

        var before   = await HostOf(channel, ct);
        var draining = HostFor(before);

        using var probes = draining.CreateClient();

        Assert.Multiple(() =>
        {
            Assert.That(Probe(probes, "startup").Result, Is.EqualTo(HttpStatusCode.OK),
                "premise: the silo has joined the cluster");
            Assert.That(Probe(probes, "ready").Result, Is.EqualTo(HttpStatusCode.OK),
                "premise: the silo is taking traffic");
        });

        var drained = await draining.Services.GetRequiredService<ISiloDrainService>()
           .StartDrainingAsync(ct);

        Assert.That(drained.IsSuccess, Is.True, drained.Message);

        var after = await WaitForMoveAsync(channel, before, ct);

        Assert.That(after, Is.Not.EqualTo(before), "the drain left the call on the silo it was draining");

        var members = await channel.GetMembers();
        var slot    = await space.GetUserVoiceSlotAsync(owner.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(members.Select(m => m.userId), Does.Contain(owner.UserId),
                "the deployment emptied the call it was supposed to move");
            Assert.That(slot, Is.Not.Null,
                "the drain was reported to the space as the participant leaving voice");
        });

        Assert.Multiple(() =>
        {
            // Readiness is how the silo tells Kubernetes to stop sending it work. Until this was
            // mapped it answered 404, which reads as unhealthy by accident rather than on purpose.
            Assert.That(Probe(probes, "ready").Result, Is.EqualTo(HttpStatusCode.ServiceUnavailable),
                "a drained silo still reports itself ready for traffic");

            // And liveness must not follow it down: the remedy for a failed liveness probe is a
            // restart, and restarting a pod in the middle of handing its grains over destroys exactly
            // what the drain was protecting.
            Assert.That(Probe(probes, "live").Result, Is.EqualTo(HttpStatusCode.OK),
                "draining was reported as the process being dead, which Kubernetes answers by killing it");
        });

        // Moving what is there is only half a drain. A silo on its way out must also stop being a
        // candidate for anything new, which is the placement filter's job — and the job it was not
        // doing, because a filter applies only where a grain property names it and nothing named it.
        var fresh = await CreateVoiceChannelAsync(owner, spaceId, "arrived-after-the-drain", ct);

        await Grains.GetGrain<IChannelGrain>(fresh).GetMembers();

        Assert.That(await HostOf(Grains.GetGrain<IChannelGrain>(fresh), ct), Is.Not.EqualTo(before),
            "a grain activated after the drain landed on the silo that is being taken out of service");
    }

    /// <summary>
    /// A drained silo can be put back into service without redeploying it.
    /// </summary>
    /// <remarks>
    /// <para>It could not. Every exit from a drain ends in <c>Drained</c> — the success path, the
    /// timeout, and both failure paths — while <c>CancelDraining</c> accepted only <c>Draining</c>,
    /// a state a drain passes through rather than one it stops in. So a maintenance window that was
    /// called off left the silo out of rotation until someone rolled the deployment.</para>
    ///
    /// <para>Safe because draining never touched Orleans membership: the silo stayed <c>Active</c> to
    /// the cluster throughout and is a valid host again the moment readiness says so.</para>
    ///
    /// <para>Ordered after the drain test because it is the drain test's silo it is putting back.</para>
    /// </remarks>
    [Test, Order(3)]
    public Task A_cancelled_maintenance_window_puts_the_silo_back()
    {
        // Which of the two was drained depends on where the channel activated, so ask rather than
        // assume — the first version of this assumed and failed on the premise.
        var host = new[] { first, second }.FirstOrDefault(h =>
            h.Services.GetRequiredService<ISiloDrainService>().GetStatus().State == SiloDrainState.Drained);

        Assert.That(host, Is.Not.Null, "premise: the previous test left one of the silos drained");

        var drain  = host!.Services.GetRequiredService<ISiloDrainService>();
        var probes = host.CreateClient();
        Assert.That(Probe(probes, "ready").Result, Is.EqualTo(HttpStatusCode.ServiceUnavailable),
            "premise: a drained silo is not taking traffic");

        var cancelled = drain.CancelDraining();

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.IsSuccess, Is.True, cancelled.Message);
            Assert.That(drain.GetStatus().State, Is.EqualTo(SiloDrainState.Active));
            Assert.That(Probe(probes, "ready").Result, Is.EqualTo(HttpStatusCode.OK),
                "the silo was put back in service and still refuses traffic");
        });

        probes.Dispose();
        return Task.CompletedTask;
    }

    // A companion test for IUserSessionGrain would belong here and does not exist: the interface
    // exposes no read of its connection set, HeartBeatAsync self-heals that set by design, and adding
    // a method to the contract purely so a test can look at it is worse than the gap. What can be
    // checked without a contract change is that the record it carries survives serialization, and
    // that is asserted in ActivationStateSerializationTests.

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(
            new CreateServerRequest("Migration Space", "Description", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private async Task<Guid> CreateVoiceChannelAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Voice, "Migration channel", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created   = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    private Task<List<RealtimeChannelUser>> GetMembersOf(IChannelGrain channel)
        => channel.GetMembers();

    /// <summary>Which silo currently holds this activation, according to the cluster itself.</summary>
    private async Task<SiloAddress> HostOf(IAddressable grain, CancellationToken ct)
    {
        var id = grain.GetGrainId();

        var statistics = await Grains.GetGrain<IManagementGrain>(0).GetDetailedGrainStatistics();
        var placed     = statistics.FirstOrDefault(s => s.GrainId.Equals(id));

        Assert.That(placed.SiloAddress, Is.Not.Null, $"'{id}' is not activated anywhere");

        return placed.SiloAddress;
    }

    /// <summary>The status code a Kubernetes probe would get.</summary>
    private static async Task<HttpStatusCode> Probe(HttpClient client, string probe)
    {
        using var response = await client.GetAsync($"/health/{probe}");
        return response.StatusCode;
    }

    /// <summary>Which of the two hosts is the silo at this address.</summary>
    private RoleHost HostFor(SiloAddress address)
    {
        var host = new[] { first, second }.FirstOrDefault(candidate =>
            candidate.Services.GetRequiredService<ILocalSiloDetails>().SiloAddress.Equals(address));

        Assert.That(host, Is.Not.Null, $"'{address}' is not one of this fixture's silos");

        return host!;
    }

    /// <summary>An active silo that is not the one given.</summary>
    private SiloAddress Elsewhere(SiloAddress from)
    {
        var other = first.Services.GetRequiredService<ISiloStatusOracle>()
           .GetApproximateSiloStatuses(onlyActive: true)
           .Keys.FirstOrDefault(silo => !silo.Equals(from));

        Assert.That(other, Is.Not.Null, "the cluster has nowhere to migrate to");

        return other!;
    }

    /// <summary>
    /// Asks the activation to move to a named silo the next time it is idle.
    /// </summary>
    /// <remarks>
    /// The destination is named rather than left to the placement director, and the test needs that
    /// even though production does not. Resource-optimized placement carries a preference for the
    /// silo it is running on, so an unhinted migration is free to put the activation straight back
    /// where it was — which proves nothing and fails on the comparison. The drain names its
    /// destinations for the same reason, so this is the production mechanism rather than a test-only
    /// shortcut; the difference is only that a drain picks the destination itself.
    /// <para>
    /// The hint travels in the request context, which migration captures and hands to the placement
    /// director for the next activation.
    /// </para>
    /// </remarks>
    private async Task MoveAsync(IAddressable grain, SiloAddress to, CancellationToken ct)
    {
        RequestContext.Set(IPlacementDirector.PlacementHintKey, to);

        try
        {
            await Grains.GetGrain<IGrainManagementExtension>(grain.GetGrainId()).MigrateOnIdle();
        }
        finally
        {
            RequestContext.Remove(IPlacementDirector.PlacementHintKey);
        }
    }

    /// <summary>
    /// Waits until the cluster reports the activation somewhere other than where it started.
    /// </summary>
    /// <remarks>
    /// Migration is a request honoured when the activation next goes idle, and the statistics that
    /// report it are gathered periodically, so this is a poll rather than an await. A move that never
    /// lands returns the original address and the caller fails on the comparison, which says more than
    /// a timeout would.
    /// </remarks>
    private async Task<SiloAddress> WaitForMoveAsync(IAddressable grain, SiloAddress from, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var now = await HostOf(grain, ct);
            if (!now.Equals(from))
                return now;
        }

        return from;
    }

    private async Task WaitForClusterAsync(int expected, TimeSpan within)
    {
        var oracle   = first.Services.GetRequiredService<ISiloStatusOracle>();
        var deadline = DateTime.UtcNow + within;

        while (DateTime.UtcNow < deadline)
        {
            if (oracle.GetApproximateSiloStatuses(onlyActive: true).Count >= expected)
                return;

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        Assert.Fail($"the migration cluster did not reach {expected} active silos");
    }
}
