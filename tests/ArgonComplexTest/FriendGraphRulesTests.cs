namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The rules around a friend request — who may send one, what a second or a crossing one means, what
/// a block does to it — and the ignore list beside it. <see cref="SocialGraphTests"/> has the happy
/// paths; this is everything a request can be refused or short-circuited by.
/// </summary>
[TestFixture]
public class FriendGraphRulesTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private static async Task<int> RequestRowsAsync(Guid from, Guid to, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);
        return await db.FriendRequest.CountAsync(x => x.RequesterId == from && x.TargetId == to, ct);
    }

    private static async Task<int> FriendshipRowsAsync(Guid a, Guid b, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);
        return await db.Friends.CountAsync(x => (x.UserId == a && x.FriendId == b) || (x.UserId == b && x.FriendId == a), ct);
    }

    private async Task BefriendAsync(TestUserSession a, TestUserSession b, CancellationToken ct)
    {
        Assert.That(await a.Friends.SendFriendRequest(b.Credentials.username, ct), Is.EqualTo(SendFriendStatus.SuccessSent));
        await b.Friends.AcceptFriendRequest(a.UserId, ct);
        Assert.That(await FriendshipRowsAsync(a.UserId, b.UserId, ct), Is.EqualTo(2));
    }

    // ── Sending ─────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_request_to_yourself_is_refused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        var status = await alice.Friends.SendFriendRequest(alice.Credentials.username, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(SendFriendStatus.CannotFriendYourself));
            Assert.That(await RequestRowsAsync(alice.UserId, alice.UserId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_request_reaches_the_target_by_username_in_any_case_and_both_sides_hear_of_it(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);
        await using var bobStream   = await SocialHarness.OnlineAsync(bob, ct);

        var status = await alice.Friends.SendFriendRequest(bob.Credentials.username.ToUpperInvariant(), ct);

        Assert.That(status, Is.EqualTo(SendFriendStatus.SuccessSent));

        await bobStream.WaitForRecordAsync<FriendRequestReceivedEvent>(e => e.requesterId == alice.UserId, EventWait, ct: ct);
        await aliceStream.WaitForRecordAsync<FriendRequestSentEvent>(e => e.targetId == bob.UserId, EventWait, ct: ct);

        Assert.That((await bob.Friends.GetMyFriendPendingList(50, 0, ct)).Values.Select(r => r.requesterId),
            Is.EqualTo(new[] { alice.UserId }));
    }

    [Test, CancelAfter(120_000)]
    public async Task A_request_across_a_block_is_refused_in_either_direction(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Friends.BlockUser(bob.UserId, ct);

        var fromBlocker = await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        var fromBlocked = await bob.Friends.SendFriendRequest(alice.Credentials.username, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(fromBlocker, Is.EqualTo(SendFriendStatus.Blocked));
            Assert.That(fromBlocked, Is.EqualTo(SendFriendStatus.Blocked));
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct) + await RequestRowsAsync(bob.UserId, alice.UserId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_request_to_a_friend_says_already_friends(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        var status = await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(status, Is.EqualTo(SendFriendStatus.AlreadyFriends));
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Sending_twice_says_already_sent_and_keeps_one_request(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var first  = await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        var second = await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first, Is.EqualTo(SendFriendStatus.SuccessSent));
            Assert.That(second, Is.EqualTo(SendFriendStatus.AlreadySent));
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct), Is.EqualTo(1));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Asking_someone_who_already_asked_you_makes_you_friends(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);
        await using var bobStream   = await SocialHarness.OnlineAsync(bob, ct);

        await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);

        var status = await bob.Friends.SendFriendRequest(alice.Credentials.username, ct);

        Assert.That(status, Is.EqualTo(SendFriendStatus.AutoAccepted));

        await aliceStream.WaitForRecordAsync<FriendRequestAcceptedEvent>(e => e.userId == bob.UserId, EventWait, ct: ct);
        await bobStream.WaitForRecordAsync<FriendRequestAcceptedEvent>(e => e.userId == alice.UserId, EventWait, ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await FriendshipRowsAsync(alice.UserId, bob.UserId, ct), Is.EqualTo(2), "friendship is one row per side");
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero, "the crossed request is consumed");
            Assert.That(await RequestRowsAsync(bob.UserId, alice.UserId, ct), Is.Zero, "no second request is left behind");
        });
    }

    // ── Accepting, declining, cancelling ────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Accepting_notifies_the_requester_and_opens_a_chat_on_both_sides(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        var feed = await alice.Users.GetNotificationFeed(50, null, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(feed.Values.Where(n => n.type == SystemNotificationType.FriendRequestAccepted).Select(n => n.referenceId),
                Does.Contain((Guid?)bob.UserId), "the requester is told their request was accepted");
            Assert.That((await alice.Chats.GetRecentChats(50, 0, ct)).Values.Select(c => c.peerId), Does.Contain(bob.UserId));
            Assert.That((await bob.Chats.GetRecentChats(50, 0, ct)).Values.Select(c => c.peerId), Does.Contain(alice.UserId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Accepting_a_request_nobody_sent_does_nothing(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await bob.Friends.AcceptFriendRequest(alice.UserId, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await FriendshipRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
            Assert.That((await alice.Users.GetNotificationFeed(50, null, ct)).Values
               .Any(n => n.type == SystemNotificationType.FriendRequestAccepted), Is.False);
        });
    }

    /// <summary>
    /// A request and a block between the same two people cannot both be written through the API — a
    /// block deletes the requests — so the row is seeded, standing in for a request that raced the block.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Accepting_across_a_block_drops_the_request_instead(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);

        await using (var db = await SocialHarness.DbAsync(ct))
        {
            db.UserBlocklist.Add(new UserBlockEntity { UserId = alice.UserId, BlockedId = bob.UserId, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        await bob.Friends.AcceptFriendRequest(alice.UserId, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await FriendshipRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
        });
    }

    /// <summary>Friends with a request still pending between them — seeded, as no call produces it.</summary>
    [Test, CancelAfter(120_000)]
    public async Task Accepting_between_friends_only_clears_the_stale_request(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);

        await using (var db = await SocialHarness.DbAsync(ct))
        {
            db.FriendRequest.Add(new FriendRequestEntity
            {
                RequesterId = alice.UserId, TargetId = bob.UserId, RequestedAt = DateTimeOffset.UtcNow,
                ExpiredAt = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30))
            });
            await db.SaveChangesAsync(ct);
        }

        await bob.Friends.AcceptFriendRequest(alice.UserId, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await FriendshipRowsAsync(alice.UserId, bob.UserId, ct), Is.EqualTo(2), "no duplicate friendship rows");
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Declining_tells_the_requester_and_declining_nothing_is_a_no_op(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);

        await bob.Friends.DeclineFriendRequest(alice.UserId, ct);

        await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        await bob.Friends.DeclineFriendRequest(alice.UserId, ct);

        await aliceStream.WaitForRecordAsync<FriendRequestDeclinedEvent>(e => e.targetId == bob.UserId, EventWait, ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(aliceStream.Records().Count(r => r.Event is FriendRequestDeclinedEvent), Is.EqualTo(1),
                "declining a request that did not exist announced something");
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Cancelling_tells_the_target_and_cancelling_nothing_is_a_no_op(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var bobStream = await SocialHarness.OnlineAsync(bob, ct);

        await alice.Friends.CancelFriendRequest(bob.UserId, ct);

        await alice.Friends.SendFriendRequest(bob.Credentials.username, ct);
        await alice.Friends.CancelFriendRequest(bob.UserId, ct);

        await bobStream.WaitForRecordAsync<FriendRequestCanceledEvent>(e => e.requesterId == alice.UserId, EventWait, ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(bobStream.Records().Count(r => r.Event is FriendRequestCanceledEvent), Is.EqualTo(1));
            Assert.That(await RequestRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
        });
    }

    // ── Blocking ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Blocking_or_unblocking_yourself_does_nothing(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        await alice.Friends.BlockUser(alice.UserId, ct);
        Assert.That((await alice.Friends.GetBlockList(50, 0, ct)).Size, Is.Zero);

        await alice.Friends.UnblockUser(alice.UserId, ct);
        Assert.That((await alice.Friends.GetBlockList(50, 0, ct)).Size, Is.Zero);
    }

    [Test, CancelAfter(120_000)]
    public async Task Blocking_twice_keeps_one_entry_and_unblocking_a_stranger_is_a_no_op(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        var carol = await CreateSessionAsync(ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);

        await alice.Friends.BlockUser(bob.UserId, ct);
        await alice.Friends.BlockUser(bob.UserId, ct);
        await alice.Friends.UnblockUser(carol.UserId, ct);

        Assert.That((await alice.Friends.GetBlockList(50, 0, ct)).Values.Select(b => b.blockedId), Is.EqualTo(new[] { bob.UserId }));

        await alice.Friends.UnblockUser(bob.UserId, ct);
        await aliceStream.WaitForRecordAsync<UserUnblockedEvent>(e => e.blockId == bob.UserId, EventWait, ct: ct);

        // One stream is ordered, so an unblock announced for carol would have arrived before bob's.
        await Assert.MultipleAsync(async () =>
        {
            Assert.That(aliceStream.Records().Count(r => r.Event is UserUnblockedEvent), Is.EqualTo(1),
                "unblocking somebody who was never blocked announced an unblock");
            Assert.That((await alice.Friends.GetBlockList(50, 0, ct)).Size, Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Lists_page_by_limit_and_offset(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        var carol = await CreateSessionAsync(ct);

        await BefriendAsync(alice, bob, ct);
        await BefriendAsync(alice, carol, ct);

        var first  = await alice.Friends.GetMyFriendships(1, 0, ct);
        var second = await alice.Friends.GetMyFriendships(1, 1, ct);
        var third  = await alice.Friends.GetMyFriendships(1, 2, ct);

        Assert.Multiple(() =>
        {
            Assert.That(first.Values.Select(f => f.friendId), Is.EqualTo(new[] { bob.UserId }), "oldest friendship first");
            Assert.That(second.Values.Select(f => f.friendId), Is.EqualTo(new[] { carol.UserId }));
            Assert.That(third.Size, Is.Zero);
        });
    }

    // ── Ignoring ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Ignoring_is_idempotent_listed_and_reversible(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);

        await alice.Friends.IgnoreUser(bob.UserId, ct);
        await alice.Friends.IgnoreUser(bob.UserId, ct);

        var ignored = await alice.Friends.GetIgnoreList(50, 0, ct);

        await Poll.UntilAsync(() => Task.FromResult(aliceStream.Records().Count(r => r.Event is UserIgnoredEvent) >= 2), EventWait, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(ignored.Values.Select(i => i.ignoredId), Is.EqualTo(new[] { bob.UserId }), "a second ignore must not add a second row");
            Assert.That(ignored.Values.Single().userId, Is.EqualTo(alice.UserId));
            Assert.That(aliceStream.Records().Count(r => r.Event is UserIgnoredEvent e && e.ignoredId == bob.UserId), Is.EqualTo(2),
                "every ignore is announced, so a window that missed the first still converges");
        });

        await alice.Friends.UnignoreUser(bob.UserId, ct);
        await alice.Friends.UnignoreUser(bob.UserId, ct);

        await aliceStream.WaitForRecordAsync<UserUnignoredEvent>(e => e.ignoredId == bob.UserId, EventWait, ct: ct);
        Assert.That((await alice.Friends.GetIgnoreList(50, 0, ct)).Size, Is.Zero);
    }

    [Test, CancelAfter(120_000)]
    public async Task Ignoring_yourself_does_nothing(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        await alice.Friends.IgnoreUser(alice.UserId, ct);

        Assert.That((await alice.Friends.GetIgnoreList(50, 0, ct)).Size, Is.Zero);
    }
}
