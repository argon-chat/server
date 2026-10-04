namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.EF;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using static ChannelTestKit;

/// <summary>Exercises the database-side predicate against the real polymorphic JSON converter.</summary>
[TestFixture]
public class MassMentionQueryTests : TestBase
{
    [Test, CancelAfter(120_000)]
    public async Task Seed_filters_before_limit_and_preserves_deleted_distinct_self_and_boundary_semantics(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var space = await CreateSpaceAsync(owner, ct);
        var channel = await CreateChannelAsync(owner, space, "seed-pings", ChannelType.Announcement, ct);
        var otherChannel = await CreateChannelAsync(owner, space, "other-seed-pings", ChannelType.Announcement, ct);
        var clock = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(clock.Ticks - clock.Ticks % 10, TimeSpan.Zero);
        var since = now.AddHours(-1);
        var users = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        const int massUsers = 3;
        const long before = 1_000;

        static IMessageEntity Mention(Guid user) => new MessageEntityMention(EntityType.Mention, 0, 1, 1, user);
        static IMessageEntity Everyone() => new MessageEntityMentionEveryone(EntityType.MentionEveryone, 0, 1, 1);
        static IMessageEntity Role() => new MessageEntityMentionRole(EntityType.MentionRole, 0, 1, 1, Guid.NewGuid());
        ArgonMessageEntity Message(long id, params IMessageEntity[] entities) => new()
        {
            SpaceId = space, ChannelId = channel, MessageId = id, CreatorId = owner.UserId,
            Text = "seed", CreatedAt = now.AddMinutes(-1), UpdatedAt = now, Entities = entities.ToList()
        };

        var rows = new List<ArgonMessageEntity>
        {
            Message(101, Everyone()),
            Message(102, Role()) with { IsDeleted = true, DeletedAt = now },
            Message(103, users.Select(Mention).ToArray()),
            Message(104, users.Take(3).Select(Mention).ToArray()), // Exactly the threshold is not mass.
            Message(105, Enumerable.Repeat(Mention(users[0]), 20).ToArray()),
            Message(106, users.Take(3).Select(Mention).Append(Mention(owner.UserId)).ToArray()),
            Message(107, Everyone()) with { CreatedAt = since.AddTicks(-10) },
            Message(108, Everyone()) with { CreatedAt = since },
            Message(109, Everyone()) with { ChannelId = otherChannel },
            Message(before, Everyone()),
            Message(before + 1, Everyone()),
            // Runtime type, not the supplied enum, was the original predicate's authority.
            Message(110, new MessageEntityMentionEveryone(EntityType.Bold, 0, 1, 1)),
            Message(111, new MessageEntityBold(EntityType.MentionEveryone, 0, 1, 1))
        };
        // Hundreds of newer ordinary messages must not hide old pings behind LIMIT.
        rows.AddRange(Enumerable.Range(200, 300).Select(i => Message(i)));

        await using var db = await DbAsync(ct);
        db.Messages.AddRange(rows);
        await db.SaveChangesAsync(ct);

        var candidates = await db.Messages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.SpaceId == space && m.ChannelId == channel && m.CreatedAt >= since && m.MessageId < before)
            .Select(m => new { m.MessageId, m.CreatorId, m.CreatedAt, m.Entities }).ToListAsync(ct);
        var expected = candidates.Where(m => m.Entities.Any(e => e is MessageEntityMentionEveryone or MessageEntityMentionRole)
                || m.Entities.OfType<MessageEntityMention>().Select(e => e.userId).Where(u => u != m.CreatorId).Distinct().Count() > massUsers)
            .OrderByDescending(m => m.MessageId).ToList();
        Assert.That(expected.Select(m => m.MessageId), Is.EqualTo(new long[] { 110, 108, 103, 102, 101 }),
            "premise: persisted fixtures cover the intended predicate and range boundaries");

        foreach (var limit in new[] { 1, 3, 20 })
        {
            var actual = await db.RecentMassPingsAsync(space, channel, since, before, massUsers, limit, ct);
            Assert.That(actual, Is.EquivalentTo(expected.Take(limit).ToDictionary(m => m.MessageId, m => m.CreatedAt)),
                $"database seed differs from the previous CLR predicate with limit {limit}");
        }
    }
}
