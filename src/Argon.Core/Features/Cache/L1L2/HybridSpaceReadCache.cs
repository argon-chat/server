namespace Argon.Services.L1L2;

using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;

/// <summary>
/// Invalidation for the answers <c>ISpaceReadGrain</c> caches: the member roster, the channel list
/// and the channel groups of one space.
/// </summary>
/// <remarks>
/// Only the invalidation half lives here. The queries stay in the grain, because they are database
/// reads and those belong to grains.
/// <para>
/// One tag, per space, deliberately. Every entry is an answer about the space as a whole — even a
/// change to one member's archetypes shows up in the roster everybody reads — so there is nothing a
/// finer tag could drop on its own.
/// </para>
/// </remarks>
public interface ISpaceReadCache
{
    public const string InvalidationSubject = "space.read.invalidate";

    /// <summary>Everything cached about one space.</summary>
    public static string SpaceTag(Guid spaceId) => $"space:read:{spaceId}";

    /// <summary>Drops the entries in this process only.</summary>
    Task InvalidateAsync(Guid spaceId);

    /// <summary>
    /// Drops them here and tells every other silo to do the same. A space is read on whichever silo
    /// the caller landed on, so the copy that matters is rarely the one that did the write.
    /// </summary>
    Task SignalInvalidationAsync(Guid spaceId, CancellationToken ct = default);
}

public record NatsSpaceReadInvalidateEvent(Guid SpaceId);

public sealed class HybridSpaceReadCache(HybridCache cache, INatsClient nats) : ISpaceReadCache
{
    public async Task InvalidateAsync(Guid spaceId)
        => await cache.RemoveByTagAsync(ISpaceReadCache.SpaceTag(spaceId));

    public async Task SignalInvalidationAsync(Guid spaceId, CancellationToken ct = default)
    {
        await InvalidateAsync(spaceId);
        await nats.PublishAsync(ISpaceReadCache.InvalidationSubject,
            new NatsSpaceReadInvalidateEvent(spaceId), cancellationToken: ct);
    }
}

public sealed class HybridSpaceReadCacheAdapter(
    INatsClient nats,
    IServiceProvider provider,
    ILogger<HybridSpaceReadCacheAdapter> logger) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in nats.SubscribeAsync<NatsSpaceReadInvalidateEvent>(
                           ISpaceReadCache.InvalidationSubject, cancellationToken: stoppingToken))
        {
            if (msg.Data is null)
                continue;

            try
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ISpaceReadCache>()
                   .InvalidateAsync(msg.Data.SpaceId);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to invalidate the space read cache for {SpaceId}", msg.Data.SpaceId);
            }
        }
    }
}
