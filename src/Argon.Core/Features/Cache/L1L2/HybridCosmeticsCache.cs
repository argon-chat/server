namespace Argon.Services.L1L2;

using Microsoft.Extensions.Caching.Hybrid;
using NATS.Client.Core;

/// <summary>
/// Invalidation for what <c>ICosmeticsReadGrain</c> caches: the catalogue, and what each person is
/// wearing.
/// </summary>
/// <remarks>
/// <para>Only the invalidation half lives here, as with <see cref="ISpaceReadCache"/>. The queries
/// stay in the grain, because they are database reads and those belong to grains.</para>
///
/// <para><b>Why it is cached at all.</b> What somebody is wearing is read by every member list,
/// every message author line and every profile card, and it changes when that one person opens a
/// settings pane — a few times a month. A reconnect wave of a few thousand clients would otherwise be
/// a few thousand identical reads of the same rows. Held in process and in Redis, it is one read per
/// person per expiry, and a write drops exactly that person's entry everywhere.</para>
/// </remarks>
public interface ICosmeticsCache
{
    public const string InvalidationSubject = "cosmetics.invalidate";

    /// <summary>Every entry this cache holds, so one sweep can drop the lot.</summary>
    public const string AllTag = "cosmetics";

    public const string CatalogueKey = "cosmetics:catalogue";

    public static string WornKey(Guid userId) => $"cosmetics:worn:{userId}";

    public static string WornTag(Guid userId) => $"cosmetics:worn:{userId}";

    /// <summary>
    /// The same for everybody, and written only from the console. Short enough that a row published
    /// by hand reaches pickers without anybody having to ask.
    /// </summary>
    public static readonly HybridCacheEntryOptions CatalogueOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = TimeSpan.FromMinutes(1)
    };

    /// <summary>
    /// Dropped explicitly whenever the wearer changes anything, so the expiry is only a ceiling on
    /// what it cannot hear about — a kind being switched off, a catalogue row being re-authored.
    /// </summary>
    public static readonly HybridCacheEntryOptions WornOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(15),
        LocalCacheExpiration = TimeSpan.FromMinutes(2)
    };

    /// <summary>Drops one person's entry in this process only.</summary>
    Task InvalidateWornAsync(Guid userId);

    /// <summary>
    /// Drops it here and tells every other silo to do the same. A profile is read on whichever silo
    /// the reader landed on, so the copy that matters is rarely the one beside the write.
    /// </summary>
    Task SignalWornInvalidationAsync(Guid userId, CancellationToken ct = default);
}

public record NatsCosmeticsInvalidateEvent(Guid UserId);

public sealed class HybridCosmeticsCache(HybridCache cache, INatsClient nats) : ICosmeticsCache
{
    public async Task InvalidateWornAsync(Guid userId)
        => await cache.RemoveByTagAsync(ICosmeticsCache.WornTag(userId));

    public async Task SignalWornInvalidationAsync(Guid userId, CancellationToken ct = default)
    {
        await InvalidateWornAsync(userId);
        await nats.PublishAsync(ICosmeticsCache.InvalidationSubject,
            new NatsCosmeticsInvalidateEvent(userId), cancellationToken: ct);
    }
}

public sealed class HybridCosmeticsCacheAdapter(
    INatsClient nats,
    IServiceProvider provider,
    ILogger<HybridCosmeticsCacheAdapter> logger) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var msg in nats.SubscribeAsync<NatsCosmeticsInvalidateEvent>(
                           ICosmeticsCache.InvalidationSubject, cancellationToken: stoppingToken))
        {
            if (msg.Data is null)
                continue;

            try
            {
                await using var scope = provider.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ICosmeticsCache>()
                   .InvalidateWornAsync(msg.Data.UserId);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Failed to invalidate the worn cosmetics of {UserId}", msg.Data.UserId);
            }
        }
    }
}
