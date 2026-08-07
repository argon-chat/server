namespace ArgonSharedLogicTest;

using Argon.Sfu;
using Livekit.Server.Sdk.Dotnet;

/// <summary>
/// The identifiers and grants Argon hands to LiveKit. Room ids round-trip through a string that
/// travels in a signed token, and the grant sets decide what a participant may do once inside — both
/// are security-relevant and neither was covered.
/// </summary>
[TestFixture]
public class SfuIdentityTests
{
    // ── ArgonRoomId ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void RoomId_FromAChannel_RendersAsPrefixSlashShard()
    {
        var spaceId   = Guid.NewGuid();
        var channelId = Guid.NewGuid();

        var roomId = ArgonRoomId.FromArgonChannel(spaceId, channelId);

        Assert.Multiple(() =>
        {
            Assert.That(roomId.PrefixId, Is.EqualTo(spaceId));
            Assert.That(roomId.ShardId, Is.EqualTo(channelId));
            Assert.That(roomId.ToRawRoomId(), Is.EqualTo($"{spaceId}/{channelId}"));
        });
    }

    [Test]
    public void RoomId_RoundTripsThroughItsRawForm()
    {
        var original = ArgonRoomId.FromArgonChannel(Guid.NewGuid(), Guid.NewGuid());

        var parsed = ArgonRoomId.FromMeetId(original.ToRawRoomId());

        Assert.That(parsed, Is.EqualTo(original));
    }

    [Test]
    public void RoomId_FromAMeetIdWithAnUnparsablePrefix_Throws()
        => Assert.Throws<FormatException>(() => ArgonRoomId.FromMeetId($"not-a-guid/{Guid.NewGuid()}"));

    [Test]
    public void RoomId_FromAMeetIdWithAnUnparsableShard_Throws()
        => Assert.Throws<FormatException>(() => ArgonRoomId.FromMeetId($"{Guid.NewGuid()}/not-a-guid"));

    [Test]
    public void RoomId_FromAMeetIdWithNoSeparator_Throws()
        // Split('/') on a bare guid yields the same value for first and last, so the shard parse is
        // the only thing standing between a malformed id and a room nobody expected.
        => Assert.Throws<FormatException>(() => ArgonRoomId.FromMeetId("no-separator-here"));

    [Test]
    public void RoomId_ForAChannel_IsNotALinkedMeeting()
    {
        // Linked meetings are flagged by a 0xFFFFFFFF prefix; an ordinary space id is not one.
        var roomId = ArgonRoomId.FromArgonChannel(Guid.NewGuid(), Guid.NewGuid());

        Assert.That(roomId.IsNotLinkedMeetId(), Is.False);
    }

    // ── ArgonUserId ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void UserId_ConvertsImplicitlyFromAGuid()
    {
        var id = Guid.NewGuid();
        ArgonUserId userId = id;

        Assert.Multiple(() =>
        {
            Assert.That(userId.id, Is.EqualTo(id));
            Assert.That(userId.ToRawIdentity(), Is.EqualTo(id.ToString()));
        });
    }

    [Test]
    public void UserId_ForARegisteredUser_IsNotAGuest()
        => Assert.That(new ArgonUserId(Guid.NewGuid()).IsGuest, Is.False);

    // ── Grants ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void DefaultUserGrants_AllowPublishAndSubscribeInOneRoomOnly()
    {
        var room   = ArgonRoomId.FromArgonChannel(Guid.NewGuid(), Guid.NewGuid()).ToRawRoomId();
        var grants = SfuPermission.DefaultUser(room);

        Assert.Multiple(() =>
        {
            Assert.That(grants.Room, Is.EqualTo(room), "the grant is scoped to a single room");
            Assert.That(grants.RoomJoin, Is.True);
            Assert.That(grants.CanPublish, Is.True);
            Assert.That(grants.CanSubscribe, Is.True);

            // A plain participant must not receive moderation powers.
            Assert.That(grants.RoomAdmin, Is.False);
            Assert.That(grants.RoomRecord, Is.False);
            Assert.That(grants.Hidden, Is.False);
        });
    }

    [Test]
    public void DefaultAdminGrants_AddModerationOnTopOfTheUserGrants()
    {
        var room   = ArgonRoomId.FromArgonChannel(Guid.NewGuid(), Guid.NewGuid()).ToRawRoomId();
        var grants = SfuPermission.DefaultAdmin(room);

        Assert.Multiple(() =>
        {
            Assert.That(grants.RoomAdmin, Is.True);
            Assert.That(grants.RoomRecord, Is.True);
            Assert.That(grants.Recorder, Is.True);
            Assert.That(grants.Hidden, Is.True);
            Assert.That(grants.Room, Is.EqualTo(room));
        });
    }

    [Test]
    public void BotGrants_AreTheSameAsAPlainUsers()
    {
        // A bot in a voice channel is a participant, not a moderator — this is what stops a bot
        // token from being escalated into recording or room administration.
        var room = ArgonRoomId.FromArgonChannel(Guid.NewGuid(), Guid.NewGuid()).ToRawRoomId();

        var bot  = SfuPermission.For(SfuPermissionKind.DefaultBot, room);
        var user = SfuPermission.For(SfuPermissionKind.DefaultUser, room);

        Assert.Multiple(() =>
        {
            Assert.That(bot.RoomAdmin, Is.EqualTo(user.RoomAdmin));
            Assert.That(bot.CanPublish, Is.EqualTo(user.CanPublish));
            Assert.That(bot.RoomRecord, Is.False);
        });
    }

    [Test]
    public void For_WithAnUnknownKind_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SfuPermission.For((SfuPermissionKind)999, "room"));

    // ── Track sources ───────────────────────────────────────────────────────────────────────────

    [Test]
    public void TrackSource_MapsToTheLiveKitWireNames(
        [Values(TrackSource.Camera, TrackSource.Microphone, TrackSource.ScreenShare, TrackSource.ScreenShareAudio)]
        TrackSource source)
    {
        var expected = source switch
        {
            TrackSource.Camera           => "camera",
            TrackSource.Microphone       => "microphone",
            TrackSource.ScreenShare      => "screen_share",
            _                            => "screen_share_audio"
        };

        Assert.That(source.ToFormatString(), Is.EqualTo(expected));
    }

    [Test]
    public void TrackSource_Unknown_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ((TrackSource)999).ToFormatString());
}
