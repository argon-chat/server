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
    HybridCache cache) : Grain, ICosmeticsReadGrain
{
    /// <summary>
    /// Asks the cache without ever running a query or writing anything back, so a miss comes back as
    /// null and the caller can gather every miss into one read.
    /// </summary>
    private static readonly HybridCacheEntryOptions Probe = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData
              | HybridCacheEntryFlags.DisableLocalCacheWrite
              | HybridCacheEntryFlags.DisableDistributedCacheWrite
    };

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

    public async Task<Dictionary<Guid, IonArray<IWornCosmetic>>> GetWornAsync(List<Guid> userIds)
    {
        var distinct = userIds.Distinct().ToList();

        // Probed side by side: a member list whose entries have fallen out of this silo's memory but
        // are still in Redis would otherwise be one Redis round trip per member, one after another.
        var probes = await Task.WhenAll(distinct.Select(async userId => (UserId: userId,
            Hit: await cache.GetOrCreateAsync<byte[]?>(ICosmeticsCache.WornKey(userId),
                static _ => ValueTask.FromResult<byte[]?>(null), Probe))));

        var worn   = new Dictionary<Guid, IonArray<IWornCosmetic>>(distinct.Count);
        var misses = new List<Guid>();

        foreach (var (userId, hit) in probes)
        {
            if (hit is null)
                misses.Add(userId);
            else
                worn[userId] = WornCosmeticsWire.Read(hit);
        }

        if (misses.Count is 0)
            return worn;

        var loaded = await LoadWornAsync(misses);

        foreach (var (userId, answer) in loaded)
        {
            worn[userId] = answer;
        }

        await Task.WhenAll(loaded.Select(pair => cache.SetAsync(ICosmeticsCache.WornKey(pair.Key),
            WornCosmeticsWire.Write(pair.Value), ICosmeticsCache.WornOptions,
            [ICosmeticsCache.AllTag, ICosmeticsCache.WornTag(pair.Key)]).AsTask()));

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
