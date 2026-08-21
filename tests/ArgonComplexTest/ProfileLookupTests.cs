namespace ArgonComplexTest.Tests;

using ArgonContracts;
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
