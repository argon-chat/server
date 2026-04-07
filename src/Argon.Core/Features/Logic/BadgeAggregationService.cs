namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using ArgonContracts;
using ion.runtime;

public class BadgeAggregationService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IReadStateService readStateService,
    IMuteSettingsService muteSettingsService,
    ISystemNotificationService systemNotificationService,
    ILogger<BadgeAggregationService> logger) : IBadgeAggregationService
{
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

        var spaceIds = readStates
            .Where(r => r.SpaceId.HasValue)
            .Select(r => r.SpaceId!.Value)
            .Distinct()
            .ToList();

        var channelLastMessages = new Dictionary<Guid, long>();
        if (spaceIds.Count > 0)
        {
            var channels = await ctx.Channels
                .AsNoTracking()
                .Where(c => spaceIds.Contains(c.SpaceId))
                .Select(c => new { c.Id, c.SpaceId, c.LastMessageId })
                .ToListAsync(ct);

            foreach (var ch in channels)
                channelLastMessages[ch.Id] = ch.LastMessageId;

            var spaceBadges = new List<SpaceBadge>();

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

                    var state = readStates.FirstOrDefault(r => r.ChannelId == ch.Id);
                    var lastRead = state?.LastReadMessageId ?? 0;

                    if (ch.LastMessageId > lastRead)
                    {
                        unreadCount++;
                        totalMentions += state?.MentionCount ?? 0;
                    }
                }

                if (unreadCount > 0)
                    spaceBadges.Add(new SpaceBadge(spaceId, unreadCount, totalMentions));
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

        var rsArr = readStates.Select(r =>
            new ChannelReadState(r.ChannelId, r.SpaceId, r.LastReadMessageId, r.MentionCount)
        ).ToArray();

        var msArr = muteSettings.Select(m =>
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
            IonArray<SpaceBadge>.Empty,
            new NotificationBadges(badgeCounts.friendRequests, badgeCounts.inventory, badgeCounts.system),
            new IonArray<ChannelReadState>(rsArr),
            new IonArray<MuteSettingsDto>(msArr)
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
