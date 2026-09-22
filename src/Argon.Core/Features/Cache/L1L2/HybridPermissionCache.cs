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

    /// <summary>
    /// For the two reads behind every channel access check, which sit on the send, reaction and
    /// drawing paths. Shorter than the base entry: the tags are the real invalidation, this bounds a
    /// lost one.
    /// </summary>
    private static readonly HybridCacheEntryOptions AccessOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromSeconds(30),
        Flags                = HybridCacheEntryFlags.DisableCompression
    };

    /// <summary>
    /// Every permission entry of a space also carries the space read tag. Joins, leaves, bot installs
    /// and channel overwrite edits signal that tag and not the permission ones, and a revoked right
    /// must not outlive them.
    /// </summary>
    private static string[] MemberTags(Guid spaceId, Guid userId)
        => [$"perm:space:{spaceId}", $"perm:member:{spaceId}:{userId}", ISpaceReadCache.SpaceTag(spaceId)];

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
            tags: MemberTags(spaceId, userId),
            cancellationToken: ct
        );

    public async Task<SpaceMemberEntity?> GetMemberWithArchetypesAsync(Guid spaceId, Guid userId, CancellationToken ct = default)
    {
        var grant = await cache.GetOrCreateAsync(
            $"perm:grant:{spaceId}:{userId}",
            (ctx, spaceId, userId),
            static async (state, cancel) =>
            {
                await using var db = await state.ctx.CreateDbContextAsync(cancel);

                // One round trip: a member holds a handful of archetypes, so the split the global
                // setting would make buys nothing here.
                var member = await db.UsersToServerRelations
                   .AsNoTracking()
                   .Where(x => x.SpaceId == state.spaceId && x.UserId == state.userId)
                   .Select(x => new
                    {
                        x.Id,
                        Archetypes = x.SpaceMemberArchetypes
                           .Select(a => new CachedArchetypeGrant(a.ArchetypeId, a.Archetype.Entitlement))
                           .ToList()
                    })
                   .AsSingleQuery()
                   .FirstOrDefaultAsync(cancel);

                return new CachedMemberGrant(member?.Id, member?.Archetypes.ToArray() ?? []);
            },
            AccessOptions,
            MemberTags(spaceId, userId),
            ct);

        return grant.AsEntity(spaceId, userId);
    }

    public async Task<ChannelEntity?> GetChannelWithOverwritesAsync(Guid spaceId, Guid channelId, CancellationToken ct = default)
    {
        var channel = await cache.GetOrCreateAsync(
            $"perm:channel:{spaceId}:{channelId}",
            (ctx, spaceId, channelId),
            static async (state, cancel) =>
            {
                await using var db = await state.ctx.CreateDbContextAsync(cancel);

                var row = await db.Channels
                   .AsNoTracking()
                   .Where(c => c.Id == state.channelId && c.SpaceId == state.spaceId)
                   .Select(c => new
                    {
                        c.Id,
                        Overwrites = c.EntitlementOverwrites
                           .Select(o => new CachedChannelOverwrite(o.Scope, o.ArchetypeId, o.SpaceMemberId, o.Allow, o.Deny))
                           .ToList()
                    })
                   .AsSingleQuery()
                   .FirstOrDefaultAsync(cancel);

                return new CachedChannelGrant(row is not null, row?.Overwrites.ToArray() ?? []);
            },
            AccessOptions,
            [$"perm:space:{spaceId}", $"perm:channel:{channelId}", ISpaceReadCache.SpaceTag(spaceId)],
            ct);

        return channel.AsEntity(spaceId, channelId);
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
    Task<ChannelEntity?> GetChannelWithOverwritesAsync(Guid spaceId, Guid channelId, CancellationToken ct = default);

    Task InvalidateMemberAsync(Guid spaceId, Guid userId);
    Task InvalidateSpaceAsync(Guid spaceId);
    Task SignalMemberInvalidationAsync(Guid spaceId, Guid userId, CancellationToken ct = default);
    Task SignalSpaceInvalidationAsync(Guid spaceId, CancellationToken ct = default);
}

// Flat projections rather than the EF graphs: the navigations are cyclic, which the cache serializer
// refuses. Immutable so an L1 hit hands back the same instance instead of deserializing it again.

[ImmutableObject(true)]
public sealed record CachedArchetypeGrant(Guid ArchetypeId, ArgonEntitlement Entitlement);

[ImmutableObject(true)]
public sealed record CachedMemberGrant(Guid? MemberId, CachedArchetypeGrant[] Archetypes)
{
    public SpaceMemberEntity? AsEntity(Guid spaceId, Guid userId)
        => MemberId is not { } memberId
            ? null
            : new SpaceMemberEntity
            {
                Id      = memberId,
                SpaceId = spaceId,
                UserId  = userId,
                SpaceMemberArchetypes = Archetypes.Select(a => new SpaceMemberArchetypeEntity
                {
                    SpaceMemberId = memberId,
                    ArchetypeId   = a.ArchetypeId,
                    Archetype     = new ArchetypeEntity { Id = a.ArchetypeId, SpaceId = spaceId, Entitlement = a.Entitlement }
                }).ToList()
            };
}

[ImmutableObject(true)]
public sealed record CachedChannelOverwrite(
    IArchetypeScope Scope,
    Guid? ArchetypeId,
    Guid? SpaceMemberId,
    ArgonEntitlement Allow,
    ArgonEntitlement Deny);

[ImmutableObject(true)]
public sealed record CachedChannelGrant(bool Exists, CachedChannelOverwrite[] Overwrites)
{
    public ChannelEntity? AsEntity(Guid spaceId, Guid channelId)
        => !Exists
            ? null
            : new ChannelEntity
            {
                Id      = channelId,
                SpaceId = spaceId,
                EntitlementOverwrites = Overwrites.Select(o => new ChannelEntitlementOverwriteEntity
                {
                    ChannelId     = channelId,
                    Scope         = o.Scope,
                    ArchetypeId   = o.ArchetypeId,
                    SpaceMemberId = o.SpaceMemberId,
                    Allow         = o.Allow,
                    Deny          = o.Deny
                }).ToList()
            };
}
