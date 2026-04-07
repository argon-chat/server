namespace Argon.Core.Features.Logic;

using Argon.Core.Entities.Data;
using Argon.Entities;
using ArgonContracts;

public class SystemNotificationService(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    ILogger<SystemNotificationService> logger) : ISystemNotificationService
{
    public async Task<SystemNotificationEntity> CreateAsync(Guid userId, string type, Guid? referenceId, string title, string? body = null, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var entity = new SystemNotificationEntity
        {
            UserId      = userId,
            Type        = type,
            ReferenceId = referenceId,
            Title       = title,
            Body        = body,
            IsRead      = false,
            CreatedAt   = DateTimeOffset.UtcNow
        };

        ctx.SystemNotifications.Add(entity);
        await ctx.SaveChangesAsync(ct);

        try
        {
            var sessions = await sessionDiscovery.GetUserSessionsAsync(userId, ct);
            if (sessions.Count > 0)
            {
                var dto = new SystemNotificationDto(
                    entity.Id,
                    entity.Type,
                    entity.ReferenceId,
                    entity.Title,
                    entity.Body,
                    entity.IsRead,
                    entity.CreatedAt.UtcDateTime
                );

                await notifier.NotifySessionsAsync(sessions,
                    new SystemNotificationReceived(userId, dto), ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to push system notification event for user {UserId}", userId);
        }

        return entity;
    }

    public async Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        await ctx.SystemNotifications
            .Where(x => x.Id == notificationId && x.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true), ct);
    }

    public async Task MarkAllReadAsync(Guid userId, string? type = null, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var query = ctx.SystemNotifications
            .Where(x => x.UserId == userId && !x.IsRead);

        if (type is not null)
            query = query.Where(x => x.Type == type);

        await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsRead, true), ct);
    }

    public async Task<List<SystemNotificationDto>> GetFeedAsync(Guid userId, int limit, DateTimeOffset? before = null, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var query = ctx.SystemNotifications
            .AsNoTracking()
            .Where(x => x.UserId == userId);

        if (before.HasValue)
            query = query.Where(x => x.CreatedAt < before.Value);

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(limit)
            .Select(x => new SystemNotificationDto(
                x.Id,
                x.Type,
                x.ReferenceId,
                x.Title,
                x.Body,
                x.IsRead,
                x.CreatedAt.UtcDateTime
            ))
            .ToListAsync(ct);
    }

    public async Task<(int friendRequests, int inventory, int system)> GetBadgeCountsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync(ct);

        var counts = await ctx.SystemNotifications
            .AsNoTracking()
            .Where(x => x.UserId == userId && !x.IsRead)
            .GroupBy(x => x.Type)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var friendRequests = counts
            .Where(c => c.Type == SystemNotificationType.FriendRequestReceived)
            .Sum(c => c.Count);

        var inventory = counts
            .Where(c => c.Type == SystemNotificationType.ItemReceived)
            .Sum(c => c.Count);

        var system = counts
            .Where(c => c.Type == SystemNotificationType.SystemAnnouncement
                     || c.Type == SystemNotificationType.FriendRequestAccepted)
            .Sum(c => c.Count);

        return (friendRequests, inventory, system);
    }
}
