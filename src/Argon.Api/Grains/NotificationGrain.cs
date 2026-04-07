namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Entities.Data;
using Argon.Core.Grains.Interfaces;
using ArgonContracts;
using Orleans.Concurrency;

[StatelessWorker]
public class NotificationGrain(
    IBadgeAggregationService badgeAggregation,
    IReadStateService readStateService,
    IMuteSettingsService muteSettingsService,
    ISystemNotificationService systemNotificationService) : Grain, INotificationGrain
{
    private Guid UserId => this.GetPrimaryKey();

    public Task<GlobalBadges> GetGlobalBadgesAsync(CancellationToken ct = default)
        => badgeAggregation.GetGlobalBadgesAsync(UserId, ct);

    public Task AckChannelAsync(Guid channelId, Guid? spaceId, long lastReadMessageId, CancellationToken ct = default)
        => readStateService.AckAsync(UserId, channelId, spaceId, lastReadMessageId, ct);

    public Task MuteAsync(Guid targetId, MuteTargetKind targetType, MuteLevelType muteLevel, bool suppressEveryone, DateTime? expiresAt, CancellationToken ct = default)
        => muteSettingsService.MuteAsync(
            UserId,
            targetId,
            targetType == MuteTargetKind.Space ? MuteTargetType.Space : MuteTargetType.Channel,
            muteLevel switch
            {
                MuteLevelType.OnlyMentions => MuteLevel.OnlyMentions,
                MuteLevelType.All          => MuteLevel.All,
                _                          => MuteLevel.None
            },
            suppressEveryone,
            expiresAt.HasValue ? new DateTimeOffset(expiresAt.Value, TimeSpan.Zero) : null,
            ct);

    public Task UnmuteAsync(Guid targetId, CancellationToken ct = default)
        => muteSettingsService.UnmuteAsync(UserId, targetId, ct);

    public Task<List<SystemNotificationDto>> GetNotificationFeedAsync(int limit, DateTime? before, CancellationToken ct = default)
        => systemNotificationService.GetFeedAsync(
            UserId,
            limit,
            before.HasValue ? new DateTimeOffset(before.Value, TimeSpan.Zero) : null,
            ct);

    public Task MarkNotificationReadAsync(Guid notificationId, CancellationToken ct = default)
        => systemNotificationService.MarkReadAsync(UserId, notificationId, ct);

    public Task MarkAllNotificationsReadAsync(string? type, CancellationToken ct = default)
        => systemNotificationService.MarkAllReadAsync(UserId, type, ct);
}
