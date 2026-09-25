namespace ArgonSharedLogicTest;

using System.Text;
using System.Text.Json;
using Argon.Sfu;

/// <summary>
/// The radio room, the radio identity and the token a broadcaster connects with. The names are what
/// keeps radio traffic out of the channel rosters (the webhook parser reads neither), and the token
/// is what limits a broadcaster to publishing a microphone into one room.
/// </summary>
[TestFixture]
public class SfuRadioTests
{
    private static readonly SfuInstanceCfg Sfu = new()
    {
        Region     = "test",
        ClientId   = "test-api-key",
        Secret     = "test-secret-key-that-is-long-enough-to-be-256-bits-minimum-for-livekit",
        PublicUrl  = "ws://localhost:7880",
        CommandUrl = "http://localhost:7880",
        Geo        = new GeoPosition(0, 0)
    };

    // ── RadioRoomId ─────────────────────────────────────────────────────────────────────────────

    [Test]
    public void RadioRoomId_RendersWithThePrefixAndRoundTrips()
    {
        var room = new RadioRoomId(Guid.NewGuid(), Guid.NewGuid());
        var raw  = room.ToRawRoomId();

        Assert.Multiple(() =>
        {
            Assert.That(raw, Is.EqualTo($"radio/{room.SpaceId}/{room.ChannelId}"));
            Assert.That(raw, Has.Length.Not.EqualTo(73), "the webhook reads only 73-character names as channel rooms");
            Assert.That(RadioRoomId.TryParse(raw, out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(room));
        });
    }

    [Test]
    public void RadioRoomId_RejectsChannelRoomsAndGarbage()
    {
        var channelRoom = ArgonRoomId.FromArgonChannel(Guid.NewGuid(), Guid.NewGuid()).ToRawRoomId();

        Assert.Multiple(() =>
        {
            Assert.That(RadioRoomId.TryParse(channelRoom, out _), Is.False);
            Assert.That(RadioRoomId.TryParse($"radio/{Guid.NewGuid()}", out _), Is.False);
            Assert.That(RadioRoomId.TryParse($"radio/not-a-guid/{Guid.NewGuid()}", out _), Is.False);
            Assert.That(RadioRoomId.TryParse(null, out _), Is.False);
        });
    }

    // ── RadioIdentity ───────────────────────────────────────────────────────────────────────────

    [Test]
    public void RadioIdentity_RendersWithThePrefixAndRoundTrips()
    {
        var identity = new RadioIdentity(Guid.NewGuid());
        var raw      = identity.ToRawIdentity();

        Assert.Multiple(() =>
        {
            Assert.That(raw, Is.EqualTo($"bc:{identity.UserId}"));
            Assert.That(Guid.TryParse(raw, out _), Is.False, "a radio identity must never read as a member");
            Assert.That(RadioIdentity.TryParse(raw, out var parsed), Is.True);
            Assert.That(parsed, Is.EqualTo(identity));
            Assert.That(RadioIdentity.TryParse(identity.UserId.ToString(), out _), Is.False);
            Assert.That(RadioIdentity.TryParse("bc:nope", out _), Is.False);
        });
    }

    // ── RadioToken ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void RadioToken_PublishesAMicrophoneIntoTheRadioRoomAndHearsNothing()
    {
        var userId  = Guid.NewGuid();
        var spaceId = Guid.NewGuid();
        var hq      = Guid.NewGuid();

        var payload = Payload(RadioToken.Create(Sfu, userId, spaceId, hq));
        var video   = payload.GetProperty("video");

        Assert.Multiple(() =>
        {
            Assert.That(payload.GetProperty("sub").GetString(), Is.EqualTo($"bc:{userId}"));
            Assert.That(payload.GetProperty("name").GetString(), Is.EqualTo($"bc:{userId}"));
            Assert.That(video.GetProperty("room").GetString(), Is.EqualTo($"radio/{spaceId}/{hq}"));
            Assert.That(video.GetProperty("roomJoin").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("canPublish").GetBoolean(), Is.True);
            Assert.That(video.GetProperty("canPublishSources").EnumerateArray().Select(x => x.GetString()),
                Is.EqualTo(new[] { "microphone" }));
            Assert.That(video.GetProperty("canSubscribe").GetBoolean(), Is.False);
            Assert.That(video.GetProperty("canPublishData").GetBoolean(), Is.False);
            Assert.That(video.GetProperty("canUpdateOwnMetadata").GetBoolean(), Is.False);
            Assert.That(video.GetProperty("hidden").GetBoolean(), Is.False);
            Assert.That(video.GetProperty("roomAdmin").GetBoolean(), Is.False);
        });
    }

    [Test]
    public void RadioToken_CarriesTheArgonAttributesAndLivesFifteenMinutes()
    {
        var userId = Guid.NewGuid();
        var hq     = Guid.NewGuid();

        var payload    = Payload(RadioToken.Create(Sfu, userId, Guid.NewGuid(), hq));
        var attributes = payload.GetProperty("attributes");
        var lifetime   = payload.GetProperty("exp").GetDouble() - payload.GetProperty("nbf").GetDouble();

        Assert.Multiple(() =>
        {
            Assert.That(attributes.GetProperty(RadioToken.KindAttribute).GetString(), Is.EqualTo("radio"));
            Assert.That(attributes.GetProperty(RadioToken.UserAttribute).GetString(), Is.EqualTo(userId.ToString()));
            Assert.That(attributes.GetProperty(RadioToken.BroadcastAttribute).GetString(), Is.EqualTo(hq.ToString()));
            Assert.That(lifetime, Is.EqualTo(15 * 60).Within(5));
        });
    }

    /// <summary>The decoded payload of a LiveKit token.</summary>
    private static JsonElement Payload(string token)
    {
        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        return JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload))).RootElement.Clone();
    }
}
