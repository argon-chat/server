namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using ArgonContracts;
using ion.runtime;
using Argon.Services;
using StackExchange.Redis;
using Argon.Features.Cache;

public class BadgeAggregationService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IReadStateService readStateService,
    IMuteSettingsService muteSettingsService,
    ISystemNotificationService systemNotificationService,
    [FromKeyedServices(RedisProfiles.Cache)] IRedisPoolConnections redis,
    ILogger<BadgeAggregationService> logger) : IBadgeAggregationService
{
    /// <summary>
    /// The freshest high-water mark per channel: the cell where there is one, the row otherwise.
    /// </summary>
    /// <remarks>
    /// <para>The channel row is written once per flush now rather than once per message, so on its own
    /// it is up to a flush interval behind — and if the activation dies before flushing, it stays
    /// behind until that channel sees another message. A badge is exactly the thing that must not be
    /// wrong for a channel that has gone quiet, so this reads the cell the send path writes.</para>
    ///
    /// <para>The larger of the two, never one or the other. The cell is missing after an eviction and
    /// for a channel nobody has posted in since it was last written; the row is behind between
    /// flushes. Both only ever rise, so the maximum is the true answer rather than a guess about
    /// which source to trust.</para>
    ///
    /// <para>Redis being unreachable degrades to the row. The values are already in hand, and they are
    /// what this query used before the cell existed — failing a user's whole badge fetch over a
    /// counter would be the worse trade.</para>
    /// </remarks>
    private async Task<Dictionary<Guid, long>> HighWaterMarksAsync(
        IReadOnlyList<(Guid Id, long LastMessageId)> channels)
    {
        var marks = channels.ToDictionary(c => c.Id, c => c.LastMessageId);

        if (marks.Count == 0)
            return marks;

        try
        {
            await using var scope = redis.Rent();

            var ids   = channels.Select(c => c.Id).ToArray();
            var cells = await scope.GetDatabase()
               .StringGetAsync(ids.Select(id => (RedisKey)ChannelHighWaterCell.KeyFor(id)).ToArray());

            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i].TryParse(out long cell) && cell > marks[ids[i]])
                    marks[ids[i]] = cell;
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Channel high-water cells unavailable; badges fall back to the stored rows");
        }

        return marks;
    }

    public async Task<GlobalBadges> GetGlobalBadgesAsync(Guid userId, CancellationToken ct = default)
    {
        var readStatesTask  = readStateService.GetAllReadStatesAsync(userId, ct);
        var muteTask        = muteSettingsService.GetMuteSettingsAsync(userId, ct);
        var badgeCountsTask = systemNotificationService.GetBadgeCountsAsync(userId, ct);
        var unreadDmTask    = GetUnreadDmCountAsync(userId, ct);

        await Task.WhenAll(readStatesTask, muteTask, badgeCountsTask, unreadDmTask);

        var readStates    = readStatesTask.Result;
        var muteSettings  = muteTask.Result;
        var badgeCounts   = badgeCountsTask.Result;
        var unreadDmCount = unreadDmTask.Result;

        var mutedTargets = muteSettings
            .Where(m => m.MuteLevel == MuteLevel.All)
            .Select(m => m.TargetId)
            .ToHashSet();

        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var spaceIds = await ctx.UsersToServerRelations
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .Select(x => x.SpaceId)
            .Distinct()
            .ToListAsync(ct);

        var spaceBadges = new List<SpaceBadge>();

        if (spaceIds.Count > 0)
        {
            // No `LastMessageId > 0` filter any more. It used to be free — the row was written on
            // every send, so a zero really did mean an empty channel. With the write coalesced, a
            // channel whose first messages have not been flushed yet still reads zero here, and
            // filtering it out server-side would drop it before the cell below could correct it.
            var channels = await ctx.Channels
                .AsNoTracking()
                .Where(c => spaceIds.Contains(c.SpaceId))
                .Select(c => new { c.Id, c.SpaceId, c.LastMessageId })
                .ToListAsync(ct);

            var marks = await HighWaterMarksAsync(
                channels.Select(c => (c.Id, c.LastMessageId)).ToList());

            var readStateMap = readStates.ToDictionary(r => r.ChannelId);

            foreach (var spaceId in spaceIds)
            {
                if (mutedTargets.Contains(spaceId))
                    continue;

                var spaceChannels = channels.Where(c => c.SpaceId == spaceId).ToList();
                var unreadCount = 0;
                var totalMentions = 0;

                foreach (var ch in spaceChannels)
                {
                    if (mutedTargets.Contains(ch.Id))
                        continue;

                    readStateMap.TryGetValue(ch.Id, out var state);
                    var lastRead = state?.LastReadMessageId ?? 0;

                    if (marks[ch.Id] > lastRead)
                    {
                        unreadCount++;
                        totalMentions += state?.MentionCount ?? 0;
                    }
                }

                if (unreadCount > 0)
                    spaceBadges.Add(new SpaceBadge(spaceId, unreadCount, totalMentions));
            }
        }

        var ionReadStates = readStates.Select(r =>
            new ChannelReadState(r.ChannelId, r.SpaceId, r.LastReadMessageId, r.MentionCount)
        ).ToArray();

        var ionMuteSettings = muteSettings.Select(m =>
            new MuteSettingsDto(
                m.TargetId,
                m.TargetType == MuteTargetType.Space ? MuteTargetKind.Space : MuteTargetKind.Channel,
                m.MuteLevel switch
                {
                    MuteLevel.OnlyMentions => MuteLevelType.OnlyMentions,
                    MuteLevel.All          => MuteLevelType.All,
                    _                      => MuteLevelType.None
                },
                m.SuppressEveryone,
                m.MuteExpiresAt?.UtcDateTime
            )
        ).ToArray();

        return new GlobalBadges(
            unreadDmCount,
            new IonArray<SpaceBadge>(spaceBadges.ToArray()),
            new NotificationBadges(badgeCounts.friendRequests, badgeCounts.inventory, badgeCounts.system),
            new IonArray<ChannelReadState>(ionReadStates),
            new IonArray<MuteSettingsDto>(ionMuteSettings)
        );
    }

    private async Task<int> GetUnreadDmCountAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        return await ctx.UserConversations
            .AsNoTracking()
            .Where(x => x.UserId == userId && x.UnreadCount > 0)
            .CountAsync(ct);
    }
}
