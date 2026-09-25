namespace ArgonComplexTest.Tests;

using Argon.Entities;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Direct chats beyond the send-and-read path <see cref="SocialGraphTests"/> covers: the echo chat
/// every list carries, deleting a chat on one side, read state across windows, attachments, the
/// link-preview stub, paging and the preview text.
/// </summary>
[TestFixture]
public class DirectChatTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private static Task<long> SendAsync(TestUserSession from, Guid to, string text, CancellationToken ct, params IMessageEntity[] entities)
        => from.Chats.SendDirectMessage(to, text, new IonArray<IMessageEntity>(entities), Random.Shared.NextInt64(), null, ct);

    private static async Task<UserChat?> ChatWithAsync(TestUserSession session, Guid peer, CancellationToken ct)
        => (await session.Chats.GetRecentChats(50, 0, ct)).Values.FirstOrDefault(c => c.peerId == peer);

    private static async Task<int> StoredRowsAsync(Guid userId, Guid peerId, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);
        return await db.UserConversations.CountAsync(x => x.UserId == userId && x.PeerId == peerId, ct);
    }

    // ── The echo chat ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The echo chat is pinned on top of every list, whether or not the user has written to it.
    /// </summary>
    /// <remarks>
    /// Once a conversation with the echo user exists it comes back from the query as an ordinary
    /// unpinned row, and <c>GetRecentChatsAsync</c> used to move only its <c>PinnedAt</c> — so the
    /// client, which groups by <c>isPinned</c>, dropped it into the unpinned list the moment the
    /// user sent it anything.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_echo_chat_stays_pinned_before_and_after_it_is_written_to(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        var before = await ChatWithAsync(alice, UserEntity.EchoUser, ct);
        Assert.That(before?.isPinned, Is.True, "a fresh account's list must carry the echo chat, pinned");

        await SendAsync(alice, UserEntity.EchoUser, "note to self", ct);

        var after = await ChatWithAsync(alice, UserEntity.EchoUser, ct);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.Not.Null);
            Assert.That(after!.lastMsg, Is.EqualTo("note to self"), "the stored echo row is the one listed");
            Assert.That(after.isPinned, Is.True, "writing to the echo chat unpinned it");
            Assert.That(after.pinnedAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddDays(365)));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_echo_chat_cannot_be_pinned_unpinned_or_deleted(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);

        await alice.Chats.PinChat(UserEntity.EchoUser, ct);
        await alice.Chats.UnpinChat(UserEntity.EchoUser, ct);
        await alice.Chats.DeleteChat(UserEntity.EchoUser, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await StoredRowsAsync(alice.UserId, UserEntity.EchoUser, ct), Is.Zero,
                "none of the three may write a row for the echo chat");
            Assert.That((await ChatWithAsync(alice, UserEntity.EchoUser, ct))?.isPinned, Is.True);
        });
    }

    // ── Deleting ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Deleting_a_chat_hides_it_on_this_side_until_the_next_message(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);

        await SendAsync(alice, bob.UserId, "first", ct);
        await alice.Chats.PinChat(bob.UserId, ct);

        await alice.Chats.DeleteChat(bob.UserId, ct);
        await aliceStream.WaitForRecordAsync<ChatDeletedEvent>(e => e.peerId == bob.UserId, EventWait, ct: ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await ChatWithAsync(alice, bob.UserId, ct), Is.Null, "the deleted chat is still listed");
            Assert.That(await ChatWithAsync(bob, alice.UserId, ct), Is.Not.Null, "deleting is one-sided");
            Assert.That((await alice.Chats.QueryDirectMessages(bob.UserId, null, 10, ct)).Values.Select(m => m.text),
                Is.EqualTo(new[] { "first" }), "deleting a chat must not destroy the messages");
        });

        await SendAsync(bob, alice.UserId, "are you there?", ct);

        var back = await ChatWithAsync(alice, bob.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(back, Is.Not.Null, "the next message has to bring the chat back");
            Assert.That(back!.isPinned, Is.False, "a deleted chat comes back unpinned");
            Assert.That(back.unreadCount, Is.EqualTo(1), "only what arrived after the delete is unread");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Deleting_a_chat_that_never_existed_writes_nothing(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Chats.DeleteChat(bob.UserId, ct);
        await alice.Chats.MarkChatRead(bob.UserId, ct);
        await alice.Chats.UnpinChat(bob.UserId, ct);

        Assert.That(await StoredRowsAsync(alice.UserId, bob.UserId, ct), Is.Zero);
    }

    // ── Read state ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reading a chat in one window clears its badge in every other window of the same account.
    /// </summary>
    /// <remarks>
    /// <c>ChatReadEvent</c> is in the contract and the client's recent-chat list handles it, but
    /// <c>MarkChatReadAsync</c> never sent it: the unread count went to zero in the database and
    /// stayed on screen everywhere else until a reload.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Marking_a_chat_read_clears_the_count_and_tells_the_other_windows(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);

        await SendAsync(bob, alice.UserId, "one", ct);
        await SendAsync(bob, alice.UserId, "two", ct);

        Assert.That((await ChatWithAsync(alice, bob.UserId, ct))?.unreadCount, Is.EqualTo(2));

        await alice.Chats.MarkChatRead(bob.UserId, ct);

        Assert.That((await ChatWithAsync(alice, bob.UserId, ct))?.unreadCount, Is.Zero);
        Assert.That((await ChatWithAsync(bob, alice.UserId, ct))?.unreadCount, Is.Zero, "the sender's own side never counts");

        await aliceStream.WaitForRecordAsync<ChatReadEvent>(e => e.peerId == bob.UserId, EventWait, ct: ct);

        // Opening a chat that is already read changes nothing and tells nobody; the next unread does.
        await alice.Chats.MarkChatRead(bob.UserId, ct);
        await SendAsync(bob, alice.UserId, "three", ct);
        await alice.Chats.MarkChatRead(bob.UserId, ct);

        await Poll.UntilAsync(() => Task.FromResult(aliceStream.Records().Count(r => r.Event is ChatReadEvent) >= 2), EventWait, ct: ct);

        Assert.That(aliceStream.Records().Select(r => r.Event).Where(e => e is ChatReadEvent or DirectMessageSent).Select(e => e.GetType().Name),
            Is.EqualTo(new[] { "DirectMessageSent", "DirectMessageSent", "ChatReadEvent", "DirectMessageSent", "ChatReadEvent" }),
            "a chat with nothing unread announced a read");
    }

    [Test, CancelAfter(120_000)]
    public async Task A_message_from_someone_you_ignore_is_delivered_but_not_counted(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        var carol = await CreateSessionAsync(ct);

        await alice.Friends.IgnoreUser(bob.UserId, ct);

        await SendAsync(bob, alice.UserId, "from bob", ct);
        await SendAsync(carol, alice.UserId, "from carol", ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await ChatWithAsync(alice, bob.UserId, ct))?.unreadCount, Is.Zero);
            Assert.That((await ChatWithAsync(alice, carol.UserId, ct))?.unreadCount, Is.EqualTo(1));
            Assert.That((await alice.Chats.QueryDirectMessages(bob.UserId, null, 10, ct)).Values.Select(m => m.text),
                Does.Contain("from bob"), "ignoring is not blocking: the message still arrives");
        });
    }

    // ── Sending and reading ─────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task The_chat_preview_is_the_first_two_hundred_characters(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        var text  = new string('x', 150) + new string('y', 150);

        await SendAsync(alice, bob.UserId, text, ct);

        var chat    = await ChatWithAsync(bob, alice.UserId, ct);
        var message = (await bob.Chats.QueryDirectMessages(alice.UserId, null, 1, ct)).Values.Single();

        Assert.Multiple(() =>
        {
            Assert.That(chat?.lastMsg, Is.EqualTo(text[..200]));
            Assert.That(message.text, Is.EqualTo(text), "only the preview is cut, never the message");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Older_messages_page_backwards_from_a_message_id(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await SendAsync(alice, bob.UserId, "m1", ct);
        await SendAsync(bob, alice.UserId, "m2", ct);
        await SendAsync(alice, bob.UserId, "m3", ct);

        var newest = await alice.Chats.QueryDirectMessages(bob.UserId, null, 2, ct);
        var older  = await alice.Chats.QueryDirectMessages(bob.UserId, newest.Values.Min(m => m.messageId), 10, ct);

        Assert.Multiple(() =>
        {
            Assert.That(newest.Values.Select(m => m.text), Is.EqualTo(new[] { "m3", "m2" }), "newest first");
            Assert.That(older.Values.Select(m => m.text), Is.EqualTo(new[] { "m1" }));
            Assert.That(newest.Values.Single(m => m.text == "m2").receiverId, Is.EqualTo(alice.UserId));
            Assert.That(newest.Values.Single(m => m.text == "m3").receiverId, Is.EqualTo(bob.UserId));
        });
    }

    /// <summary>
    /// A client's link-preview stub is the server's to fill, and with no crawler answering there is
    /// nothing to fill it with: a direct message has no deferred path, so the stub is dropped.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_link_preview_stub_nobody_can_fill_is_dropped(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        const string text = "look at https://example.com/page";
        var stub = new MessageEntityLinkPreview(EntityType.LinkPreview, 8, 24, 1,
            "https://example.com/page", "PHISHING", "client-written", "Evil", "https://evil.test/i.png", null);

        await SendAsync(alice, bob.UserId, text, ct, stub);

        var stored = (await bob.Chats.QueryDirectMessages(alice.UserId, null, 1, ct)).Values.Single();

        Assert.Multiple(() =>
        {
            Assert.That(stored.text, Is.EqualTo(text));
            Assert.That(stored.entities.Values.OfType<MessageEntityLinkPreview>(), Is.Empty,
                "a card the sender wrote reached the receiver");
        });
    }

    // ── Attachments ─────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task An_attachment_is_uploaded_and_described_back(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var begin = await alice.Chats.BeginUploadAttachment(bob.UserId, ct);
        Assert.That(begin, Is.InstanceOf<SuccessUploadFile>(), $"refused: {(begin as FailedUploadFile)?.error}");

        var ticket = (SuccessUploadFile)begin;
        await SocialHarness.UploadAsync(ticket, SocialHarness.Png, "image/png");

        var info = await alice.Chats.CompleteUploadAttachment(bob.UserId, ticket.blobId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(info.fileId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(info.fileSize, Is.EqualTo(SocialHarness.Png.Length));
            Assert.That(info.contentType, Is.EqualTo("image/png"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_peer_who_blocked_you_refuses_your_attachments(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await bob.Friends.BlockUser(alice.UserId, ct);

        var refused = await alice.Chats.BeginUploadAttachment(bob.UserId, ct);
        var allowed = await bob.Chats.BeginUploadAttachment(alice.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((refused as FailedUploadFile)?.error, Is.EqualTo(UploadFileError.NOT_AUTHORIZED));
            Assert.That(allowed, Is.InstanceOf<SuccessUploadFile>(), "the wall is one-way: the blocker may still send");
        });
    }

    /// <summary>
    /// A blocked sender is not told: their message is kept for them and never reaches the blocker —
    /// not in the history, not as a chat, not as an event.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_blocked_sender_cannot_reach_the_person_who_blocked_them(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        await alice.Friends.BlockUser(bob.UserId, ct);

        await using var aliceStream = await SocialHarness.OnlineAsync(alice, ct);
        var mark = aliceStream.Mark();

        var sent = await SendAsync(bob, alice.UserId, "you blocked me", ct);

        var delivered = await aliceStream.FirstWithinAsync<DirectMessageSent>(e => e.senderId == bob.UserId, EventWait, mark, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(sent, Is.GreaterThan(0), "the sender gets an ordinary answer");
            Assert.That((await bob.Chats.QueryDirectMessages(alice.UserId, null, 10, ct)).Values.Select(m => m.text),
                Does.Contain("you blocked me"), "the sender still sees what they sent");
            Assert.That((await alice.Chats.QueryDirectMessages(bob.UserId, null, 10, ct)).Values.Select(m => m.text),
                Does.Not.Contain("you blocked me"), "a blocked sender's message was delivered");
            Assert.That(await ChatWithAsync(alice, bob.UserId, ct), Is.Null, "a blocked sender opened a chat on the blocker's side");
            Assert.That(delivered, Is.Null, "the blocker was told about the message");
        });

        await alice.Friends.UnblockUser(bob.UserId, ct);

        Assert.That((await alice.Chats.QueryDirectMessages(bob.UserId, null, 10, ct)).Values.Select(m => m.text),
            Does.Not.Contain("you blocked me"), "unblocking does not bring back what was sent during the block");
    }
}
