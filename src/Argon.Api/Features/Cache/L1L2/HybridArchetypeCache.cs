namespace Argon.Services.L1L2;

using Features.Repositories;
using Metrics;
using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;
using System.Diagnostics;

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
        builder.Services.AddScoped<IArchetypeAgent, ArchetypeAgentHub>();
        builder.Services.AddScoped<IArchetypeCache, HybridArchetypeCache>();
    }
}

public class ArchetypeAgentHub(IArchetypeCache cache) : IArchetypeAgent
{
    public async Task<ArchetypeDto?> GetAsync(Guid serverId, Guid archetypeId, CancellationToken ct = default)
        => await cache.GetAsync(serverId, archetypeId);

    public async Task<List<ArchetypeDto>> GetAllAsync(Guid serverId, CancellationToken ct = default)
    {
        var e = await cache.GetAllAsync(serverId);
        return [..e];
    }

    public async Task<ArchetypeDto> DoCreatedAsync(Archetype archetype, CancellationToken ct = default)
    {
        await cache.SignalInvalidationAsync(archetype.ServerId, archetype.Id, ct);
        return archetype.ToDto();
    }
    public async Task<ArchetypeDto> DoUpdatedAsync(Archetype archetype, CancellationToken ct = default)
    {
        await cache.SignalInvalidationAsync(archetype.ServerId, archetype.Id, ct);
        return archetype.ToDto();
    }
}

public interface IArchetypeAgent
{
    Task<ArchetypeDto?>      GetAsync(Guid serverId, Guid archetypeId, CancellationToken ct = default);
    Task<List<ArchetypeDto>> GetAllAsync(Guid serverId, CancellationToken ct = default);

    Task<ArchetypeDto> DoCreatedAsync(Archetype archetype, CancellationToken ct = default);
    Task<ArchetypeDto> DoUpdatedAsync(Archetype archetype, CancellationToken ct = default);
}

public class HybridArchetypeCache(
    HybridCache cache,
    IArchetypeRepository repo,
    IMetricsCollector metrics,
    INatsClient nats) : IArchetypeCache
{
    public async Task<ArchetypeDto?> GetAsync(Guid serverId, Guid archetypeId)
    {
        var tags = new Dictionary<string, string>
        {
            ["server"]    = serverId.ToString(),
            ["archetype"] = archetypeId.ToString()
        };

        return await metrics.TimeAsync(HybridArchetypeCacheMetrics.GetDuration, async () =>
        {
            await metrics.CountAsync(HybridArchetypeCacheMetrics.GetCount, tags);
            return await cache.GetOrCreateAsync<ArchetypeDto>(
                $"archetype:{serverId}:{archetypeId}",
                async ct => await repo.GetByIdAsync(serverId, archetypeId, ct),
                tags: [$"server:{serverId}", "archetype"]
            );
        }, tags);
    }

    public async Task<IReadOnlyList<ArchetypeDto>> GetAllAsync(Guid serverId)
    {
        var tags = new Dictionary<string, string>
        {
            ["server"] = serverId.ToString()
        };
        return await metrics.TimeAsync(HybridArchetypeCacheMetrics.GetAllDuration, async () =>
        {
            await metrics.CountAsync(HybridArchetypeCacheMetrics.GetAllCount, tags);
            return await cache.GetOrCreateAsync<IReadOnlyList<ArchetypeDto>>(
                $"archetypes:{serverId}",
                async ct => await repo.GetAllAsync(serverId, ct),
                tags: [$"server:{serverId}", "archetypes"]
            );
        }, tags);
    }

    public async Task InvalidateAsync(Guid serverId, Guid archetypeId)
    {
        var tags = new Dictionary<string, string>
        {
            ["server"]    = serverId.ToString(),
            ["archetype"] = archetypeId.ToString()
        };
        await metrics.TimeAsync(HybridArchetypeCacheMetrics.InvalidateDuration, async () =>
        {
            await metrics.CountAsync(HybridArchetypeCacheMetrics.InvalidateCount, tags);
            await cache.RemoveByTagAsync($"server:{serverId}");
        }, tags);
    }

    public async Task SignalInvalidationAsync(Guid serverId, Guid archetypeId, CancellationToken cancellationToken = default)
    {
        await InvalidateAsync(serverId, archetypeId);
        await nats.PublishAsync(
            IArchetypeCache.InvalidationSubject,
            new NatsArchetypeInvalidateEvent(serverId, archetypeId),
            cancellationToken: cancellationToken);
    }
}

public record NatsArchetypeInvalidateEvent(Guid ServerId, Guid ArchetypeId);

public static class HybridArchetypeCacheMetrics
{
    public static readonly MeasurementId ReceivedMsg             = new("archetypes.nats.received");
    public static readonly MeasurementId CacheInvalidated        = new("archetypes.cache.invalidated");
    public static readonly MeasurementId CacheInvalidationError  = new("archetypes.cache.invalidate.error");
    public static readonly MeasurementId CacheInvalidateDuration = new("archetypes.cache.invalidate.duration");

    public static readonly MeasurementId GetCount           = new("archetypes.cache.get");
    public static readonly MeasurementId GetAllCount        = new("archetypes.cache.get_all");
    public static readonly MeasurementId InvalidateCount    = new("archetypes.cache.invalidate");
    public static readonly MeasurementId GetDuration        = new("archetypes.cache.get.duration");
    public static readonly MeasurementId GetAllDuration     = new("archetypes.cache.get_all.duration");
    public static readonly MeasurementId InvalidateDuration = new("archetypes.cache.invalidate.duration");
}

public class HybridArchetypeCacheAdapter(
    INatsClient nats,
    IServiceProvider provider,
    ILogger<HybridArchetypeCacheAdapter> logger,
    IMetricsCollector metrics) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in nats.SubscribeAsync<NatsArchetypeInvalidateEvent>(IArchetypeCache.InvalidationSubject,
                           cancellationToken: stoppingToken))
        {
            if (msg.Data is null)
                continue;

            var tags = new Dictionary<string, string>
            {
                ["server"]    = msg.Data.ServerId.ToString(),
                ["archetype"] = msg.Data.ArchetypeId.ToString()
            };

            await metrics.CountAsync(HybridArchetypeCacheMetrics.ReceivedMsg, tags);

            var start = Stopwatch.GetTimestamp();

            try
            {
                await using var scope = provider.CreateAsyncScope();
                var             cache = scope.ServiceProvider.GetRequiredService<IArchetypeCache>();
                await cache.InvalidateAsync(msg.Data.ServerId, msg.Data.ArchetypeId);
                await metrics.CountAsync(HybridArchetypeCacheMetrics.CacheInvalidated, tags);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to invalidate archetype cache for server {ServerId}, role {RoleId}",
                    msg.Data.ServerId, msg.Data.ArchetypeId);
                await metrics.CountAsync(HybridArchetypeCacheMetrics.CacheInvalidationError, tags);
            }
            finally
            {
                var duration = Stopwatch.GetElapsedTime(start);
                await metrics.DurationAsync(HybridArchetypeCacheMetrics.CacheInvalidateDuration, duration, tags);
            }
        }
    }
}

public interface IArchetypeCache
{
    public const string InvalidationSubject = "archetypes.invalidate";

    Task<ArchetypeDto?>               GetAsync(Guid serverId, Guid archetypeId);
    Task<IReadOnlyList<ArchetypeDto>> GetAllAsync(Guid serverId);

    Task InvalidateAsync(Guid serverId, Guid archetypeId);
    Task SignalInvalidationAsync(Guid serverId, Guid roleId, CancellationToken cancellationToken = default);
}