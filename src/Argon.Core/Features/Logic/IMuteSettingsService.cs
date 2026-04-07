namespace Argon.Core.Features.Logic;

using Core.Entities.Data;

public interface IMuteSettingsService
{
    Task MuteAsync(Guid userId, Guid targetId, MuteTargetType targetType, MuteLevel muteLevel,
        bool suppressEveryone = false, DateTimeOffset? expiresAt = null, CancellationToken ct = default);
    Task UnmuteAsync(Guid userId, Guid targetId, CancellationToken ct = default);
    Task<List<MuteSettingsEntity>> GetMuteSettingsAsync(Guid userId, CancellationToken ct = default);
    Task<bool> IsMutedAsync(Guid userId, Guid targetId, CancellationToken ct = default);
    Task<HashSet<Guid>> FilterMutedUsersAsync(Guid channelId, Guid spaceId, IReadOnlyList<Guid> userIds, CancellationToken ct = default);
}
