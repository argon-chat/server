namespace Argon.Features.Cosmetics;

using Argon.Entities;
using Argon.Features.Cosmetics.Kinds;
using ion.runtime;

/// <summary>
/// What a person is wearing, as the wire carries it: their worn rows, checked against the catalogue
/// and reduced to typed references.
/// </summary>
/// <remarks>
/// <para><b>References, never content.</b> A profile is read by the hundred, so what goes into one is
/// the catalogue rows by id — the payloads, files and names are the catalogue's, read once per
/// session. The one kind whose look the wearer composes carries its choices as typed fields; see
/// <see cref="WornCosmetic"/>.</para>
///
/// <para><b>No database here.</b> The rows come in already read, along with every catalogue row they
/// name that may be served right now. Kept apart from the read so the rules can be tested without a
/// container, and so one batch read serves a whole member list.</para>
///
/// <para><b>Anything that no longer resolves is dropped, never shown half-way.</b> A kind whose file
/// was deleted, a kind switched off, an item unpublished or out of its window: each leaves the worn row
/// where it is and out of the answer, so turning the thing back on restores what people had on. An
/// option that no longer resolves drops only that axis — a name keeps its colour when its font goes.</para>
/// </remarks>
public static class CosmeticWornProjection
{
    public static IonArray<IWornCosmetic> Project(
        IEnumerable<CosmeticEquipEntity> worn,
        IReadOnlyDictionary<Guid, CosmeticItemEntity> servable,
        CosmeticKindRegistry registry,
        IReadOnlySet<string> enabledKinds)
    {
        var projected = new List<(int Layer, int Slot, IWornCosmetic Cosmetic)>();

        foreach (var row in worn)
        {
            if (!registry.TryGet(row.KindKey, out var kind) || !enabledKinds.Contains(row.KindKey))
                continue;

            if (Resolve(row, kind, servable) is { } cosmetic)
                projected.Add((kind.Layer, row.SlotIndex, cosmetic));
        }

        projected.Sort(static (a, b) => a.Layer != b.Layer ? a.Layer.CompareTo(b.Layer) : a.Slot.CompareTo(b.Slot));

        return new IonArray<IWornCosmetic>(projected.Select(entry => entry.Cosmetic).ToList());
    }

    /// <summary>Every catalogue row a person's worn rows name: the items, and the options they chose.</summary>
    public static IEnumerable<Guid> Named(CosmeticEquipEntity row)
    {
        if (row.CosmeticItemId is { } itemId)
            yield return itemId;

        foreach (var optionId in ChoicesOf(row).Values)
        {
            yield return optionId;
        }
    }

    /// <summary>What was chosen on a row's axes, axis id to option row. Empty when nothing was.</summary>
    public static Dictionary<string, Guid> ChoicesOf(CosmeticEquipEntity row)
    {
        var choices = new Dictionary<string, Guid>();

        if (!CosmeticChoices.TryParse(row.Choices, out var stored, out _))
            return choices;

        foreach (var (axis, value) in stored)
        {
            if (Guid.TryParse(value, out var optionId))
                choices[axis] = optionId;
        }

        return choices;
    }

    private static IWornCosmetic? Resolve(
        CosmeticEquipEntity row,
        CosmeticKindDefinition kind,
        IReadOnlyDictionary<Guid, CosmeticItemEntity> servable)
    {
        if (!kind.IsBare)
        {
            return row.CosmeticItemId is { } itemId && servable.ContainsKey(itemId)
                ? new WornItem(itemId)
                : null;
        }

        // A bare kind has no row to point at. One that does was written by something other than this
        // build, and it is not drawn rather than drawn as a guess.
        if (row.CosmeticItemId is not null)
            return null;

        var options = new Dictionary<string, Guid>();

        foreach (var (axis, optionId) in ChoicesOf(row))
        {
            // Held to the axis it was chosen on as well as to the catalogue: an option row that has
            // since become some other kind's is not this axis's option any more.
            if (kind.Facets.TryGetValue(axis, out var facet)
                && servable.TryGetValue(optionId, out var option)
                && option.KindKey == facet.OptionKindKey)
            {
                options[axis] = optionId;
            }
        }

        return kind.Key switch
        {
            NicknameStyleKind.Key => NicknameStyleKind.ToWire(options, TuningOf<NicknameStyleTuning>(row)),

            // A composable kind this build declares but the wire has no case for yet: nothing to send.
            _ => null
        };
    }

    private static T? TuningOf<T>(CosmeticEquipEntity row) where T : class
        => row.Tuning is { } json && CosmeticJson.Default.TryRead<T>(json, out var tuning, out _) ? tuning : null;
}
