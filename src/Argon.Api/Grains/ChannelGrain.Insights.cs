namespace Argon.Grains;

public partial class ChannelGrain : IChannelInsightsGrain
{
    private static readonly TimeSpan ReadCountTtl = TimeSpan.FromSeconds(30);
    private const int ReadCountCacheSize = 256;

    // Every author and moderator looking at a post asks; one count per post per TTL answers them all.
    private readonly Dictionary<long, (Guid Author, int Readers, int Members, DateTimeOffset At)> readCounts = new();

    public async Task<IReadCountResult> GetReadCount(long messageId, CancellationToken ct = default)
    {
        if (_self.ChannelType != ChannelType.Announcement)
            return new FailedReadCount(ReadCountError.NOT_AN_ANNOUNCEMENT_CHANNEL);

        var callerId  = this.GetUserId();
        var channelId = this.GetPrimaryKey();

        if (!await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ViewChannel, ct))
            return new FailedReadCount(ReadCountError.INSUFFICIENT_PERMISSIONS);

        if (readCounts.TryGetValue(messageId, out var cached) && DateTimeOffset.UtcNow - cached.At < ReadCountTtl)
        {
            if (cached.Author != callerId
             && !await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageMessages, ct))
                return new FailedReadCount(ReadCountError.INSUFFICIENT_PERMISSIONS);
            return new SuccessReadCount(cached.Readers, cached.Members);
        }

        await using var ctx = await context.CreateDbContextAsync(ct);

        var authorId = await ctx.Messages.AsNoTracking()
           .Where(m => m.SpaceId == SpaceId && m.ChannelId == channelId && m.MessageId == messageId && !m.IsDeleted)
           .Select(m => (Guid?)m.CreatorId)
           .FirstOrDefaultAsync(ct);

        if (authorId is not { } author)
            return new FailedReadCount(ReadCountError.MESSAGE_NOT_FOUND);

        if (author != callerId
         && !await entitlementChecker.HasChannelAccessAsync(SpaceId, channelId, callerId, ArgonEntitlement.ManageMessages, ct))
            return new FailedReadCount(ReadCountError.INSUFFICIENT_PERMISSIONS);

        var spaceId = SpaceId;

        // Current members only, and never the author, who has read their own post by writing it.
        var readers = await ctx.ChannelReadStates.AsNoTracking()
           .Where(r => r.ChannelId == channelId && r.LastReadMessageId >= messageId && r.UserId != author)
           .Where(r => ctx.UsersToServerRelations.Any(m => m.SpaceId == spaceId && m.UserId == r.UserId))
           .CountAsync(ct);

        var members = await ctx.UsersToServerRelations.CountAsync(m => m.SpaceId == spaceId, ct);

        if (readCounts.Count >= ReadCountCacheSize)
            readCounts.Clear();
        readCounts[messageId] = (author, readers, members, DateTimeOffset.UtcNow);

        return new SuccessReadCount(readers, members);
    }
}
