namespace Argon.Core.Features.Logic;

using Core.Entities.Data;
using ArgonContracts;

public interface ISystemNotificationService
{
    Task<SystemNotificationEntity> CreateAsync(Guid userId, string type, Guid? referenceId, string title, string? body = null, CancellationToken ct = default);
    Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default);
    Task MarkAllReadAsync(Guid userId, string? type = null, CancellationToken ct = default);
    Task<List<SystemNotificationDto>> GetFeedAsync(Guid userId, int limit, DateTimeOffset? before = null, CancellationToken ct = default);
    Task<(int friendRequests, int inventory, int system)> GetBadgeCountsAsync(Guid userId, CancellationToken ct = default);
}
