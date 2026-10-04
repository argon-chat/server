namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Features.EF;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;

[TestFixture]
public class DirectChatMetadataTests : TestBase
{
    private static Task<long> SendAsync(TestUserSession from, Guid to, string text, CancellationToken ct)
        => from.Chats.SendDirectMessage(to, text, new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

    [Test, CancelAfter(120_000)]
    public async Task Sending_revives_metadata_without_overwriting_user_settings(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob = await CreateSessionAsync(ct);
        await SendAsync(alice, bob.UserId, "initial", ct);
        var conversationId = ConversationEntity.GenerateConversationId(alice.UserId, bob.UserId);
        var pinnedAt = DateTimeOffset.UtcNow.AddDays(-1);

        await using (var db = await SocialHarness.DbAsync(ct))
        {
            var rows = await db.UserConversations.Where(x => x.ConversationId == conversationId).ToListAsync(ct);
            foreach (var row in rows)
            {
                row.IsPinned = true;
                row.PinnedAt = pinnedAt;
                row.IsMuted = true;
                row.IsArchived = true;
                row.LastReadMessageId = 123;
                row.UnreadCount = 7;
            }
            await db.SaveChangesAsync(ct);
        }

        const string text = "it's a 'quoted' preview; SELECT 1;";
        await SendAsync(alice, bob.UserId, text, ct);

        await using var check = await SocialHarness.DbAsync(ct);
        var metadata = await check.UserConversations.AsNoTracking().Where(x => x.ConversationId == conversationId).ToListAsync(ct);
        Assert.Multiple(() =>
        {
            Assert.That(metadata, Has.Count.EqualTo(2));
            foreach (var row in metadata)
            {
                Assert.That(row.IsArchived, Is.False);
                Assert.That(row.IsPinned, Is.True);
                Assert.That(row.PinnedAt, Is.EqualTo(pinnedAt).Within(TimeSpan.FromMilliseconds(1)));
                Assert.That(row.IsMuted, Is.True);
                Assert.That(row.LastReadMessageId, Is.EqualTo(123));
                Assert.That(row.LastMessageText, Is.EqualTo(text));
                Assert.That(row.UnreadCount, Is.EqualTo(row.UserId == alice.UserId ? 7 : 8));
            }
        });
    }

    [TestCase(false), TestCase(true), CancelAfter(120_000)]
    public async Task Concurrent_first_messages_create_one_metadata_row_per_user_and_count_every_message(bool blocked, CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob = await CreateSessionAsync(ct);
        var conversationId = ConversationEntity.GenerateConversationId(alice.UserId, bob.UserId);
        var (first, second) = ConversationEntity.OrderParticipants(alice.UserId, bob.UserId);
        if (blocked)
        {
            await alice.Friends.BlockUser(bob.UserId, ct);
            await bob.Friends.BlockUser(alice.UserId, ct);
        }
        // Isolate metadata insert races from the conversation service's separate create path.
        await using (var db = await SocialHarness.DbAsync(ct))
        {
            db.Conversations.Add(new ConversationEntity
            {
                Id = conversationId,
                Participant1Id = first,
                Participant2Id = second,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
        }

        const int eachDirection = 6;
        var sends = Enumerable.Range(0, eachDirection).SelectMany(i => new[]
        {
            SendAsync(alice, bob.UserId, $"alice {i}", ct),
            SendAsync(bob, alice.UserId, $"bob {i}", ct)
        });
        var messageIds = await Task.WhenAll(sends);

        await using var check = await SocialHarness.DbAsync(ct);
        var metadata = await check.UserConversations.AsNoTracking().Where(x => x.ConversationId == conversationId).ToListAsync(ct);
        var messages = await check.DirectMessages.CountAsync(x => x.ConversationId == conversationId, ct);
        var hidden = await check.DirectMessages.CountAsync(x => x.ConversationId == conversationId && x.IsHiddenFromReceiver, ct);
        var parent = await check.Conversations.AsNoTracking().SingleAsync(x => x.Id == conversationId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(metadata, Has.Count.EqualTo(2));
            Assert.That(metadata.Select(x => x.UnreadCount), Is.All.EqualTo(blocked ? 0 : eachDirection));
            Assert.That(messages, Is.EqualTo(eachDirection * 2));
            Assert.That(hidden, Is.EqualTo(blocked ? eachDirection * 2 : 0));
            if (blocked)
            {
                Assert.That(parent.LastMessageAt, Is.Null);
                Assert.That(parent.LastMessageText, Is.Null);
                Assert.That(parent.LastMessageSenderId, Is.Null);
            }
            Assert.That(messageIds.Distinct().Count(), Is.EqualTo(eachDirection * 2));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Metadata_upsert_rolls_back_with_the_callers_transaction(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob = await CreateSessionAsync(ct);
        await SendAsync(alice, bob.UserId, "committed", ct);
        var conversationId = ConversationEntity.GenerateConversationId(alice.UserId, bob.UserId);

        await using (var db = await SocialHarness.DbAsync(ct))
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await UserConversationWrites.RecordMessageAsync(db, bob.UserId, alice.UserId, conversationId,
                    null, DateTimeOffset.UtcNow, true, ct);
                var changed = await db.UserConversations.AsNoTracking().SingleAsync(x => x.UserId == bob.UserId && x.ConversationId == conversationId, ct);
                Assert.That(changed.UnreadCount, Is.EqualTo(2));
                Assert.That(changed.LastMessageText, Is.Null);
                await transaction.RollbackAsync(ct);
            });
        }

        await using var check = await SocialHarness.DbAsync(ct);
        var row = await check.UserConversations.AsNoTracking().SingleAsync(x => x.UserId == bob.UserId && x.ConversationId == conversationId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(row.UnreadCount, Is.EqualTo(1));
            Assert.That(row.LastMessageText, Is.EqualTo("committed"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_mismatched_second_participant_rolls_back_the_first_participants_update(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob = await CreateSessionAsync(ct);
        var (first, second) = ConversationEntity.OrderParticipants(alice.UserId, bob.UserId);
        var firstSession = alice.UserId == first ? alice : bob;
        await SendAsync(firstSession, second, "committed", ct);
        var conversationId = ConversationEntity.GenerateConversationId(first, second);
        var otherConversationId = Guid.CreateVersion7();

        await using (var db = await SocialHarness.DbAsync(ct))
        {
            // Preserve the unique peer association while making the second participant's identity inconsistent.
            await db.UserConversations.Where(x => x.UserId == second && x.ConversationId == conversationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConversationId, otherConversationId), ct);

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await db.Database.BeginTransactionAsync(ct);
                    // The first UPDATE succeeds and increments unread; the second UPDATE must reject the mismatch.
                    await UserConversationWrites.RecordParticipantsAsync(db, second, first, conversationId,
                        "must roll back", DateTimeOffset.UtcNow, true, ct);
                    await transaction.CommitAsync(ct);
                }));
        }

        await using var check = await SocialHarness.DbAsync(ct);
        var firstRow = await check.UserConversations.AsNoTracking()
            .SingleAsync(x => x.UserId == first && x.PeerId == second, ct);
        var secondRow = await check.UserConversations.AsNoTracking()
            .SingleAsync(x => x.UserId == second && x.PeerId == first, ct);
        Assert.Multiple(() =>
        {
            Assert.That(firstRow.LastMessageText, Is.EqualTo("committed"));
            Assert.That(firstRow.UnreadCount, Is.Zero, "the successful first UPDATE must roll back");
            Assert.That(secondRow.LastMessageText, Is.EqualTo("committed"));
            Assert.That(secondRow.UnreadCount, Is.EqualTo(1));
            Assert.That(secondRow.ConversationId, Is.EqualTo(otherConversationId));
            Assert.That(secondRow.IsArchived, Is.False);
        });
    }
}
