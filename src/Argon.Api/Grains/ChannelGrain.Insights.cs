namespace Argon.Grains;

public partial class ChannelGrain : IChannelInsightsGrain
{
    private static readonly TimeSpan ReadCountTtl = TimeSpan.FromSeconds(30);
    private const int ReadCountCacheSize = 256;
    private const int ReadCountBatch     = 50;

    /// <summary>Below this, a count all but names who has read the post.</summary>
    public const int ReadCountMinMembers = 5;

    private readonly record struct ReadCount(Guid Author, int Readers, int Members, DateTimeOffset At);

    // Every author and moderator looking at a post asks; one count per post per TTL answers them all.
    private readonly Dictionary<long, ReadCount> readCounts = new();

    public async Task<IReadCountResult> GetReadCount(long messageId, CancellationToken ct = default)
    {
        if (_self.ChannelType != ChannelType.Announcement)
            return new FailedReadCount(ReadCountError.NOT_AN_ANNOUNCEMENT_CHANNEL);

        var results = await ReadCountsAsync([messageId], ct);
        return results?[messageId] ?? new FailedReadCount(ReadCountError.INSUFFICIENT_PERMISSIONS);
    }

    public async Task<List<ReadCountEntry>> GetReadCounts(List<long> messageIds, CancellationToken ct = default)
    {
        if (_self.ChannelType != ChannelType.Announcement)
            return [];

        var ids     = messageIds.Distinct().Take(ReadCountBatch).ToList();
        var results = await ReadCountsAsync(ids, ct);

        var entries = new List<ReadCountEntry>();
        foreach (var id in ids)
            if (results?[id] is SuccessReadCount ok)
                entries.Add(new ReadCountEntry(id, ok.readers, ok.members));

        return entries;
    }

    /// <summary>
    /// A result for every id: the author and members with ManageMessages get a count, anyone else a
    /// refusal. Null when the caller cannot see the channel at all.
    /// </summary>
    private async Task<Dictionary<long, IReadCountResult>?> ReadCountsAsync(List<long> ids, CancellationToken ct)
    {
        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel, ct))
            return null;

        bool? moderator = null;

        async ValueTask<bool> MayCountAsync(Guid author)
            => author == callerId
            || (moderator ??= await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageMessages, ct));

        var now    = DateTimeOffset.UtcNow;
        var counts = new Dictionary<long, ReadCount>();
        var stale  = new List<long>();

        foreach (var id in ids)
            if (readCounts.TryGetValue(id, out var cached) && now - cached.At < ReadCountTtl)
                counts[id] = cached;
            else
                stale.Add(id);

        var authors = counts.ToDictionary(c => c.Key, c => c.Value.Author);

        if (stale.Count > 0)
        {
            await using var ctx = await context.CreateDbContextAsync(ct);

            var found = await ctx.Messages.AsNoTracking()
               .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && stale.Contains(m.MessageId) && !m.IsDeleted)
               .Select(m => new { m.MessageId, m.CreatorId })
               .ToListAsync(ct);

            var toCount = new List<(long Id, Guid Author)>();
            foreach (var message in found)
            {
                authors[message.MessageId] = message.CreatorId;
                if (await MayCountAsync(message.CreatorId))
                    toCount.Add((message.MessageId, message.CreatorId));
            }

            if (toCount.Count > 0)
                foreach (var (id, count) in await CountReadersAsync(ctx, toCount, ct))
                    counts[id] = count;
        }

        var results = new Dictionary<long, IReadCountResult>(ids.Count);
        foreach (var id in ids)
        {
            if (!authors.TryGetValue(id, out var author))
                results[id] = new FailedReadCount(ReadCountError.MESSAGE_NOT_FOUND);
            else if (!await MayCountAsync(author))
                results[id] = new FailedReadCount(ReadCountError.INSUFFICIENT_PERMISSIONS);
            else if (counts[id].Members < ReadCountMinMembers)
                results[id] = new FailedReadCount(ReadCountError.TOO_FEW_MEMBERS);
            else
                results[id] = new SuccessReadCount(counts[id].Readers, counts[id].Members);
        }

        return results;
    }

    /// <summary>Counts every post from one read of the members' read marks, and keeps the counts for <see cref="ReadCountTtl"/>.</summary>
    private async Task<Dictionary<long, ReadCount>> CountReadersAsync(ApplicationDbContext ctx, List<(long Id, Guid Author)> posts,
        CancellationToken ct)
    {
        var spaceId   = SpaceId;
        var channelId = this.GetPrimaryKey();
        var oldest    = posts.Min(p => p.Id);

        var members = await ctx.UsersToServerRelations.CountAsync(m => m.SpaceId == spaceId, ct);

        // Current members only, and only marks that reach the oldest post asked about.
        var marks = members < ReadCountMinMembers
            ? null
            : await ctx.ChannelReadStates.AsNoTracking()
               .Where(r => r.ChannelId == channelId && r.LastReadMessageId >= oldest)
               .Where(r => ctx.UsersToServerRelations.Any(m => m.SpaceId == spaceId && m.UserId == r.UserId))
               .Select(r => new { r.UserId, r.LastReadMessageId })
               .ToListAsync(ct);

        if (readCounts.Count + posts.Count > ReadCountCacheSize)
            readCounts.Clear();

        var now     = DateTimeOffset.UtcNow;
        var counted = new Dictionary<long, ReadCount>(posts.Count);

        foreach (var (id, author) in posts)
        {
            // Never the author, who has read their own post by writing it.
            var readers = marks?.Count(r => r.LastReadMessageId >= id && r.UserId != author) ?? 0;
            counted[id] = readCounts[id] = new ReadCount(author, readers, members, now);
        }

        return counted;
    }
}
