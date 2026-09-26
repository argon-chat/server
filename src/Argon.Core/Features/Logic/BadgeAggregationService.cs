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
    /// The freshest high-water mark per channel: the cell where there is one, the stored row
    /// otherwise.
    /// With it, when the channel last moved: the cell's send time, or the row's flush time without one.
    /// </summary>
    /// <remarks>
    /// <para>The stored row lives in <c>ChannelLastMessages</c> — a table carrying nothing but the
    /// mark — and is written once per flush rather than once per message, so on its own it is up to a
    /// flush interval behind, and if the activation dies before flushing it stays behind until that
    /// channel sees another message. A badge is exactly the thing that must not be wrong for a
    /// channel that has gone quiet, so this reads the cell the send path writes.</para>
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
    private async Task<Dictionary<Guid, (long Mark, DateTimeOffset At)>> HighWaterMarksAsync(
        IReadOnlyList<(Guid Id, long Mark, DateTimeOffset At)> channels)
    {
        var marks = channels.ToDictionary(c => c.Id, c => (c.Mark, c.At));

        if (marks.Count == 0)
            return marks;

        try
        {
            await using var scope = redis.Rent();

            var ids   = channels.Select(c => c.Id).ToArray();
            var cells = await scope.GetDatabase().StringGetAsync(ids
               .Select(id => (RedisKey)ChannelHighWaterCell.KeyFor(id))
               .Concat(ids.Select(id => (RedisKey)ChannelHighWaterCell.AtKeyFor(id)))
               .ToArray());

            for (var i = 0; i < ids.Length; i++)
            {
                var (mark, at) = marks[ids[i]];

                if (!cells[i].TryParse(out long cell) || cell < mark)
                    continue;

                mark = cell;

                // The cell carries the send time; the row only its flush time, which can land after a join.
                if (cells[ids.Length + i].TryParse(out long ms))
                    at = DateTimeOffset.FromUnixTimeMilliseconds(ms);

                marks[ids[i]] = (mark, at);
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
        // The three services read through contexts of their own and run beside the queries below;
        // everything this method reads itself goes through one context.
        var readStatesTask  = readStateService.GetAllReadStatesAsync(userId, ct);
        var muteTask        = muteSettingsService.GetMuteSettingsAsync(userId, ct);
        var badgeCountsTask = systemNotificationService.GetBadgeCountsAsync(userId, ct);

        int unreadDmCount;
        List<(Guid Id, Guid SpaceId)> channels;
        Dictionary<Guid, (long Mark, DateTimeOffset At)> stored;
        Dictionary<Guid, (DateTimeOffset Joined, Guid? MainAnnouncement)> horizons;

        await using (var ctx = await contextFactory.CreateDbContextAsync(ct))
        {
            unreadDmCount = await ctx.UserConversations
                .AsNoTracking()
                .Where(x => x.UserId == userId && x.UnreadCount > 0)
                .CountAsync(ct);

            // A subquery rather than a list read first: it saves the round trip that fetched the ids.
            var memberOf = ctx.UsersToServerRelations
                .Where(x => x.UserId == userId)
                .Select(x => x.SpaceId);

            var mains = await ctx.Spaces
                .AsNoTracking()
                .Where(s => memberOf.Contains(s.Id))
                .ToDictionaryAsync(s => s.Id, s => s.MainAnnouncementChannelId, ct);

            // A membership row per stay: leaving soft-deletes it, so a return starts a new one.
            horizons = (await ctx.UsersToServerRelations
                    .AsNoTracking()
                    .Where(x => x.UserId == userId)
                    .Select(x => new { x.SpaceId, x.CreatedAt })
                    .ToListAsync(ct))
                .DistinctBy(x => x.SpaceId)
                .ToDictionary(x => x.SpaceId, x => (x.CreatedAt, mains.GetValueOrDefault(x.SpaceId)));

            // Which channels exist, and which space each is in. Nothing about the counter is read
            // here any more: Channels.LastMessageId is the dead column, and the mark comes from the
            // side table below.
            //
            // No `LastMessageId > 0` filter, and there could not be one now even if it were wanted.
            // It used to be free — the row was written on every send, so a zero really did mean an
            // empty channel — but that stopped being true when the write was coalesced onto a flush
            // timer, and it is doubly untrue now that the number is not on this row at all.
            channels = (await ctx.Channels
                    .AsNoTracking()
                    .Where(c => memberOf.Contains(c.SpaceId))
                    .Select(c => new { c.Id, c.SpaceId })
                    .ToListAsync(ct))
                .Select(c => (c.Id, c.SpaceId))
                .ToList();

            // One seek per space over ix_channel_last_messages_space, which is the shape this table
            // was given a SpaceId for. A second query rather than a left join onto the one above,
            // for two reasons: two independent index seeks beat one plan that has to walk both
            // tables, and "no row" stays a C# lookup miss instead of a nullable column that the next
            // person to touch this has to remember to coalesce. Neither table needs the other to
            // answer its half.
            stored = channels.Count == 0
                ? []
                : await ctx.ChannelLastMessages
                    .AsNoTracking()
                    .Where(m => memberOf.Contains(m.SpaceId))
                    .Select(m => new { m.ChannelId, m.LastMessageId, m.UpdatedAt })
                    .ToDictionaryAsync(m => m.ChannelId, m => (m.LastMessageId, m.UpdatedAt), ct);
        }

        await Task.WhenAll(readStatesTask, muteTask, badgeCountsTask);

        var readStates   = await readStatesTask;
        var muteSettings = await muteTask;
        var badgeCounts  = await badgeCountsTask;

        var mutedTargets = muteSettings
            .Where(m => m.MuteLevel == MuteLevel.All)
            .Select(m => m.TargetId)
            .ToHashSet();

        var spaceBadges = new List<SpaceBadge>();
        var effective   = readStates.ToDictionary(r => r.ChannelId,
            r => new ChannelReadState(r.ChannelId, r.SpaceId, r.LastReadMessageId, r.MentionCount));

        // A space with no channels can have nothing unread, so the spaces come from the channels.
        if (channels.Count > 0)
        {
            // A channel with no row is a channel nobody has posted in, which is the common case and
            // reads as zero. It must not read as "not in the result" — every channel in the space has
            // to reach the loop below, or a channel whose first messages are still only in the Redis
            // cell would be dropped before the cell could correct it.
            var marks = await HighWaterMarksAsync(channels
               .Select(c => stored.TryGetValue(c.Id, out var s) ? (c.Id, s.Mark, s.At) : (c.Id, 0L, DateTimeOffset.MinValue))
               .ToList());

            // What a channel held when the member (re)joined is history: read, whatever cursor an earlier
            // stay left behind. The main announcement channel stays unread so a newcomer sees it.
            foreach (var (id, spaceId) in channels)
            {
                var (mark, at) = marks[id];

                if (mark > 0 && horizons.TryGetValue(spaceId, out var horizon)
                 && id != horizon.MainAnnouncement && at <= horizon.Joined
                 && (!effective.TryGetValue(id, out var cursor) || cursor.lastReadMessageId < mark))
                    effective[id] = new ChannelReadState(id, spaceId, mark, 0);
            }

            foreach (var spaceId in channels.Select(c => c.SpaceId).Distinct())
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

                    effective.TryGetValue(ch.Id, out var state);

                    if (marks[ch.Id].Mark > (state?.lastReadMessageId ?? 0))
                    {
                        unreadCount++;
                        totalMentions += state?.mentionCount ?? 0;
                    }
                }

                if (unreadCount > 0)
                    spaceBadges.Add(new SpaceBadge(spaceId, unreadCount, totalMentions));
            }
        }

        var ionReadStates = effective.Values.ToArray();

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
}
