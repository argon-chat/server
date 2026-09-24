namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using ArgonContracts;
using Google.Protobuf;
using Livekit.Server.Sdk.Dotnet;

/// <summary>
/// <c>POST /webhook-endpoint</c>: LiveKit telling the server who actually connected to or left a room.
/// </summary>
/// <remarks>
/// The request is signed the way livekit-server signs it — a JWT from the API key whose <c>sha256</c>
/// claim is the body's hash — so the verification half is the production code, not a bypass.
/// </remarks>
[TestFixture]
public class LiveKitWebhookTests : TestBase
{
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Participant_joined_and_left_drive_the_roster(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await VoiceRoomAsync(ct);
        var room = $"{spaceId}/{channelId}";

        var joined = await SendAsync(Event("participant_joined", room, owner.UserId), ct);
        var afterJoin = await OccupantsAsync(owner, spaceId, channelId, ct);

        var left = await SendAsync(Event("participant_left", room, owner.UserId), ct);
        var afterLeave = await OccupantsAsync(owner, spaceId, channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(joined, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(afterJoin, Does.Contain(owner.UserId));
            Assert.That(left, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(afterLeave, Does.Not.Contain(owner.UserId));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_side_room_is_not_mistaken_for_the_channel_its_name_starts_with(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await VoiceRoomAsync(ct);

        var status = await SendAsync(Event("participant_joined", $"{spaceId}/{channelId}~radio", owner.UserId), ct);

        Assert.Multiple(async () =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(await OccupantsAsync(owner, spaceId, channelId, ct), Does.Not.Contain(owner.UserId));
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Room_finished_clears_the_roster(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await VoiceRoomAsync(ct);
        var room = $"{spaceId}/{channelId}";
        await SendAsync(Event("participant_joined", room, owner.UserId), ct);

        var status = await SendAsync(Event("room_finished", room, null), ct);

        var occupants = await Infrastructure.Presence.Poll.ForValueAsync(
            () => OccupantsAsync(owner, spaceId, channelId, ct), users => users.Count == 0, TimeSpan.FromSeconds(15), ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(occupants, Is.Empty);
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Unsigned_or_tampered_requests_are_refused(CancellationToken ct = default)
    {
        var body = JsonFormatter.Default.Format(Event("participant_joined", $"{Guid.NewGuid()}/{Guid.NewGuid()}", Guid.NewGuid()));

        var unsigned = await PostAsync(body, authorization: null, ct);
        var tampered = await PostAsync(body, Sign(body + " "), ct);
        var garbage  = await PostAsync(body, "not-a-token", ct);

        Assert.Multiple(() =>
        {
            Assert.That(unsigned, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(tampered, Is.EqualTo(HttpStatusCode.Unauthorized));
            Assert.That(garbage, Is.EqualTo(HttpStatusCode.Unauthorized));
        });
    }

    private static WebhookEvent Event(string kind, string room, Guid? identity)
    {
        var ev = new WebhookEvent { Id = $"EV_{Guid.NewGuid():N}", Event = kind, Room = new Room { Name = room } };
        if (identity is { } id)
            ev.Participant = new ParticipantInfo { Identity = id.ToString() };
        return ev;
    }

    private Task<HttpStatusCode> SendAsync(WebhookEvent ev, CancellationToken ct)
    {
        var body = JsonFormatter.Default.Format(ev);
        return PostAsync(body, Sign(body), ct);
    }

    private async Task<HttpStatusCode> PostAsync(string body, string? authorization, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhook-endpoint")
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/webhook+json"))
        };
        if (authorization is not null)
            request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await HttpClient.SendAsync(request, ct);
        return response.StatusCode;
    }

    private static string Sign(string body)
        => new AccessToken(ArgonServerTargetHost.SfuClientId, ArgonServerTargetHost.SfuSecret)
           .WithSha256(Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(body))))
           .ToJwt();

    private async Task<(TestUserSession Owner, Guid SpaceId, Guid ChannelId)> VoiceRoomAsync(CancellationToken ct)
    {
        var owner  = await CreateSessionAsync(ct);
        var result = await owner.Users.CreateSpace(new CreateServerRequest("Webhook", "", string.Empty), ct);
        var spaceId = ((SuccessCreateSpace)result).space.spaceId;

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, "hooked", ChannelType.Voice, "", null), ct);
        var channels = await owner.Servers.GetChannels(spaceId, ct);

        return (owner, spaceId, channels.Values.First(c => c.channel.name == "hooked").channel.channelId);
    }

    private static async Task<List<Guid>> OccupantsAsync(TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var channels = await reader.Servers.GetChannels(spaceId, ct);
        return channels.Values.First(c => c.channel.channelId == channelId).users.Values.Select(u => u.userId).ToList();
    }
}
