namespace Argon.Core.Features.Logic;

public record NotificationCounters(
    long UnreadInventoryItems,
    long PendingFriendRequests,
    long UnreadDirectMessages
);

public interface INotificationCounterService
{
    Task<NotificationCounters> GetAllCountersAsync(Guid userId, CancellationToken ct = default);
    
    Task<long> GetCounterAsync(Guid userId, string counterType, CancellationToken ct = default);
    
    Task IncrementAsync(Guid userId, string counterType, long delta = 1, CancellationToken ct = default);
    
    Task DecrementAsync(Guid userId, string counterType, long delta = 1, CancellationToken ct = default);
    
    Task SetAsync(Guid userId, string counterType, long value, CancellationToken ct = default);
    
    Task ResetAsync(Guid userId, string counterType, CancellationToken ct = default);
    
    Task InvalidateCacheAsync(Guid userId, CancellationToken ct = default);
}
