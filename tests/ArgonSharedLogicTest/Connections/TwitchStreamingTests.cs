namespace ArgonSharedLogicTest.Connections;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Features.Integrations.Connections.Twitch;
using Argon.Grains;
using ArgonContracts;

/// <summary>
/// Twitch's streaming status: the EventSub envelope, what a notification says, and the activity
/// a live stream becomes.
/// </summary>
[TestFixture]
public class TwitchStreamingTests
{
    private const string Secret = "a-secret-of-reasonable-length";

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static TwitchEventSubMessage Signed(string type, string body, DateTimeOffset at, string secret = Secret, string id = "msg-1")
    {
        var timestamp = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        var signature = "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(id + timestamp + body))).ToLowerInvariant();

        return new TwitchEventSubMessage(id, type, timestamp, signature, "stream.online", body);
    }

    [Test]
    public void A_message_verifies_only_with_the_secret_it_was_signed_with_and_inside_the_replay_window()
    {
        var body    = """{"challenge":"pogchamp","subscription":{"type":"stream.online"}}""";
        var message = Signed("webhook_callback_verification", body, Now);

        Assert.Multiple(() =>
        {
            Assert.That(TwitchEventSub.VerifySignature(Secret, message, Now), Is.True);
            Assert.That(TwitchEventSub.VerifySignature(Secret, message, Now.AddMinutes(5)), Is.True);
            Assert.That(TwitchEventSub.VerifySignature("another-secret-entirely", message, Now), Is.False);
            Assert.That(TwitchEventSub.VerifySignature(Secret, message with { Body = body + " " }, Now), Is.False, "the body is signed");
            Assert.That(TwitchEventSub.VerifySignature(Secret, message, Now.AddMinutes(11)), Is.False, "ten minutes is the replay window");
            Assert.That(TwitchEventSub.VerifySignature("", message, Now), Is.False, "no secret, nothing verifies");
            Assert.That(TwitchEventSub.VerifySignature(Secret, message with { Signature = "" }, Now), Is.False);
            Assert.That(TwitchEventSub.Challenge(body), Is.EqualTo("pogchamp"));
        });
    }

    [Test]
    public void A_notification_names_its_type_and_broadcaster()
    {
        var online = """
            {"subscription":{"id":"s1","type":"stream.online","condition":{"broadcaster_user_id":"141981764"}},
             "event":{"id":"9001","broadcaster_user_id":"141981764","broadcaster_user_login":"twitchdev","type":"live","started_at":"2026-10-01T10:00:00Z"}}
            """;

        var offline = """{"subscription":{"type":"stream.offline","condition":{"broadcaster_user_id":"141981764"}},"event":{"broadcaster_user_login":"twitchdev"}}""";

        Assert.Multiple(() =>
        {
            Assert.That(TwitchEventSub.Notification(online), Is.EqualTo(("stream.online", "141981764")));
            Assert.That(TwitchEventSub.Notification(offline), Is.EqualTo(("stream.offline", "141981764")), "the condition names the broadcaster when the event does not");
            Assert.That(TwitchEventSub.Notification("{}"), Is.Null);
            Assert.That(TwitchEventSub.Notification("<html>"), Is.Null);
            Assert.That(TwitchEventSub.Challenge("{}"), Is.Null);
        });
    }

    [Test]
    public void A_live_stream_is_read_and_an_offline_channel_is_null()
    {
        var live = JsonDocument.Parse("""
            {"data":[{"id":"1","user_id":"141981764","user_login":"twitchdev","user_name":"TwitchDev","game_name":"Science & Technology",
                      "type":"live","title":"Building EventSub","viewer_count":42,"started_at":"2026-10-01T10:00:00Z"}]}
            """).RootElement;

        var stream = TwitchEventSubClient.ParseStream(live);

        Assert.That(stream, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(stream!.BroadcasterId, Is.EqualTo("141981764"));
            Assert.That(stream.Login, Is.EqualTo("twitchdev"));
            Assert.That(stream.Title, Is.EqualTo("Building EventSub"));
            Assert.That(stream.Game, Is.EqualTo("Science & Technology"));
            Assert.That(stream.Url, Is.EqualTo("https://twitch.tv/twitchdev"));
            Assert.That(stream.StartedAt, Is.EqualTo(new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero)));
            Assert.That(TwitchEventSubClient.ParseStream(JsonDocument.Parse("""{"data":[]}""").RootElement), Is.Null);
            Assert.That(TwitchEventSubClient.ParseSubscriptionIds("""{"data":[{"id":"a"},{"id":"b"}],"total":2}"""), Is.EqualTo(new[] { "a", "b" }));
        });
    }

    [Test]
    public void The_activity_is_streaming_with_the_title_the_game_and_the_channel_link()
    {
        var stream   = new TwitchStream("141981764", "twitchdev", "TwitchDev", "Building EventSub", "Science & Technology", Now);
        var presence = TwitchPresenceGrain.ToPresence(stream);

        Assert.Multiple(() =>
        {
            Assert.That(presence.kind, Is.EqualTo(ActivityPresenceKind.STREAMING));
            Assert.That(presence.source, Is.EqualTo(ArgonContracts.ActivitySource.TWITCH));
            Assert.That(presence.titleName, Is.EqualTo("Building EventSub · Science & Technology"));
            Assert.That(presence.startTimestampSeconds, Is.EqualTo((ulong)Now.ToUnixTimeSeconds()));
            Assert.That(presence.url, Is.EqualTo("https://twitch.tv/twitchdev"));
            Assert.That(presence.spotify, Is.Null);
            Assert.That(TwitchPresenceGrain.ToPresence(stream with { Game = "" }).titleName, Is.EqualTo("Building EventSub"));
            Assert.That(TwitchPresenceGrain.ToPresence(stream with { Game = "", Title = "" }).titleName, Is.EqualTo("TwitchDev"));
        });
    }
}
