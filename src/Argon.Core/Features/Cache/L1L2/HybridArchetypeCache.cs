namespace Argon.Services.L1L2;

using Features.Repositories;
using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;

public static class L1L2CacheExtensions
{
    public static void AddHybridCache(this WebApplicationBuilder builder)
    {
        builder.Services.AddHybridCache(options =>
        {
            options.MaximumPayloadBytes = 1024 * 1024 * 512;
            options.MaximumKeyLength    = 512;
            options.DisableCompression  = true;

            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration           = TimeSpan.FromHours(48),
                LocalCacheExpiration = TimeSpan.FromHours(48),
                Flags                = HybridCacheEntryFlags.DisableCompression
            };
        });
    }
    public static void AddArchetypesCache(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IArchetypeAgent, ArchetypeAgentHub>();
        builder.Services.AddScoped<IArchetypeCache, HybridArchetypeCache>();
    }
}

public class ArchetypeAgentHub(IArchetypeCache cache) : IArchetypeAgent
{
    public async Task<Archetype?> GetAsync(Guid spaceId, Guid archetypeId, CancellationToken ct = default)
        => await cache.GetAsync(spaceId, archetypeId);

    public async Task<List<Archetype>> GetAllAsync(Guid spaceId, CancellationToken ct = default)
    {
        var e = await cache.GetAllAsync(spaceId);
        return [..e];
    }

    public async Task<Archetype> DoCreatedAsync(ArchetypeEntity archetype, CancellationToken ct = default)
    {
        await cache.SignalInvalidationAsync(archetype.SpaceId, archetype.Id, ct);
        return archetype.ToDto();
    }
    public async Task<Archetype> DoUpdatedAsync(ArchetypeEntity archetype, CancellationToken ct = default)
    {
        await cache.SignalInvalidationAsync(archetype.SpaceId, archetype.Id, ct);
        return archetype.ToDto();
    }
}

public interface IArchetypeAgent
{
    Task<Archetype?>      GetAsync(Guid spaceId, Guid archetypeId, CancellationToken ct = default);
    Task<List<Archetype>> GetAllAsync(Guid spaceId, CancellationToken ct = default);

    Task<Archetype> DoCreatedAsync(ArchetypeEntity archetype, CancellationToken ct = default);
    Task<Archetype> DoUpdatedAsync(ArchetypeEntity archetype, CancellationToken ct = default);
}

public class HybridArchetypeCache(
    HybridCache cache,
    IArchetypeRepository repo,
    INatsClient nats) : IArchetypeCache
{
    public async Task<Archetype?> GetAsync(Guid spaceId, Guid archetypeId)
        => await cache.GetOrCreateAsync<Archetype>(
            $"archetype:{spaceId}:{archetypeId}",
            async ct => await repo.GetByIdAsync(spaceId, archetypeId, ct),
            tags: [$"server:{spaceId}", "archetype"]
        );

    public async Task<IReadOnlyList<Archetype>> GetAllAsync(Guid spaceId)
        => await cache.GetOrCreateAsync<IReadOnlyList<Archetype>>(
            $"archetypes:{spaceId}",
            async ct => await repo.GetAllAsync(spaceId, ct),
            tags: [$"server:{spaceId}", "archetypes"]
        );

    public async Task InvalidateAsync(Guid spaceId, Guid archetypeId)
        => await cache.RemoveByTagAsync($"server:{spaceId}");

    public async Task SignalInvalidationAsync(Guid spaceId, Guid archetypeId, CancellationToken cancellationToken = default)
    {
        await InvalidateAsync(spaceId, archetypeId);
        await nats.PublishAsync(
            IArchetypeCache.InvalidationSubject,
            new NatsArchetypeInvalidateEvent(spaceId, archetypeId),
            cancellationToken: cancellationToken);
    }
}

public record NatsArchetypeInvalidateEvent(Guid SpaceId, Guid ArchetypeId);


public class HybridArchetypeCacheAdapter(
    INatsClient nats,
    IServiceProvider provider,
    ILogger<HybridArchetypeCacheAdapter> logger) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in nats.SubscribeAsync<NatsArchetypeInvalidateEvent>(IArchetypeCache.InvalidationSubject,
                           cancellationToken: stoppingToken))
        {
            if (msg.Data is null)
                continue;

            try
            {
                await using var scope = provider.CreateAsyncScope();
                var             cache = scope.ServiceProvider.GetRequiredService<IArchetypeCache>();
                await cache.InvalidateAsync(msg.Data.SpaceId, msg.Data.ArchetypeId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to invalidate archetype cache for server {SpaceId}, role {RoleId}",
                    msg.Data.SpaceId, msg.Data.ArchetypeId);
            }
        }
    }
}

public interface IArchetypeCache
{
    public const string InvalidationSubject = "archetypes.invalidate";

    Task<Archetype?>               GetAsync(Guid spaceId, Guid archetypeId);
    Task<IReadOnlyList<Archetype>> GetAllAsync(Guid spaceId);

    Task InvalidateAsync(Guid spaceId, Guid archetypeId);
    Task SignalInvalidationAsync(Guid spaceId, Guid roleId, CancellationToken cancellationToken = default);
}