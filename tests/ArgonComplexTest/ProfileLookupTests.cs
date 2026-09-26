namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Resolving somebody who is not in a space with you — the direct-message and friends card.
/// </summary>
/// <remarks>
/// <para><c>PrefetchUser</c> and <c>PrefetchProfile</c> hang off <c>ServerInteraction(spaceId)</c>,
/// and that space id was doing two jobs at once: choosing which space's nickname and roles to show,
/// and proving the caller had met this person at all. A DM has no space to name, so the card could
/// not be built there — but simply dropping the parameter would drop the second job with the first
/// and turn a bare user id into a directory walk.</para>
///
/// <para>So the interesting tests here are the refusals. A stranger must stay unreachable, and a
/// block must survive whichever way round it was made; the successes only prove the feature exists,
/// while these prove it did not cost anything.</para>
/// </remarks>
[TestFixture]
public class ProfileLookupTests : TestBase
{
    private IUserInteraction Users(IServiceProvider provider)
        => IonClient.ForService<IUserInteraction>(provider);

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_OnYourself_Answers(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var me     = await Users(scope.ServiceProvider).GetMe(ct);
        var result = await Users(scope.ServiceProvider).LookupUser(me.userId, ct);

        Assert.That(result, Is.InstanceOf<SuccessLookupUser>());
        Assert.That(((SuccessLookupUser)result).user.userId, Is.EqualTo(me.userId));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_OnPlatformAccounts_AnswersForAFreshAccount(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        // A new account has sent nothing to Echo and shares nothing with System, yet both are on its
        // screen from the first launch: the pinned echo chat and system messages.
        var system = await Users(scope.ServiceProvider).LookupUser(Guid.Parse("11111111-2222-1111-2222-111111111111"), ct);
        var echo   = await Users(scope.ServiceProvider).LookupUser(Guid.Parse("44444444-2222-1111-2222-444444444444"), ct);

        Assert.That(system, Is.InstanceOf<SuccessLookupUser>());
        Assert.That(((SuccessLookupUser)system).user.username, Is.EqualTo("system"));
        Assert.That(echo, Is.InstanceOf<SuccessLookupUser>());
        Assert.That(((SuccessLookupUser)echo).user.username, Is.EqualTo("echo"));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_OnABot_AnswersWithoutAnAnchorUntilBlocked(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var owner = await CreateSessionAsync(ct);
        var teams = scope.ServiceProvider.GetRequiredService<IGrainFactory>().GetGrain<IDevTeamsGrain>(Guid.Empty);
        var tag   = Guid.NewGuid().ToString("N")[..12];
        var team  = await teams.CreateTeamAsync(owner.UserId, $"lookup-{tag}", ct);
        var app   = await teams.CreateBotAppAsync(team.teamId, "Lookup Bot", $"lookup{tag}bot", ct);

        await using var db = await AccountSeed.NewDbAsync(ct);
        var botUserId = await db.BotEntities.Where(b => b.AppId == app.appId).Select(b => b.BotAsUserId).FirstAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var found = await Users(scope.ServiceProvider).LookupUser(botUserId, ct);

        Assert.That(found, Is.InstanceOf<SuccessLookupUser>());
        Assert.That(((SuccessLookupUser)found).user.userId, Is.EqualTo(botUserId));
        Assert.That(((SuccessLookupUser)found).user.flags.HasFlag(UserFlag.BOT), Is.True);

        await GetFriendsService(scope.ServiceProvider).BlockUser(botUserId, ct);

        var afterBlock = await Users(scope.ServiceProvider).LookupUser(botUserId, ct);

        Assert.That(afterBlock, Is.InstanceOf<FailedLookupUser>());
        Assert.That(((FailedLookupUser)afterBlock).error, Is.EqualTo(LookupError.NO_ANCHOR));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_OnACompleteStranger_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var stranger = await CreateSessionAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        // No shared space, no friendship, no request, no conversation. This is the case the space
        // id used to make impossible, and it has to stay impossible.
        var result = await Users(scope.ServiceProvider).LookupUser(stranger.UserId, ct);

        Assert.That(result, Is.InstanceOf<FailedLookupUser>());
        Assert.That(((FailedLookupUser)result).error, Is.EqualTo(LookupError.NO_ANCHOR));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupProfile_OnACompleteStranger_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var stranger = await CreateSessionAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var result = await Users(scope.ServiceProvider).LookupProfile(stranger.UserId, ct);

        // The profile is the richer of the two and the one worth harvesting, so it gets its own test
        // rather than trusting that it shares a code path with LookupUser.
        Assert.That(result, Is.InstanceOf<FailedLookupProfile>());
        Assert.That(((FailedLookupProfile)result).error, Is.EqualTo(LookupError.NO_ANCHOR));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_AfterAFriendRequest_Answers(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var target = await CreateSessionAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var sent = await GetFriendsService(scope.ServiceProvider)
           .SendFriendRequest(target.Credentials.username, ct);

        Assert.That(sent, Is.EqualTo(SendFriendStatus.SuccessSent).Or.EqualTo(SendFriendStatus.AutoAccepted));

        // A pending request is an anchor on purpose: the person being asked has to be able to see
        // who is asking before deciding, and the asker has just named them.
        var result = await Users(scope.ServiceProvider).LookupUser(target.UserId, ct);

        Assert.That(result, Is.InstanceOf<SuccessLookupUser>());
        Assert.That(((SuccessLookupUser)result).user.userId, Is.EqualTo(target.UserId));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_FromTheOtherSideOfARequest_Answers(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var requester = await CreateSessionAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await Users(scope.ServiceProvider).GetMe(ct);

        await requester.Friends.SendFriendRequest(me.username, ct);

        // The direction of the request must not decide who can see whom — the receiver is the one
        // who most needs the card.
        var result = await Users(scope.ServiceProvider).LookupUser(requester.UserId, ct);

        Assert.That(result, Is.InstanceOf<SuccessLookupUser>());
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_WhenBlocked_IsRefused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var other = await CreateSessionAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await Users(scope.ServiceProvider).GetMe(ct);

        // Build a genuine anchor first, so the refusal below can only be the block.
        await other.Friends.SendFriendRequest(me.username, ct);

        var beforeBlock = await Users(scope.ServiceProvider).LookupUser(other.UserId, ct);
        Assert.That(beforeBlock, Is.InstanceOf<SuccessLookupUser>(), "the request should have been an anchor");

        await other.Friends.BlockUser(me.userId, ct);

        // Blocked by them, not by me: a block has to hold from the side that did not make it, or it
        // is only a mute.
        var afterBlock = await Users(scope.ServiceProvider).LookupUser(other.UserId, ct);

        Assert.That(afterBlock, Is.InstanceOf<FailedLookupUser>());
        Assert.That(((FailedLookupUser)afterBlock).error, Is.EqualTo(LookupError.NO_ANCHOR));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupProfile_WithAnAnchor_CarriesTheProfile(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var target = await CreateSessionAsync(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        await GetFriendsService(scope.ServiceProvider).SendFriendRequest(target.Credentials.username, ct);

        var result = await Users(scope.ServiceProvider).LookupProfile(target.UserId, ct);

        Assert.That(result, Is.InstanceOf<SuccessLookupProfile>());
        Assert.That(((SuccessLookupProfile)result).profile.userId, Is.EqualTo(target.UserId));
    }

    [Test, CancelAfter(120_000)]
    public async Task LookupUser_WithAnUnknownId_IsRefusedWithoutSayingWhy(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var result = await Users(scope.ServiceProvider).LookupUser(Guid.NewGuid(), ct);

        // NO_ANCHOR rather than NOT_FOUND, and that ordering is the point: answering "no such user"
        // for unknown ids and "not allowed" for real ones would turn this into an existence oracle.
        Assert.That(result, Is.InstanceOf<FailedLookupUser>());
        Assert.That(((FailedLookupUser)result).error, Is.EqualTo(LookupError.NO_ANCHOR));
    }
}
