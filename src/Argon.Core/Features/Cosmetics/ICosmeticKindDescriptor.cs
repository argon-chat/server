namespace Argon.Features.Cosmetics;

/// <summary>
/// Fluent declaration of what a cosmetic kind is, where it renders, who may wear it, and what a
/// catalogue row of it has to carry.
/// </summary>
public interface ICosmeticKindDescriptor
{
    /// <summary>
    /// Stable identity, dotted and lowercase — <c>profile.frame</c>. Stored on every catalogue and
    /// equipped row, so changing it is a data migration, not a rename.
    /// </summary>
    ICosmeticKindDescriptor Keyed(string key);

    ICosmeticKindDescriptor Describing(string description);

    /// <summary>The client renderer this kind's payload is written for. Required.</summary>
    ICosmeticKindDescriptor Rendering(RenderPrimitive primitive);

    /// <summary>Every surface this kind may appear on. Required, and must name at least one.</summary>
    ICosmeticKindDescriptor On(CosmeticSurface surfaces);

    /// <summary>
    /// Compositing order within a surface, low to high. Kinds that can overlap must not share a
    /// layer, because a tie has no stable answer once two of them are on screen.
    /// </summary>
    ICosmeticKindDescriptor Layer(int layer);

    ICosmeticKindDescriptor Stacking(StackingRule rule);

    /// <summary>
    /// Upper bound on equipped slots for <see cref="StackingRule.Ordered"/>. Ignored, and rejected
    /// at build, for <see cref="StackingRule.Single"/>.
    /// </summary>
    ICosmeticKindDescriptor MaxSlots(int maxSlots);

    ICosmeticKindDescriptor Scoped(CosmeticScope scope);

    /// <summary>
    /// The type a catalogue row's payload deserializes into. Its <c>required</c> members and data
    /// annotations are the schema; see <see cref="CosmeticPayloadSchema"/>.
    /// </summary>
    /// <remarks>
    /// Required for every kind that has rows, and refused for a <see cref="Bare"/> one, which has
    /// none for a payload to sit on.
    /// </remarks>
    ICosmeticKindDescriptor Payload<TPayload>() where TPayload : class;

    /// <summary>
    /// An asset the item must carry before it can be published, and the limits its upload is held to.
    /// </summary>
    ICosmeticKindDescriptor RequiresAsset(CosmeticAssetSlot slot, CosmeticAssetKind kind, long maxBytes);

    /// <summary>
    /// An asset the item may carry. Not checked at publish.
    /// </summary>
    ICosmeticKindDescriptor AllowsAsset(CosmeticAssetSlot slot, CosmeticAssetKind kind, long maxBytes);

    /// <summary>
    /// Declares a schema this kind's wearer fills in.
    /// </summary>
    /// <remarks>
    /// <para>A catalogue row says what a thing <i>is</i>; some things also have a part only the
    /// person wearing them can decide — the colours of their own name, say — and that part is
    /// neither a pick from a list nor something an operator can author on their behalf.</para>
    /// </remarks>
    ICosmeticKindDescriptor Tuning<TContent>() where TContent : class;

    /// <summary>
    /// Declares that this kind has no catalogue rows: it is configured rather than chosen.
    /// </summary>
    /// <remarks>
    /// <para>For a kind whose whole appearance comes from its axes and its wearer's tuning. A row
    /// for one of those would carry nothing — no payload anybody sees, no asset, nothing to tell two
    /// of them apart — so offering a choice between rows is offering a choice that does not exist.
    /// </para>
    ///
    /// <para>Entitlement goes with it: what is owned is the face, the treatment and the colour, each
    /// priced and gated on its own row. There is nothing left on this kind to sell.</para>
    /// </remarks>
    ICosmeticKindDescriptor Bare();

    ICosmeticKindDescriptor Entitlement(CosmeticEntitlement entitlement);

    /// <summary>
    /// An axis the wearer composes, and the kind whose items are its options.
    /// </summary>
    /// <remarks>
    /// The axis lists nothing. Which faces or colours exist is whatever rows that kind has, so
    /// offering one more is a row created from the admin console rather than a release.
    /// </remarks>
    ICosmeticKindDescriptor Facet(string facetId, string optionKindKey);

    /// <summary>
    /// Marks a kind whose items are only ever options on somebody else's axis, never worn on their
    /// own. Such a kind is kept out of the wardrobe and refused by equip.
    /// </summary>
    ICosmeticKindDescriptor Compositional();

    /// <summary>
    /// Overrides the feature flag that gates this kind. Defaults to
    /// <c>af.cosmetics.&lt;kebab-key&gt;.active</c>, which is what a new kind should use.
    /// </summary>
    ICosmeticKindDescriptor FeatureFlag(string flagKey);
}
