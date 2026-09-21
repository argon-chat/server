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
/// <remarks>
/// <para><b>No <see cref="Type"/> anywhere on it.</b> A kind's schema arrives as
/// <see cref="CosmeticPayloadSchema"/>, which is a function closed over the type at the one place
/// that named it. Everything downstream — the registry, the read path, the console — asks whether a
/// string fits, and none of them has any business holding the runtime type to ask it.</para>
/// </remarks>
public sealed record CosmeticKindDefinition
{
    public required string Key { get; init; }

    /// <summary>The type that declared it, by name. For diagnostics, and never resolved back.</summary>
    public required string DeclaredBy { get; init; }

    public required string?             Description { get; init; }
    public required RenderPrimitive     Primitive   { get; init; }
    public required CosmeticSurface     Surfaces    { get; init; }
    public required int                 Layer       { get; init; }
    public required StackingRule        Stacking    { get; init; }
    public required int                 MaxSlots    { get; init; }
    public required CosmeticScope       Scope       { get; init; }
    public required CosmeticEntitlement Entitlement { get; init; }

    /// <summary>What a catalogue row of this kind carries. Null for a <see cref="IsBare"/> kind.</summary>
    public required CosmeticPayloadSchema? Payload { get; init; }

    /// <summary>What this kind's wearer fills in. Null for a kind worn exactly as authored.</summary>
    public required CosmeticPayloadSchema? Tuning { get; init; }

    /// <summary>
    /// True for a kind whose items exist only as options on another kind's axis. Not shown in the
    /// wardrobe and refused by equip: choosing one is done through the kind that offers the axis.
    /// </summary>
    public required bool IsCompositional { get; init; }

    /// <summary>
    /// Whether this kind has no catalogue rows and is configured rather than chosen.
    /// </summary>
    public required bool IsBare { get; init; }

    public required string FeatureFlagKey { get; init; }

    public required FrozenDictionary<CosmeticAssetSlot, CosmeticAssetRequirement> Assets { get; init; }

    /// <summary>
    /// The axes a wearer composes, keyed by axis id. Empty for a kind worn exactly as authored.
    /// </summary>
    public required FrozenDictionary<string, CosmeticFacetDefinition> Facets { get; init; }

    public bool RendersOn(CosmeticSurface surface) => (Surfaces & surface) != 0;

    public bool SupportsScope(CosmeticScope scope) => (Scope & scope) != 0;

    public CosmeticPayloadValidation ValidatePayload(string? payloadJson, ICosmeticJsonCodec? codec = null)
        => Payload is null
            ? CosmeticPayloadValidation.Invalid($"'{Key}' has no catalogue rows, so it has no payload")
            : Payload.Validate(payloadJson, codec);

    public CosmeticPayloadValidation ValidateTuning(string? tuningJson, ICosmeticJsonCodec? codec = null)
        => Tuning is null
            ? CosmeticPayloadValidation.Invalid($"'{Key}' is worn as it was authored, so it takes no tuning")
            : Tuning.Validate(tuningJson, codec);

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

    /// <summary>
    /// Whether a kind is switched on, stated once so the console and the read path cannot disagree.
    /// </summary>
    /// <remarks>
    /// <para><b>A kind is on unless a flag row exists and says otherwise.</b> The feature-flag grain
    /// evaluates a flag nobody created as <i>disabled</i>, which is right for an experiment and
    /// wrong here: a kind exists because a file in the build declares it, and requiring somebody to
    /// also create a database row before shipped code does anything is the failure mode where a
    /// feature is live, correct, and invisible — with nothing in any log to say why.</para>
    ///
    /// <para>So the flag is a kill switch rather than an enabler. Turning a kind off creates the row
    /// the first time, and from then on the row is the answer.</para>
    /// </remarks>
    public static bool IsEnabled(bool flagExists, bool flagSaysEnabled) => !flagExists || flagSaysEnabled;
}
