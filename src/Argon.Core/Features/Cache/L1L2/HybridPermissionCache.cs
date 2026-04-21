namespace Argon.Services.L1L2;

using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;

public class HybridPermissionCache(
    HybridCache cache,
    IDbContextFactory<ApplicationDbContext> ctx,
    INatsClient nats) : IPermissionCache
{
    private static readonly HybridCacheEntryOptions CacheOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(5),
        Flags                = HybridCacheEntryFlags.DisableCompression
    };

    public async Task<ArgonEntitlement> GetBasePermissionsAsync(Guid spaceId, Guid userId, CancellationToken ct = default)
        => await cache.GetOrCreateAsync(
            $"perm:base:{spaceId}:{userId}",
            async cancel =>
            {
                await using var db = await ctx.CreateDbContextAsync(cancel);
                var entitlements = await db.UsersToServerRelations
                   .AsNoTracking()
                   .Where(x => x.SpaceId == spaceId && x.UserId == userId)
                   .SelectMany(x => x.SpaceMemberArchetypes)
                   .Select(x => x.Archetype.Entitlement)
                   .ToListAsync(cancel);
                return entitlements.Aggregate(ArgonEntitlement.None, (a, b) => a | b);
            },
            options: CacheOptions,
            tags: [$"perm:space:{spaceId}", $"perm:member:{spaceId}:{userId}"],
            cancellationToken: ct
        );

    public async Task<SpaceMemberEntity?> GetMemberWithArchetypesAsync(Guid spaceId, Guid userId, CancellationToken ct = default)
    {
        await using var db = await ctx.CreateDbContextAsync(ct);
        return await db.UsersToServerRelations
           .AsNoTracking()
           .Where(x => x.SpaceId == spaceId && x.UserId == userId)
           .Include(x => x.SpaceMemberArchetypes)
           .ThenInclude(x => x.Archetype)
           .FirstOrDefaultAsync(ct);
    }

    public async Task<ChannelEntity?> GetChannelWithOverwritesAsync(Guid channelId, CancellationToken ct = default)
    {
        await using var db = await ctx.CreateDbContextAsync(ct);
        return await db.Channels
           .AsNoTracking()
           .Include(c => c.EntitlementOverwrites)
           .FirstOrDefaultAsync(c => c.Id == channelId, ct);
    }

    public async Task InvalidateMemberAsync(Guid spaceId, Guid userId)
        => await cache.RemoveByTagAsync($"perm:member:{spaceId}:{userId}");

    public async Task InvalidateSpaceAsync(Guid spaceId)
        => await cache.RemoveByTagAsync($"perm:space:{spaceId}");

    public async Task SignalMemberInvalidationAsync(Guid spaceId, Guid userId, CancellationToken ct = default)
    {
        await InvalidateMemberAsync(spaceId, userId);
        await nats.PublishAsync(
            IPermissionCache.MemberInvalidationSubject,
            new NatsPermissionInvalidateEvent(spaceId, userId, null),
            cancellationToken: ct);
    }

    public async Task SignalSpaceInvalidationAsync(Guid spaceId, CancellationToken ct = default)
    {
        await InvalidateSpaceAsync(spaceId);
        await nats.PublishAsync(
            IPermissionCache.SpaceInvalidationSubject,
            new NatsPermissionInvalidateEvent(spaceId, null, null),
            cancellationToken: ct);
    }
}

public record NatsPermissionInvalidateEvent(Guid SpaceId, Guid? UserId, Guid? ChannelId);

public class HybridPermissionCacheAdapter(
    INatsClient nats,
    IServiceProvider provider,
    ILogger<HybridPermissionCacheAdapter> logger) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var memberTask = ProcessMemberInvalidations(stoppingToken);
        var spaceTask  = ProcessSpaceInvalidations(stoppingToken);
        await Task.WhenAll(memberTask, spaceTask);
    }

    private async Task ProcessMemberInvalidations(CancellationToken ct)
    {
        await foreach (var msg in nats.SubscribeAsync<NatsPermissionInvalidateEvent>(
                           IPermissionCache.MemberInvalidationSubject, cancellationToken: ct))
        {
            if (msg.Data is not { UserId: not null } data) continue;

            try
            {
                await using var scope = provider.CreateAsyncScope();
                var             cache = scope.ServiceProvider.GetRequiredService<IPermissionCache>();
                await cache.InvalidateMemberAsync(data.SpaceId, data.UserId.Value);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to invalidate permission cache for member {UserId} in space {SpaceId}",
                    msg.Data?.UserId, msg.Data?.SpaceId);
            }
        }
    }

    private async Task ProcessSpaceInvalidations(CancellationToken ct)
    {
        await foreach (var msg in nats.SubscribeAsync<NatsPermissionInvalidateEvent>(
                           IPermissionCache.SpaceInvalidationSubject, cancellationToken: ct))
        {
            if (msg.Data is null) continue;

            try
            {
                await using var scope = provider.CreateAsyncScope();
                var             cache = scope.ServiceProvider.GetRequiredService<IPermissionCache>();
                await cache.InvalidateSpaceAsync(msg.Data.SpaceId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to invalidate permission cache for space {SpaceId}",
                    msg.Data?.SpaceId);
            }
        }
    }
}

public interface IPermissionCache
{
    public const string MemberInvalidationSubject = "permissions.member.invalidate";
    public const string SpaceInvalidationSubject  = "permissions.space.invalidate";

    Task<ArgonEntitlement> GetBasePermissionsAsync(Guid spaceId, Guid userId, CancellationToken ct = default);
    Task<SpaceMemberEntity?> GetMemberWithArchetypesAsync(Guid spaceId, Guid userId, CancellationToken ct = default);
    Task<ChannelEntity?> GetChannelWithOverwritesAsync(Guid channelId, CancellationToken ct = default);

    Task InvalidateMemberAsync(Guid spaceId, Guid userId);
    Task InvalidateSpaceAsync(Guid spaceId);
    Task SignalMemberInvalidationAsync(Guid spaceId, Guid userId, CancellationToken ct = default);
    Task SignalSpaceInvalidationAsync(Guid spaceId, CancellationToken ct = default);
}
