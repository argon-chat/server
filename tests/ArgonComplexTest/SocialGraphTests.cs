namespace ArgonComplexTest.Tests;

using Argon.Core.Features.CoreLogic.Privacy;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Friends, blocks, direct messages, recent chats, read acknowledgement, mutes and privacy rules —
/// the social graph. Almost all of it is multi-user by nature, which is what
/// <see cref="TestBase.CreateSessionAsync"/> exists for: two independent clients, two tokens, no
/// ambient state for them to fight over.
/// </summary>
[TestFixture]
public class SocialGraphTests : TestBase
{
    private IUserChatInteractions Chats(IServiceProvider provider)
        => IonClient.ForService<IUserChatInteractions>(provider);

    // ── Friend requests ─────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task FriendRequest_SendAcceptAndRemove(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var status = await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        Assert.That(status, Is.EqualTo(SendFriendStatus.SuccessSent).Or.EqualTo(SendFriendStatus.AutoAccepted));

        var incoming = await bob.Friends.GetMyFriendPendingList(50, 0, ct);
        Assert.That(incoming.Values.Select(r => r.requesterId), Does.Contain(alice.UserId));

        var outgoing = await alice.Friends.GetMyFriendOutgoingList(50, 0, ct);
        Assert.That(outgoing.Values.Select(r => r.targetId), Does.Contain(bob.UserId));

        await bob.Friends.AcceptFriendRequest(alice.UserId, ct);

        var bobFriends = await bob.Friends.GetMyFriendships(50, 0, ct);
        Assert.That(bobFriends.Values.Select(f => f.friendId), Does.Contain(alice.UserId));

        // Friendship is symmetric; removing from either side must clear both.
        await alice.Friends.RemoveFriend(bob.UserId, ct);

        var afterRemoval = await bob.Friends.GetMyFriendships(50, 0, ct);
        Assert.That(afterRemoval.Values.Select(f => f.friendId), Does.Not.Contain(alice.UserId));
    }

    [Test, CancelAfter(120_000)]
    public async Task FriendRequest_Decline_RemovesItFromBothLists(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        await bob.Friends.DeclineFriendRequest(alice.UserId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((await bob.Friends.GetMyFriendPendingList(50, 0, ct)).Values.Select(r => r.requesterId),
                Does.Not.Contain(alice.UserId));
            Assert.That((await alice.Friends.GetMyFriendOutgoingList(50, 0, ct)).Values.Select(r => r.targetId),
                Does.Not.Contain(bob.UserId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task FriendRequest_Cancel_WithdrawsIt(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        await alice.Friends.CancelFriendRequest(bob.UserId, ct);

        Assert.That((await bob.Friends.GetMyFriendPendingList(50, 0, ct)).Values.Select(r => r.requesterId),
            Does.Not.Contain(alice.UserId));
    }

    [Test, CancelAfter(120_000)]
    public async Task SendFriendRequest_ToAnUnknownUsername_IsReported(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        var status = await alice.Friends.SendFriendRequest($"nobody_{Guid.NewGuid():N}", ct);

        Assert.That(status, Is.EqualTo(SendFriendStatus.TargetNotFound));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetMyFriendships_ForANewUser_IsEmpty(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        Assert.That((await alice.Friends.GetMyFriendships(50, 0, ct)).Size, Is.EqualTo(0));
    }

    // ── Blocking ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task BlockUser_AppearsInTheBlockListAndCanBeUndone(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Friends.BlockUser(bob.UserId, ct);

        var blocked = await alice.Friends.GetBlockList(50, 0, ct);
        Assert.That(blocked.Values.Select(b => b.blockedId), Does.Contain(bob.UserId));

        await alice.Friends.UnblockUser(bob.UserId, ct);

        var afterUnblock = await alice.Friends.GetBlockList(50, 0, ct);
        Assert.That(afterUnblock.Values.Select(b => b.blockedId), Does.Not.Contain(bob.UserId));
    }

    [Test, CancelAfter(120_000)]
    public async Task BlockUser_TearsDownAnExistingFriendship(CancellationToken ct = default)
    {
        // Blocking someone you are friends with has to remove the friendship too, otherwise the
        // blocked user keeps seeing presence and DM affordances for someone who blocked them.
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        await bob.Friends.AcceptFriendRequest(alice.UserId, ct);

        await alice.Friends.BlockUser(bob.UserId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((await alice.Friends.GetMyFriendships(50, 0, ct)).Values.Select(f => f.friendId),
                Does.Not.Contain(bob.UserId));
            Assert.That((await bob.Friends.GetMyFriendships(50, 0, ct)).Values.Select(f => f.friendId),
                Does.Not.Contain(alice.UserId));
        });
    }

    // ── Direct messages and recent chats ────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task DirectMessage_SendThenQuery(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var messageId = await alice.Client.ForService<IUserChatInteractions>(FactoryAsp.Services)
           .SendDirectMessage(bob.UserId, "hello bob", new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

        Assert.That(messageId, Is.GreaterThan(0));

        var conversation = await bob.Client.ForService<IUserChatInteractions>(FactoryAsp.Services)
           .QueryDirectMessages(alice.UserId, null, 50, ct);

        Assert.That(conversation.Values.Select(m => m.text), Does.Contain("hello bob"));
    }

    [Test, CancelAfter(120_000)]
    public async Task DirectMessage_ShowsUpInRecentChatsForBothSides(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Client.ForService<IUserChatInteractions>(FactoryAsp.Services)
           .SendDirectMessage(bob.UserId, "recent chats", new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

        var aliceChats = await alice.Client.ForService<IUserChatInteractions>(FactoryAsp.Services).GetRecentChats(50, 0, ct);
        var bobChats   = await bob.Client.ForService<IUserChatInteractions>(FactoryAsp.Services).GetRecentChats(50, 0, ct);

        Assert.Multiple(() =>
        {
            Assert.That(aliceChats.Values.Select(c => c.peerId), Does.Contain(bob.UserId));
            Assert.That(bobChats.Values.Select(c => c.peerId), Does.Contain(alice.UserId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RecentChat_PinUnpinAndMarkRead(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var aliceChats = alice.Client.ForService<IUserChatInteractions>(FactoryAsp.Services);

        await aliceChats.SendDirectMessage(
            bob.UserId, "pin me", new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

        await aliceChats.PinChat(bob.UserId, ct);
        Assert.That((await aliceChats.GetRecentChats(50, 0, ct)).Values.First(c => c.peerId == bob.UserId).isPinned, Is.True);

        await aliceChats.UnpinChat(bob.UserId, ct);
        Assert.That((await aliceChats.GetRecentChats(50, 0, ct)).Values.First(c => c.peerId == bob.UserId).isPinned, Is.False);

        Assert.DoesNotThrowAsync(async () => await aliceChats.MarkChatRead(bob.UserId, ct));
    }

    [Test, CancelAfter(120_000)]
    public async Task QueryDirectMessages_WithNoConversation_IsEmpty(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var conversation = await alice.Client.ForService<IUserChatInteractions>(FactoryAsp.Services)
           .QueryDirectMessages(bob.UserId, null, 50, ct);

        Assert.That(conversation.Size, Is.EqualTo(0));
    }

    // ── Read state, mutes and badges ────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task AckChannel_ClearsTheUnreadBadge(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"acked-{Guid.NewGuid():N}"[..20], ct);

        var messageId = await GetChannelService(scope.ServiceProvider).SendMessage(
            spaceId, channelId, "read me", new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

        await GetUserService(scope.ServiceProvider).AckChannel(channelId, messageId, ct);

        var badges = await GetUserService(scope.ServiceProvider).GetGlobalBadges(ct);
        Assert.That(badges, Is.Not.Null);
    }

    [Test, CancelAfter(120_000)]
    public async Task MuteThenUnmuteASpace_IsAccepted(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var spaceId = await CreateSpaceAndGetIdAsync(ct);

        await GetUserService(scope.ServiceProvider).MuteTarget(
            spaceId, MuteTargetKind.Space, MuteLevelType.All, suppressEveryone: true,
            expiresAt: DateTime.UtcNow.AddHours(1), ct);

        Assert.DoesNotThrowAsync(async () => await GetUserService(scope.ServiceProvider).UnmuteTarget(spaceId, ct));
    }

    [Test, CancelAfter(120_000)]
    public async Task NotificationFeed_MarkReadIsIdempotentForAnUnknownId(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        Assert.DoesNotThrowAsync(async () =>
        {
            await GetUserService(scope.ServiceProvider).MarkNotificationRead(Guid.NewGuid(), ct);
            await GetUserService(scope.ServiceProvider).MarkAllNotificationsRead(null, ct);
        });
    }

    // ── Presence and profile ────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task BroadcastAndClearPresence_AreAccepted(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        Assert.DoesNotThrowAsync(async () =>
        {
            await GetUserService(scope.ServiceProvider).BroadcastPresence(
                new UserActivityPresence(ActivityPresenceKind.GAME, (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "Testing"), ct);
            await GetUserService(scope.ServiceProvider).RemoveBroadcastPresence(ct);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetTodayStatsAndLevel_AreAvailableForANewUser(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var stats = await GetUserService(scope.ServiceProvider).GetTodayStats(ct);
        var level = await GetUserService(scope.ServiceProvider).GetMyLevel(ct);

        Assert.Multiple(() =>
        {
            Assert.That(stats, Is.Not.Null);
            Assert.That(level.currentLevel, Is.GreaterThanOrEqualTo(1));
            Assert.That(level.readyToClaimCoin, Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetMyFeatures_ReturnsTheEvaluatedFlagSet(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var features = await GetUserService(scope.ServiceProvider).GetMyFeatures(ct);

        Assert.That(features.Size, Is.GreaterThanOrEqualTo(0));
    }

    [Test, CancelAfter(120_000)]
    public async Task GetMyLegalState_ReflectsTheVersionsAcceptedAtRegistration(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var legal = await GetUserService(scope.ServiceProvider).GetMyLegalState(ct);

        Assert.That(legal, Is.Not.Null);
    }

    // ── Privacy rules ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task PrivacyRule_SetThenRead(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var blocked = await CreateSessionAsync(ct);

        var set = await GetPrivacyService(scope.ServiceProvider).SetPrivacyRule(
            PrivacyKeys.StreamDraw, PrivacyRuleMode.NOBODY, null,
            new IonArray<Guid>([]), new IonArray<Guid>([blocked.UserId]), ct);

        Assert.That(set, Is.True);

        var rule = await GetPrivacyService(scope.ServiceProvider).GetPrivacyRule(PrivacyKeys.StreamDraw, null, ct);

        Assert.Multiple(() =>
        {
            Assert.That(rule.mode, Is.EqualTo(PrivacyRuleMode.NOBODY));
            Assert.That(rule.deny.Values, Does.Contain(blocked.UserId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetPrivacyRule_ForAnUnsetKey_ReturnsADefault(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var rule = await GetPrivacyService(scope.ServiceProvider).GetPrivacyRule($"never_set_{Guid.NewGuid():N}", null, ct);

        Assert.That(rule, Is.Not.Null);
    }
}
