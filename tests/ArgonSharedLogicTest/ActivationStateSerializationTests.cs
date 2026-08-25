namespace ArgonSharedLogicTest;

using Argon.Grains;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;
using Argon.Features.Orleanse.Storages;
using Orleans.Runtime;
using Orleans.Serialization;

/// <summary>
/// The records a grain hands the runtime when its activation moves to another silo.
/// </summary>
/// <remarks>
/// <para>These are the only types in the codebase whose serialization is load-bearing and invisible.
/// A member without an <c>[Id]</c>, or one Orleans has no codec for, does not fail a build and does
/// not fail a request — it arrives on the other silo as a default value, and the grain carries on
/// with a slightly wrong idea of the world. For a channel that means a call that quietly lost its
/// participants; for a session, one that believes nobody is connected.</para>
///
/// <para>Round-tripping through the real serializer is what makes that visible. It also pins the
/// thing a reviewer would otherwise have to notice by eye: every member is asserted, so adding one
/// without an <c>[Id]</c> fails here rather than in a deployment.</para>
/// </remarks>
[TestFixture]
public class ActivationStateSerializationTests
{
    private static Serializer Serializer()
        => new ServiceCollection()
           .AddSerializer()
           .BuildServiceProvider()
           .GetRequiredService<Serializer>();

    private static T RoundTrip<T>(T value)
    {
        var serializer = Serializer();
        return serializer.Deserialize<T>(serializer.SerializeToArray(value));
    }

    [Test]
    public void A_channel_carries_everything_it_was_holding()
    {
        var streamer = Guid.NewGuid();
        var drawer   = Guid.NewGuid();
        var sender   = Guid.NewGuid();
        var typing   = Guid.NewGuid();
        var sentAt   = DateTimeOffset.UtcNow.AddSeconds(-3);

        var original = new ChannelActivationState
        {
            DedupTrustedUntil = DateTimeOffset.UtcNow.AddMinutes(2),
            SentByRandomId    = new Dictionary<long, long> { [77] = 1234 },
            LastSentBySender  = new Dictionary<Guid, DateTimeOffset> { [sender] = sentAt },
            CapSecond         = DateTimeOffset.UtcNow,
            CapAccepted       = 9,
            DrawingSession    = new DrawingSessionState("session-1", streamer, [drawer]),
            BotTyping         = [typing]
        };

        var carried = RoundTrip(original);

        Assert.Multiple(() =>
        {
            Assert.That(carried.DedupTrustedUntil, Is.EqualTo(original.DedupTrustedUntil));
            Assert.That(carried.SentByRandomId, Is.EqualTo(original.SentByRandomId));
            Assert.That(carried.LastSentBySender, Is.EqualTo(original.LastSentBySender));
            Assert.That(carried.CapSecond, Is.EqualTo(original.CapSecond));
            Assert.That(carried.CapAccepted, Is.EqualTo(original.CapAccepted));
            Assert.That(carried.BotTyping, Is.EqualTo(original.BotTyping));

            Assert.That(carried.DrawingSession, Is.Not.Null);
            Assert.That(carried.DrawingSession!.SessionId, Is.EqualTo("session-1"));
            Assert.That(carried.DrawingSession.StreamerId, Is.EqualTo(streamer));
            Assert.That(carried.DrawingSession.AllowedDrawers, Is.EquivalentTo(new[] { drawer }));
        });
    }

    /// <summary>
    /// The empty case, which is what most moves actually carry.
    /// </summary>
    /// <remarks>
    /// Worth its own test because the collections are initialised rather than nullable: a round trip
    /// that turned them into nulls would only be found by whichever call site enumerated one first.
    /// </remarks>
    [Test]
    public void A_channel_with_nothing_in_it_still_arrives_usable()
    {
        var carried = RoundTrip(new ChannelActivationState());

        Assert.Multiple(() =>
        {
            Assert.That(carried.SentByRandomId, Is.Empty);
            Assert.That(carried.LastSentBySender, Is.Empty);
            Assert.That(carried.BotTyping, Is.Empty);
            Assert.That(carried.DrawingSession, Is.Null);
        });
    }

    /// <summary>
    /// The session's half, which the migration fixture cannot reach.
    /// </summary>
    /// <remarks>
    /// <c>IUserSessionGrain</c> exposes no read of its connection set — <c>HeartBeatAsync</c> re-adds
    /// whatever it is given, by design — so an end-to-end test could not tell a set that survived the
    /// move from one that was rebuilt. This asserts the half that is testable: that the set travels.
    /// </remarks>
    [Test]
    public void A_session_carries_its_connections_and_its_status_budget()
    {
        var original = new UserSessionActivationState
        {
            Connections                = ["conn-a", "conn-b"],
            SessionStarted             = true,
            PreferredStatus            = UserStatus.DoNotDisturb,
            SessionStartTime           = DateTime.UtcNow.AddMinutes(-20),
            LastDebouncedHeartbeatTime = DateTime.UtcNow.AddSeconds(-5),
            StatusTokens               = 2.5,
            StatusTokensUpdatedAt      = DateTime.UtcNow.AddSeconds(-1)
        };

        var carried = RoundTrip(original);

        Assert.Multiple(() =>
        {
            Assert.That(carried.Connections, Is.EquivalentTo(original.Connections));
            Assert.That(carried.SessionStarted, Is.True);
            Assert.That(carried.PreferredStatus, Is.EqualTo(UserStatus.DoNotDisturb));
            Assert.That(carried.SessionStartTime, Is.EqualTo(original.SessionStartTime));
            Assert.That(carried.LastDebouncedHeartbeatTime, Is.EqualTo(original.LastDebouncedHeartbeatTime));

            // Carried so a move does not hand the client a fresh budget to spend on status flapping.
            Assert.That(carried.StatusTokens, Is.EqualTo(2.5));
            Assert.That(carried.StatusTokensUpdatedAt, Is.EqualTo(original.StatusTokensUpdatedAt));
        });
    }

    [Test]
    public void A_session_that_never_started_arrives_usable()
    {
        var carried = RoundTrip(new UserSessionActivationState());

        Assert.Multiple(() =>
        {
            Assert.That(carried.Connections, Is.Empty);
            Assert.That(carried.SessionStarted, Is.False);
            Assert.That(carried.PreferredStatus, Is.Null);
        });
    }
}

/// <summary>
/// The storage that stores nothing.
/// </summary>
/// <remarks>
/// Its whole reason to exist is that Orleans carries an <c>IPersistentState</c> across a migration
/// and skips the read on the far side, so state declared against it travels without a line of code
/// in the grain. What it must never do is look like persistence.
/// </remarks>
[TestFixture]
public class VolatileGrainStorageTests
{
    private static readonly GrainId AnyGrain = GrainId.Create("channel", Guid.NewGuid().ToString("N"));

    [Test]
    public async Task A_read_reports_that_nothing_is_stored()
    {
        var state = new GrainState<ChannelActivationState>(new ChannelActivationState { CapAccepted = 7 })
        {
            ETag        = "stale",
            RecordExists = true
        };

        await new VolatileGrainStorage().ReadStateAsync("activation", AnyGrain, state);

        Assert.Multiple(() =>
        {
            // Quietly, because the runtime calls this on every activation before any grain code runs
            // — a throw here would stop the grain activating at all.
            Assert.That(state.RecordExists, Is.False);
            Assert.That(state.ETag, Is.Null);

            // And without touching what is there: a migrated activation arrives holding its state.
            Assert.That(state.State.CapAccepted, Is.EqualTo(7));
        });
    }

    /// <summary>
    /// A write refuses rather than being discarded.
    /// </summary>
    /// <remarks>
    /// Nothing in the runtime writes; only grain code does, and only deliberately. Accepting it
    /// silently would make <c>WriteStateAsync</c> read as persistence and behave as a dropped write —
    /// the kind of loss that surfaces months later as a support ticket rather than as a failure.
    /// </remarks>
    [Test]
    public void A_write_is_refused_rather_than_swallowed()
    {
        var storage = new VolatileGrainStorage();
        var state   = new GrainState<ChannelActivationState>(new ChannelActivationState());

        Assert.Multiple(() =>
        {
            Assert.That(() => storage.WriteStateAsync("activation", AnyGrain, state),
                Throws.TypeOf<NotSupportedException>());
            Assert.That(() => storage.ClearStateAsync("activation", AnyGrain, state),
                Throws.TypeOf<NotSupportedException>());
        });
    }
}
