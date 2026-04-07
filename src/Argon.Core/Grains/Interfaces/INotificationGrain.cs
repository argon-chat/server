namespace Argon.Core.Grains.Interfaces;

using ArgonContracts;

public interface INotificationGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetGlobalBadgesAsync))]
    Task<GlobalBadges> GetGlobalBadgesAsync(CancellationToken ct = default);

    [Alias(nameof(AckChannelAsync))]
    Task AckChannelAsync(Guid channelId, Guid? spaceId, long lastReadMessageId, CancellationToken ct = default);

    [Alias(nameof(MuteAsync))]
    Task MuteAsync(Guid targetId, MuteTargetKind targetType, MuteLevelType muteLevel, bool suppressEveryone, DateTime? expiresAt, CancellationToken ct = default);

    [Alias(nameof(UnmuteAsync))]
    Task UnmuteAsync(Guid targetId, CancellationToken ct = default);

    [Alias(nameof(GetNotificationFeedAsync))]
    Task<List<SystemNotificationDto>> GetNotificationFeedAsync(int limit, DateTime? before, CancellationToken ct = default);

    [Alias(nameof(MarkNotificationReadAsync))]
    Task MarkNotificationReadAsync(Guid notificationId, CancellationToken ct = default);

    [Alias(nameof(MarkAllNotificationsReadAsync))]
    Task MarkAllNotificationsReadAsync(string? type, CancellationToken ct = default);
}
