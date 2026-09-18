namespace Argon.Features.Cosmetics;

using Argon.Entities;

public enum CosmeticPublicationRefusal
{
    None,
    UnknownKind,
    AlreadyPublished,
    MissingAsset,
    PayloadInvalid,
    MissingName
}

public readonly record struct CosmeticPublicationVerdict(CosmeticPublicationRefusal Refusal, string? Detail)
{
    public bool IsAllowed => Refusal is CosmeticPublicationRefusal.None;

    public static CosmeticPublicationVerdict Allowed { get; } = new(CosmeticPublicationRefusal.None, null);

    public static CosmeticPublicationVerdict Refused(CosmeticPublicationRefusal refusal, string? detail = null)
        => new(refusal, detail);
}

/// <summary>
/// Whether a catalogue row may be published — the one gate between an operator's draft and every
/// client that would render it.
/// </summary>
/// <remarks>
/// <para>A pure function over the row and its kind, deliberately: the rule that decides what every
/// client is shown is worth being able to test on its own and to call from anywhere without a
/// database.</para>
///
/// <para>What it asks: that the kind is one this build declares, that the row is not published
/// already, that the payload validates against the kind's schema, and that every asset slot the kind
/// requires has a file behind it — the last of these only where the catalogue is what supplies the
/// asset, and that it has a name in the fallback language.</para>
/// </remarks>
public static class CosmeticPublication
{
    public static CosmeticPublicationVerdict Evaluate(
        CosmeticItemEntity item,
        CosmeticKindDefinition? kind,
        IReadOnlySet<string> namedLocales)
    {
        if (kind is null)
            return CosmeticPublicationVerdict.Refused(CosmeticPublicationRefusal.UnknownKind, item.KindKey);

        if (item.IsPublished)
            return CosmeticPublicationVerdict.Refused(CosmeticPublicationRefusal.AlreadyPublished);

        var payload = kind.ValidatePayload(item.Payload);

        if (!payload.IsValid)
            return CosmeticPublicationVerdict.Refused(CosmeticPublicationRefusal.PayloadInvalid, string.Join("; ", payload.Errors));

        // An item whose asset the wearer supplies has nothing for the catalogue to carry — a profile
        // banner is the wearer's own file — so the requirement does not apply to it.
        if (item.AssetSource is CosmeticAssetSource.Catalogue)
        {
            foreach (var requirement in kind.RequiredAssets())
            {
                if (!item.AssetFileIds.ContainsKey(requirement.Slot.ToString()))
                {
                    return CosmeticPublicationVerdict.Refused(CosmeticPublicationRefusal.MissingAsset,
                        $"slot {requirement.Slot} has no file");
                }
            }
        }

        // Last, because it is the cheapest to fix and the least interesting thing to be told first
        // when the payload is also wrong.
        if (!namedLocales.Contains(CosmeticLocale.Fallback))
        {
            return CosmeticPublicationVerdict.Refused(CosmeticPublicationRefusal.MissingName,
                $"no name in '{CosmeticLocale.Fallback}'");
        }

        return CosmeticPublicationVerdict.Allowed;
    }
}
