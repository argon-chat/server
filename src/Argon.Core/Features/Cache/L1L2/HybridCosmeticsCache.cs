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
///
/// <para><b>A fill can lose a race with a change.</b> The read grain loads its misses in one query
/// and writes them back afterwards, and <c>HybridCache</c> stamps an entry with the moment it is
/// written, not the moment its rows were read. So rows read just before somebody's change commits,
/// written back just after the drop, make an entry newer than the drop, and every silo serves it for
/// the whole expiry. <c>GetOrCreateAsync</c> is no way out: when the drop lands while its factory
/// runs, it re-stamps the result as of now, so the stale answer survives just the same. Two things
/// close it, and each covers what the other cannot: <see cref="WornDropLedger"/> for a silo that
/// heard of the drop before writing, and <see cref="SecondDropDelay"/> for one that had not heard
/// yet.</para>
/// </remarks>
public interface ICosmeticsCache
{
    public const string InvalidationSubject = "cosmetics.invalidate";

    /// <summary>Every entry this cache holds, so one sweep can drop the lot.</summary>
    public const string AllTag = "cosmetics";

    public const string CatalogueKey = "cosmetics:catalogue";

    public static string WornKey(Guid userId) => $"cosmetics:worn:{userId}";

    public static string WornTag(Guid userId) => $"cosmetics:worn:{userId}";

    /// <summary>The tags a worn entry carries, both when it is written and when it is looked up.</summary>
    /// <remarks>
    /// Both, because <c>HybridCache</c> files the copy it takes from Redis under the tags of the call
    /// that found it, not the tags stored with it. A lookup without them leaves a copy in memory that no
    /// drop can reach.
    /// </remarks>
    public static string[] WornTags(Guid userId) => [AllTag, WornTag(userId)];

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

    /// <summary>
    /// Looks a worn entry up in memory and then in Redis without ever running a query, so a miss
    /// comes back as null and the caller can gather every miss into one read.
    /// </summary>
    /// <remarks>
    /// <c>DisableUnderlyingData</c> is all it takes: with no factory to run, <c>HybridCache</c> writes
    /// nothing anywhere for a miss. A hit found in Redis is still copied into memory, as any lookup's
    /// is, so the next member list on this silo does not go back for it. The copy is kept as long as
    /// <see cref="WornOptions"/> keeps its own, which has to be said here: left unsaid, it would take
    /// the process-wide default of two days, cut only to whatever Redis has left.
    /// </remarks>
    public static readonly HybridCacheEntryOptions WornLookupOptions = new()
    {
        LocalCacheExpiration = WornOptions.LocalCacheExpiration,
        Flags                = HybridCacheEntryFlags.DisableUnderlyingData
    };

    /// <summary>How long after dropping a person's entry every silo drops it once more.</summary>
    /// <remarks>
    /// For a fill that read before a change and wrote after the drop, on a silo the drop had not
    /// reached yet, so the ledger could not warn it. Its write is newer than the drop on every silo
    /// that heard sooner, and outlives it there; the second drop is newer still. The write can trail
    /// those silos' first drop by no more than the time the message took to reach its own, so a few
    /// seconds is ample.
    /// </remarks>
    public static readonly TimeSpan SecondDropDelay = TimeSpan.FromSeconds(5);

    /// <summary>Drops one person's entry in this process only.</summary>
    Task InvalidateWornAsync(Guid userId);

    /// <summary>
    /// Drops it here and tells every other silo to do the same. A profile is read on whichever silo
    /// the reader landed on, so the copy that matters is rarely the one beside the write.
    /// </summary>
    /// <remarks>
    /// This silo hears its own message too, since NATS echoes to the publishing connection unless told
    /// not to, and so it gets its <see cref="SecondDropDelay"/> pass like every other.
    /// </remarks>
    Task SignalWornInvalidationAsync(Guid userId, CancellationToken ct = default);
}

public record NatsCosmeticsInvalidateEvent(Guid UserId);

/// <summary>
/// How many times this process has dropped each person's worn entry, so a fill can tell that it
/// read across a change.
/// </summary>
/// <remarks>
/// <para>A fill reads the count before its query and again before writing, and writes nothing for a
/// person whose count moved in between: their rows may predate the change, and an entry written now
/// would be newer than the drop.</para>
///
/// <para>Striped rather than kept per person: a fixed four thousand counters, shared by whoever hashes
/// to the same one. A collision can only make a fill skip a write it could have made, never make one
/// it should not have, and the ledger stays the same size however many people pass through.</para>
/// </remarks>
public sealed class WornDropLedger
{
    private const int Stripes = 4096;

    private readonly long[] drops = new long[Stripes];

    public long Of(Guid userId) => Volatile.Read(ref drops[StripeOf(userId)]);

    public void Note(Guid userId) => Interlocked.Increment(ref drops[StripeOf(userId)]);

    private static int StripeOf(Guid userId) => (int)((uint)userId.GetHashCode() % Stripes);
}

public sealed class HybridCosmeticsCache(HybridCache cache, INatsClient nats, WornDropLedger ledger) : ICosmeticsCache
{
    public async Task InvalidateWornAsync(Guid userId)
    {
        // Noted before the entry goes, so a fill that checks after it has gone cannot miss that it went.
        ledger.Note(userId);
        await cache.RemoveByTagAsync(ICosmeticsCache.WornTag(userId));
    }

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

            await DropAsync(msg.Data.UserId);

            // Not awaited, so the next message does not queue behind the wait.
            _ = DropAgainAsync(msg.Data.UserId, stoppingToken);
        }
    }

    /// <summary>The second drop — see <see cref="ICosmeticsCache.SecondDropDelay"/>.</summary>
    private async Task DropAgainAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(ICosmeticsCache.SecondDropDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await DropAsync(userId);
    }

    private async Task DropAsync(Guid userId)
    {
        try
        {
            await using var scope = provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<ICosmeticsCache>().InvalidateWornAsync(userId);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to invalidate the worn cosmetics of {UserId}", userId);
        }
    }
}
