namespace Argon.Grains;

using Argon.Api.Entities.Data;
using ConsoleContracts;
using ion.runtime;

/// <summary>What the console shows inside a box: the items its scenario names, read for a whole list at once.</summary>
internal static class AdminBoxContents
{
    /// <summary>The template id of every item the given scenarios refer to, in one query.</summary>
    public static async Task<Dictionary<Guid, string>> ReadAsync(ApplicationDbContext db, IEnumerable<ItemUseScenario?> scenarios,
        CancellationToken ct)
    {
        var ids = scenarios.SelectMany(Referenced).Distinct().ToList();

        if (ids.Count == 0)
            return [];

        return await db.Items
           .AsNoTracking()
           .Where(i => ids.Contains(i.Id))
           .Select(i => new { i.Id, i.TemplateId })
           .ToDictionaryAsync(i => i.Id, i => i.TemplateId, ct);
    }

    /// <summary>The contents of one box, from what <see cref="ReadAsync"/> found; empty for anything that is not a box.</summary>
    public static IonArray<BoxContentInfo> Of(ItemUseScenario? scenario, IReadOnlyDictionary<Guid, string> items)
    {
        var contents = Referenced(scenario)
           .Distinct()
           .Where(items.ContainsKey)
           .Select(id => new BoxContentInfo(id, items[id]))
           .ToList();

        return contents.Count > 0 ? new IonArray<BoxContentInfo>(contents) : IonArray<BoxContentInfo>.Empty;
    }

    private static IEnumerable<Guid> Referenced(ItemUseScenario? scenario) => scenario switch
    {
        QualifierBox qb when qb.ReferenceItemId != Guid.Empty => [qb.ReferenceItemId],
        MultipleQualifierBox mqb                              => mqb.ReferenceItemIds,
        _                                                     => []
    };
}
