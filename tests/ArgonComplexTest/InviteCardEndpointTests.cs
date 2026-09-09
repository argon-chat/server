namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ArgonContracts;

/// <summary>
/// The invite card as the web reads it: over plain HTTP, with nobody signed in.
/// </summary>
/// <remarks>
/// <para>Everything else that resolves an invite does it over Ion, for a caller who already holds a
/// session. The landing page at <c>argon.gl/i/{code}</c> — and the chat client unfurling that link
/// into a conversation — has neither, and a card that only worked for members would leave both of
/// them showing "somebody invited you somewhere". So these go through <see cref="TestBase.HttpClient"/>,
/// which carries no token, rather than through a session's services.</para>
///
/// <para>The revocation case is the one that guards the cache rather than the endpoint: the card is
/// held for an hour, which is only safe because revoking a link drops it.</para>
/// </remarks>
[TestFixture]
public class InviteCardEndpointTests : TestBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record CardSpace(
        Guid    Id,
        string  Name,
        string  Description,
        string? AvatarUrl,
        string? BannerUrl,
        string? SplashUrl,
        bool    IsVerified,
        bool    IsOfficial,
        bool    IsCommunity,
        int     MemberCount,
        int     OnlineCount);

    private sealed record CardVoice(Guid Id, string Name);

    private sealed record Card(string Code, string Kind, string DeepLink, CardSpace Space, CardVoice? VoiceChannel);

    private sealed record CardError(string Error);

    private async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Invited from the web", string.Empty), ct);

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
            new CreateChannelRequest(spaceId, name, ChannelType.Voice, "Test channel", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    private async Task<(HttpStatusCode status, Card? card, CardError? error)> ReadCardAsync(string code, CancellationToken ct)
    {
        using var response = await HttpClient.GetAsync($"/api/invite/{code}", ct);

        if (response.StatusCode is HttpStatusCode.OK)
            return (response.StatusCode, await response.Content.ReadFromJsonAsync<Card>(Json, ct), null);

        return (response.StatusCode, null, await response.Content.ReadFromJsonAsync<CardError>(Json, ct));
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task ASpaceInvite_ResolvesToACardWithNobodySignedIn(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Card Space", ct);
        var code    = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);

        var (status, card, error) = await ReadCardAsync(code.inviteCode, ct);

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), $"card refused: {error?.Error}");
        Assert.That(card, Is.Not.Null);

        Assert.Multiple(() =>
        {
            Assert.That(card!.Space.Id, Is.EqualTo(spaceId));
            Assert.That(card.Space.Name, Is.EqualTo("Card Space"));
            // The creator is a member, so a card that reports nobody is reporting the wrong space.
            Assert.That(card.Space.MemberCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(card.Kind, Is.EqualTo("space"));
            // A plain invite must not claim to point at a room, or every space link would drop the
            // person somewhere nobody chose.
            Assert.That(card.VoiceChannel, Is.Null);
            Assert.That(card.DeepLink, Is.EqualTo($"argon://invite/{code.inviteCode}"));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task AVoiceInvite_NamesTheRoomItWasMintedFor(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, "Card Space With A Room", ct);
        var channelId = await CreateVoiceChannelAsync(owner, spaceId, "rocket-deck", ct);

        var created = await owner.Channels.CreateVoiceInviteCode(spaceId, channelId, 60, 0, ct);

        Assert.That(created, Is.InstanceOf<SuccessCreateVoiceInvite>(),
            $"Could not mint a room link: {(created as FailedCreateVoiceInvite)?.error}");

        var invite = (SuccessCreateVoiceInvite)created;

        var (status, card, error) = await ReadCardAsync(invite.code.inviteCode, ct);

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), $"card refused: {error?.Error}");
        Assert.That(card, Is.Not.Null);

        Assert.Multiple(() =>
        {
            // The room is the whole reason this link shape exists: the page has to be able to say
            // "join rocket-deck" before anyone commits to it.
            Assert.That(card!.Kind, Is.EqualTo("voice"));
            Assert.That(card.VoiceChannel, Is.Not.Null);
            Assert.That(card.VoiceChannel!.Id, Is.EqualTo(channelId));
            Assert.That(card.VoiceChannel.Name, Is.EqualTo("rocket-deck"));
            Assert.That(card.Space.Id, Is.EqualTo(spaceId));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task ACodeNobodyIssued_IsNotFound(CancellationToken ct = default)
    {
        // Well-formed but never minted: the interesting refusal is the one that got as far as a
        // lookup, not the one the format check turned away.
        var (status, _, error) = await ReadCardAsync("ZZZ-ZZZ-ZZZ", ct);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(error?.Error, Is.EqualTo("NOT_FOUND"));
        });
    }

    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task ARevokedInvite_StopsBeingServed(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Card Space To Revoke", ct);
        var code    = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);

        var (before, _, error) = await ReadCardAsync(code.inviteCode, ct);
        Assert.That(before, Is.EqualTo(HttpStatusCode.OK), $"card refused before revocation: {error?.Error}");

        await owner.Servers.RevokeInviteCode(spaceId, code, ct);

        // The read above put the card in the cache for an hour. Revocation has to take it back out,
        // or a link the owner has just killed keeps advertising the space for the rest of that hour.
        var (after, _, afterError) = await ReadCardAsync(code.inviteCode, ct);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(afterError?.Error, Is.EqualTo("NOT_FOUND"));
        });
    }
}
