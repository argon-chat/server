namespace Argon.Grains;

using Argon.Entities;
using Argon.Features.Cosmetics;
using Argon.Core.Entities.Data;
using Grains.Interfaces;
using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using Orleans.Concurrency;
using Services.L1L2;

/// <summary>
/// Reads about cosmetics, answered by a pool of activations over the L1/L2 cache. See
/// <see cref="ICosmeticsReadGrain"/> for why it is its own grain.
/// </summary>
[StatelessWorker]
public sealed class CosmeticsReadGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    CosmeticKindRegistry registry,
    HybridCache cache,
    WornDropLedger drops) : Grain, ICosmeticsReadGrain
{
    /// <summary>How many cache round trips one read keeps in flight at once.</summary>
    /// <remarks>
    /// Bounded both ways. A member list is hundreds of people, and every one of them missing from
    /// memory is a trip to Redis: all at once is a burst on the connection every other caller shares,
    /// and one at a time is hundreds of round trips back to back. Sixteen, as <c>UserGrain</c>'s
    /// fan-outs are.
    /// </remarks>
    private const int CacheConcurrency = 16;

    /// <remarks>
    /// Cached as its Ion encoding, like what people wear: its payloads are a union, which the cache's
    /// JSON serializer can write but not read back.
    /// </remarks>
    public async Task<CosmeticCatalogue> GetCatalogueAsync()
    {
        var bytes = await cache.GetOrCreateAsync(ICosmeticsCache.CatalogueKey,
            this,
            static async (self, ct) =>
            {
                var cbor = new System.Formats.Cbor.CborWriter();
                IonFormatterStorage<CosmeticCatalogue>.Write(cbor, await self.LoadCatalogueAsync(ct));
                return cbor.Encode();
            },
            ICosmeticsCache.CatalogueOptions,
            [ICosmeticsCache.AllTag]);

        return IonFormatterStorage<CosmeticCatalogue>.Read(new System.Formats.Cbor.CborReader(bytes));
    }

    /// <remarks>
    /// <para>Looked up one by one and loaded all together, because <c>HybridCache</c> has no batch
    /// read and a factory per person would be a query per person on a cold member list.</para>
    ///
    /// <para>Written back only for the people nobody dropped while the query ran, as
    /// <see cref="WornDropLedger"/> tells it — see <see cref="ICosmeticsCache"/> for the race that
    /// guards against. Whoever was dropped is still answered from the rows just read: the change's own
    /// announcement follows with the new look, and nothing stale is left behind for anybody else.</para>
    /// </remarks>
    public async Task<Dictionary<Guid, IonArray<IWornCosmetic>>> GetWornAsync(List<Guid> userIds)
    {
        var distinct = userIds.Distinct().ToList();
        var worn     = new Dictionary<Guid, IonArray<IWornCosmetic>>(distinct.Count);
        var misses   = new List<Guid>();

        foreach (var chunk in distinct.Chunk(CacheConcurrency))
        {
            var found = await Task.WhenAll(chunk.Select(async userId => (UserId: userId,
                Hit: await cache.GetOrCreateAsync<byte[]?>(ICosmeticsCache.WornKey(userId),
                    static _ => ValueTask.FromResult<byte[]?>(null), ICosmeticsCache.WornLookupOptions,
                    ICosmeticsCache.WornTags(userId)))));

            foreach (var (userId, hit) in found)
            {
                if (hit is null)
                    misses.Add(userId);
                else
                    worn[userId] = WornCosmeticsWire.Read(hit);
            }
        }

        if (misses.Count is 0)
            return worn;

        var before = misses.ToDictionary(userId => userId, drops.Of);
        var loaded = await LoadWornAsync(misses);

        foreach (var chunk in loaded.Chunk(CacheConcurrency))
        {
            await Task.WhenAll(chunk
               .Where(pair => drops.Of(pair.Key) == before[pair.Key])
               .Select(pair => cache.SetAsync(ICosmeticsCache.WornKey(pair.Key),
                    WornCosmeticsWire.Write(pair.Value), ICosmeticsCache.WornOptions,
                    ICosmeticsCache.WornTags(pair.Key)).AsTask()));
        }

        foreach (var (userId, answer) in loaded)
        {
            worn[userId] = answer;
        }

        return worn;
    }

    public async Task<HashSet<string>> GetEnabledKindsAsync()
    {
        var flags = grainFactory.GetGrain<IFeatureFlagGrain>(Guid.Empty);

        // A flag nobody created evaluates as disabled, which is right for an experiment and wrong for a
        // kind — so existence is asked separately, and only an existing flag can switch a kind off.
        var existing = (await flags.ListFlagsAsync()).Select(flag => flag.Id).ToHashSet();
        var gated    = registry.All.Where(kind => existing.Contains(kind.FeatureFlagKey)).ToList();

        var evaluated = gated.Count is 0
            ? []
            : await flags.EvaluateManyAsync(gated.Select(kind => kind.FeatureFlagKey).ToList(),
                FeatureFlagEvaluationContext.Empty);

        var enabled = new HashSet<string>(registry.All.Count);

        foreach (var kind in registry.All)
        {
            var exists = existing.Contains(kind.FeatureFlagKey);
            var says   = exists && evaluated.TryGetValue(kind.FeatureFlagKey, out var result) && result.IsEnabled;

            if (CosmeticKindDefinition.IsEnabled(exists, says))
                enabled.Add(kind.Key);
        }

        return enabled;
    }

    private async Task<CosmeticCatalogue> LoadCatalogueAsync(CancellationToken ct)
    {
        var enabled = await GetEnabledKindsAsync();
        var keys    = enabled.ToList();
        var now     = DateTimeOffset.UtcNow;

        await using var ctx = await context.CreateDbContextAsync(ct);

        var items = await ctx.Cosmetics
           .AsNoTracking()
           .Where(CosmeticAvailability.ServableAt(now))
           .Where(item => keys.Contains(item.KindKey))
           .OrderBy(item => item.KindKey)
           .ThenBy(item => item.SortOrder)
           .ToListAsync(ct);

        var ids = items.Select(item => item.Id).ToList();

        var texts = ids.Count is 0
            ? []
            : await ctx.CosmeticTranslations
               .AsNoTracking()
               .Where(text => ids.Contains(text.CosmeticItemId))
               .ToListAsync(ct);

        var textByItem = texts.ToLookup(text => text.CosmeticItemId);

        var catalogue = new List<CatalogueCosmetic>(items.Count);

        foreach (var item in items)
        {
            // A row whose kind this build does not declare is an orphan: never served. Nor is one whose
            // stored document does not fit its kind — there is nothing typed to send for it.
            if (!registry.TryGet(item.KindKey, out var kind) || kind.PayloadToWire(item.Payload) is not { } payload)
                continue;

            catalogue.Add(new CatalogueCosmetic(
                item.Id,
                item.KindKey,
                item.Slug,
                item.NameKey,
                item.DescriptionKey,
                item.Rarity,
                item.Version,
                payload,
                AssetsOf(item),
                item.AvailableUntil?.UtcDateTime,
                CosmeticWear.IsFree(item, kind),
                CosmeticWear.IsCoveredByUltima(item, kind),
                new IonArray<CosmeticText>(textByItem[item.Id]
                   .Select(text => new CosmeticText(text.Locale, text.Name, text.Description))
                   .ToList())));
        }

        return new CosmeticCatalogue(new IonArray<string>(keys), new IonArray<CatalogueCosmetic>(catalogue));
    }

    /// <summary>
    /// Everything worn by these people, in two queries however many of them there are: the worn rows,
    /// and every catalogue row they name that may be served — the items and the options alike.
    /// </summary>
    private async Task<Dictionary<Guid, IonArray<IWornCosmetic>>> LoadWornAsync(List<Guid> userIds)
    {
        var enabled = await GetEnabledKindsAsync();
        var now     = DateTimeOffset.UtcNow;

        await using var ctx = await context.CreateDbContextAsync();

        var rows = await ctx.CosmeticEquips
           .AsNoTracking()
           .Where(equip => userIds.Contains(equip.UserId))
           .ToListAsync();

        var named = rows.SelectMany(CosmeticWornProjection.Named).Distinct().ToList();

        var servable = named.Count is 0
            ? new Dictionary<Guid, CosmeticItemEntity>()
            : await ctx.Cosmetics
               .AsNoTracking()
               .Where(CosmeticAvailability.ServableAt(now))
               .Where(item => named.Contains(item.Id))
               .ToDictionaryAsync(item => item.Id);

        var byUser = rows.ToLookup(row => row.UserId);
        var worn   = new Dictionary<Guid, IonArray<IWornCosmetic>>(userIds.Count);

        foreach (var userId in userIds)
        {
            worn[userId] = CosmeticWornProjection.Project(byUser[userId], servable, registry, enabled);
        }

        return worn;
    }

    private static IonArray<CosmeticAsset> AssetsOf(CosmeticItemEntity item)
    {
        if (item.AssetFileIds.Count is 0)
            return IonArray<CosmeticAsset>.Empty;

        var assets = new List<CosmeticAsset>(item.AssetFileIds.Count);

        foreach (var (key, fileId) in item.AssetFileIds)
        {
            // A key this build does not know is a file the kind no longer has a slot for.
            if (CosmeticAssetSlots.TryParse(key, out var slot))
                assets.Add(new CosmeticAsset(CosmeticAssetSlots.ToWire(slot), fileId));
        }

        return new IonArray<CosmeticAsset>(assets);
    }
}
