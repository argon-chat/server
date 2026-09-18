namespace Argon.Features.Cosmetics;

using System.Collections.Frozen;

/// <summary>
/// One asset slot a kind accepts, and the limits its upload is held to.
/// </summary>
public sealed record CosmeticAssetRequirement(
    CosmeticAssetSlot Slot,
    CosmeticAssetKind Kind,
    long MaxBytes,
    bool IsRequired);

/// <summary>
/// The built, immutable description of a cosmetic kind. Produced once per type by
/// <see cref="CosmeticKindRegistry"/>.
/// </summary>
public sealed record CosmeticKindDefinition
{
    public required Type                KindType        { get; init; }
    public required string              Key             { get; init; }
    public required string?             Description     { get; init; }
    public required RenderPrimitive     Primitive       { get; init; }
    public required CosmeticSurface     Surfaces        { get; init; }
    public required int                 Layer           { get; init; }
    public required StackingRule        Stacking        { get; init; }
    public required int                 MaxSlots        { get; init; }
    public required CosmeticScope       Scope           { get; init; }
    public required Type                PayloadType     { get; init; }
    public required CosmeticEntitlement Entitlement     { get; init; }
    public required LegacyCosmeticField LegacyField     { get; init; }

    /// <summary>
    /// True for a kind whose items exist only as options on another kind's axis. Not shown in the
    /// wardrobe and refused by equip: choosing one is done through the kind that offers the axis.
    /// </summary>
    public required bool                IsCompositional { get; init; }

    /// <summary>
    /// Set for a card on the profile board: what its wearer fills in, and how wide it may be.
    /// Null for everything that is simply worn.
    /// </summary>
    public required CosmeticBoardDefinition? Board { get; init; }

    /// <summary>
    /// What this kind's wearer may fill in, whether it is a card's content or a thing's tuning.
    /// Null for a kind worn exactly as the catalogue authored it.
    /// </summary>
    public required Type? AuthoredType { get; init; }

    /// <summary>
    /// Whether this kind has no catalogue rows and is configured rather than chosen.
    /// </summary>
    public required bool IsBare { get; init; }
    public required string              FeatureFlagKey  { get; init; }

    public required FrozenDictionary<CosmeticAssetSlot, CosmeticAssetRequirement> Assets { get; init; }

    /// <summary>
    /// The axes a wearer composes, keyed by axis id. Empty for a kind worn exactly as authored.
    /// </summary>
    public required FrozenDictionary<string, CosmeticFacetDefinition> Facets { get; init; }

    public bool RendersOn(CosmeticSurface surface) => (Surfaces & surface) != 0;

    public bool SupportsScope(CosmeticScope scope) => (Scope & scope) != 0;

    public CosmeticPayloadValidation ValidatePayload(string? payloadJson)
        => CosmeticPayloadValidator.Validate(PayloadType, payloadJson);

    public CosmeticPayloadValidation ValidateContent(string? contentJson)
        => CosmeticContent.Validate(this, contentJson);



    /// <summary>
    /// Asset slots that must be filled before an item of this kind may be published.
    /// </summary>
    public IEnumerable<CosmeticAssetRequirement> RequiredAssets()
    {
        foreach (var requirement in Assets.Values)
        {
            if (requirement.IsRequired)
                yield return requirement;
        }
    }
}
