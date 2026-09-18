namespace Argon.Features.Cosmetics;

using Argon.Core.Entities.Data;
using Argon.Entities;
using ion.runtime;

/// <summary>
/// Turning an equipped row and its axis choices into what a client renders.
/// </summary>
/// <remarks>
/// Shared between the projection, which answers for other people, and the grain, which answers for
/// the wearer. Two readers with one difference: the projection serves only what will actually draw,
/// while the wearer's own wardrobe shows what they picked whether or not it is currently theirs —
/// so an option their subscription used to cover reads as locked rather than as never chosen.
/// </remarks>
public static class CosmeticComposition
{
    public static List<EquippedCosmeticAsset> AssetsOf(CosmeticItemEntity item)
    {
        var assets = new List<EquippedCosmeticAsset>(item.AssetFileIds.Count);

        foreach (var (slot, fileId) in item.AssetFileIds)
        {
            assets.Add(new EquippedCosmeticAsset(slot, fileId));
        }

        return assets;
    }

    /// <summary>
    /// The options chosen on one equipped row's axes.
    /// </summary>
    /// <param name="admissible">
    /// Applied to each option row before it is included. Null includes whatever was chosen.
    /// </param>
    public static List<EquippedCosmeticOption> Compose(
        CosmeticEquipEntity equip,
        CosmeticKindDefinition kind,
        CosmeticKindRegistry registry,
        IReadOnlyDictionary<(string KindKey, string Slug), CosmeticItemEntity> rows,
        Func<CosmeticKindDefinition, CosmeticItemEntity, bool>? admissible = null)
    {
        if (kind.Facets.Count is 0 || string.IsNullOrWhiteSpace(equip.Overrides))
            return [];

        if (!CosmeticChoices.TryParse(equip.Overrides, out var choices, out _))
            return [];

        var composed = new List<EquippedCosmeticOption>(choices.Count);

        foreach (var (facetId, slug) in choices)
        {
            if (slug == CosmeticChoices.None || !kind.Facets.TryGetValue(facetId, out var facet))
                continue;

            if (!registry.TryGet(facet.OptionKindKey, out var optionKind))
                continue;

            if (!rows.TryGetValue((facet.OptionKindKey, slug), out var option))
                continue;

            if (admissible is not null && !admissible(optionKind, option))
                continue;

            composed.Add(new EquippedCosmeticOption(
                facetId,
                option.KindKey,
                option.Id,
                option.Slug,
                option.Payload,
                new IonArray<EquippedCosmeticAsset>(AssetsOf(option)),

                // As on the worn row: an option is a catalogue row too, so re-authoring a colour has
                // to reach the people already wearing it, and this is what tells them it moved.
                option.Version));
        }

        return composed;
    }

    /// <summary>
    /// Every option row any of these equips might refer to, in one query.
    /// </summary>
    /// <remarks>
    /// Per-equip lookups would be a query per name on screen, which is the one thing a member list
    /// cannot afford. The filter is by kind and by slug separately rather than by exact pair — a
    /// wider net a database can index, narrowed again in memory.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<(string KindKey, string Slug), CosmeticItemEntity>> ResolveRowsAsync(
        ApplicationDbContext db,
        CosmeticKindRegistry registry,
        IEnumerable<CosmeticEquipEntity> equips,
        CancellationToken ct)
    {
        var wanted = new HashSet<(string KindKey, string Slug)>();

        foreach (var equip in equips)
        {
            if (string.IsNullOrWhiteSpace(equip.Overrides))
                continue;

            if (!registry.TryGet(equip.KindKey, out var kind) || kind.Facets.Count is 0)
                continue;

            if (!CosmeticChoices.TryParse(equip.Overrides, out var choices, out _))
                continue;

            foreach (var (facetId, slug) in choices)
            {
                if (slug != CosmeticChoices.None && kind.Facets.TryGetValue(facetId, out var facet))
                    wanted.Add((facet.OptionKindKey, slug));
            }
        }

        if (wanted.Count is 0)
            return new Dictionary<(string, string), CosmeticItemEntity>();

        var kindKeys = wanted.Select(x => x.KindKey).Distinct().ToList();
        var slugs    = wanted.Select(x => x.Slug).Distinct().ToList();

        var rows = await db.Cosmetics
           .Where(item => kindKeys.Contains(item.KindKey) && slugs.Contains(item.Slug))
           .ToListAsync(ct);

        var resolved = new Dictionary<(string KindKey, string Slug), CosmeticItemEntity>(rows.Count);

        foreach (var row in rows)
        {
            if (wanted.Contains((row.KindKey, row.Slug)))
                resolved[(row.KindKey, row.Slug)] = row;
        }

        return resolved;
    }
}
