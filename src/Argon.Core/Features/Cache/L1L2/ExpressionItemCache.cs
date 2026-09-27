namespace Argon.Services.L1L2;

using System.ComponentModel;
using Microsoft.Extensions.Caching.Hybrid;

/// <summary>
/// Custom emoji by item id, as a status shows them. An id that names no live emoji is cached too, briefly.
/// </summary>
/// <remarks>
/// <c>SpaceExpressionsGrain</c> drops an item's entry when it renames or deletes it; another silo's
/// in-memory copy lives out <see cref="LocalExpiration"/>.
/// </remarks>
public static class ExpressionItemCache
{
    private const int CacheConcurrency = 16;

    public static readonly TimeSpan LocalExpiration = TimeSpan.FromMinutes(1);

    public static readonly HybridCacheEntryOptions LiveOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(10),
        LocalCacheExpiration = LocalExpiration
    };

    public static readonly HybridCacheEntryOptions AbsentOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(1),
        LocalCacheExpiration = LocalExpiration
    };

    // No factory: a miss comes back as null and is loaded with the others in one query.
    private static readonly HybridCacheEntryOptions LookupOptions = new()
    {
        LocalCacheExpiration = LocalExpiration,
        Flags                = HybridCacheEntryFlags.DisableUnderlyingData
    };

    private static readonly Entry Absent = new(null);

    public static string Key(Guid itemId) => $"expr:item:{itemId}";

    /// <summary>What is cached per id: the emoji, or null when the id names no live one.</summary>
    [ImmutableObject(true)]
    public sealed record Entry(StatusEmoji? Emoji);

    /// <summary>The live emoji among <paramref name="itemIds"/>; <paramref name="load"/> gets every miss at once.</summary>
    public static async Task<Dictionary<Guid, StatusEmoji>> ResolveAsync(HybridCache cache, IReadOnlyCollection<Guid> itemIds,
        Func<List<Guid>, Task<Dictionary<Guid, StatusEmoji>>> load)
    {
        var         found  = new Dictionary<Guid, StatusEmoji>(itemIds.Count);
        List<Guid>? misses = null;

        foreach (var chunk in itemIds.Distinct().Chunk(CacheConcurrency))
        {
            var hits = await Task.WhenAll(chunk.Select(id => cache.GetOrCreateAsync<Entry?>(Key(id),
                static _ => ValueTask.FromResult<Entry?>(null), LookupOptions).AsTask()));

            for (var i = 0; i < chunk.Length; i++)
            {
                if (hits[i] is not { } entry)
                    (misses ??= []).Add(chunk[i]);
                else if (entry.Emoji is { } emoji)
                    found[chunk[i]] = emoji;
            }
        }

        if (misses is null)
            return found;

        var loaded = await load(misses);

        foreach (var chunk in misses.Chunk(CacheConcurrency))
        {
            await Task.WhenAll(chunk.Select(id => loaded.TryGetValue(id, out var emoji)
                ? cache.SetAsync(Key(id), new Entry(emoji), LiveOptions).AsTask()
                : cache.SetAsync(Key(id), Absent, AbsentOptions).AsTask()));
        }

        foreach (var (id, emoji) in loaded)
            found[id] = emoji;

        return found;
    }

    public static ValueTask InvalidateAsync(HybridCache cache, IEnumerable<Guid> itemIds)
        => cache.RemoveAsync(itemIds.Select(Key));
}
