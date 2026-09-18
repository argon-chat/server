namespace Argon.Features.Cosmetics;

/// <summary>
/// Where a cosmetic is allowed to render. A kind names every surface it belongs on, and the client
/// asks for one surface at a time, so a kind that renders in two places is declared once here rather
/// than wired into two components.
/// </summary>
[Flags]
public enum CosmeticSurface
{
    None               = 0,
    ProfileCard        = 1 << 0,
    OwnProfile         = 1 << 1,
    Avatar             = 1 << 2,
    NicknameInMessages = 1 << 3,
    MemberListRow      = 1 << 4,
    VoiceCard          = 1 << 5,
    Banner             = 1 << 6,
    Status             = 1 << 7
}

/// <summary>
/// The closed set of client renderers. A kind picks one; it does not ship a component.
/// </summary>
/// <remarks>
/// This set is what lets an operator publish a new cosmetic without a client release: the payload
/// changes, the renderer does not. Adding a member here is a client release by definition, so the
/// bar for a new primitive is that no existing one can express the thing at all — not that an
/// existing one would need a new payload field.
/// </remarks>
public enum RenderPrimitive
{
    ImageLayer,
    VideoLayer,
    SpriteSheet,

    /// <summary>
    /// A frame around a profile card, assembled out of parts an operator arranged with numbers.
    /// </summary>
    /// <remarks>
    /// <para>It replaced a primitive that drew one coloured ring, which is the only shape four
    /// numbers can describe. A frame that surrounds a card, or lies along its top alone, or hangs a
    /// character over its edge, is a list of parts — and no existing primitive can express a list of
    /// anything, which is the bar this set holds a new member to.</para>
    ///
    /// <para>Not <see cref="CardLayer"/>: that is one picture fitted to the card's own shape, so it
    /// cannot leave the card, cannot keep a corner undistorted while an edge stretches, and has
    /// nowhere to put a second piece.</para>
    /// </remarks>
    FrameAssembly,

    TextStyle,
    IconBadge,
    WidgetSlot,

    /// <summary>
    /// A picture laid over a whole profile card — a frame around it, or something moving across it.
    /// </summary>
    /// <remarks>
    /// Not <see cref="ImageLayer"/>, which is fitted inside the thing it decorates and is what an
    /// avatar's ornament is. A card layer is stretched or cropped to the card's own shape, and the
    /// surfaces that draw a card keep it in a different slot — over everything rather than under it.
    /// Appended, because a name on the wire is what a client matches on.
    /// </remarks>
    CardLayer,

    /// <summary>
    /// Bodies travelling a ring around an avatar, drawn with a near half and a far half.
    /// </summary>
    /// <remarks>
    /// <para>The thing no other member can express is <b>occlusion</b>. Every primitive above draws
    /// a layer, and a layer is on one side of what it decorates — there is no arrangement of an
    /// inset, an opacity and a z-order that puts half of a picture behind a head and the other half
    /// in front of it, because the picture is one layer and the head is one box.</para>
    ///
    /// <para>Not <see cref="FrameAssembly"/>, which is also a list of parts: its parts are pinned to
    /// the edges of a card and move a little way from where they were pinned. These have positions
    /// in three dimensions and the arithmetic is the primitive.</para>
    /// </remarks>
    OrbitStage,

    /// <summary>
    /// Actors moving across a whole profile card: a picture, a figure travelling a path, a band
    /// along one edge, or a scatter of one sprite.
    /// </summary>
    /// <remarks>
    /// <para><b>What no other member can express is a list of moving things with a depth.</b> A
    /// <see cref="CardLayer"/> is one picture fitted to the card and a <see cref="FrameAssembly"/> is
    /// a set of pieces pinned to its edges and standing still. Neither can put one figure in front of
    /// the card's own text and another behind its glass, and neither has anywhere to put a path.</para>
    ///
    /// <para>Unlike a frame, it is bounded by the card: a scene clips. A thing that both moves and
    /// leaves the card is a thing that moves over somebody else's messages.</para>
    /// </remarks>
    SceneStage
}

/// <summary>
/// Whether a kind may be equipped globally, per space, or either.
/// </summary>
[Flags]
public enum CosmeticScope
{
    Global   = 1 << 0,
    PerSpace = 1 << 1
}

/// <summary>
/// How many of one kind may be equipped in the same loadout.
/// </summary>
public enum StackingRule
{
    /// <summary>One, at slot 0. Equipping a second replaces the first.</summary>
    Single,

    /// <summary>Several, ordered by slot index and bounded by <c>ICosmeticKindDescriptor.MaxSlots</c>.</summary>
    Ordered
}

/// <summary>
/// What makes an item of this kind usable once it is in the catalogue.
/// </summary>
public enum CosmeticEntitlement
{
    /// <summary>Anyone may equip it; ownership is not consulted.</summary>
    Free,

    /// <summary>Only an owner may equip it — a grant, a promo code, a gift or a purchase.</summary>
    Owned,

    /// <summary>An owner, or anyone whose Ultima tier covers the item.</summary>
    UltimaOrOwned
}

/// <summary>
/// The pre-cosmetics field an equipped item projects onto for clients built before this system.
/// </summary>
/// <remarks>
/// A kind that names one here is readable by an old build; a kind that names <see cref="None"/> is
/// simply invisible to it, which is the correct outcome and not a failure. See
/// <c>CosmeticProfileProjection</c>.
/// </remarks>
public enum LegacyCosmeticField
{
    None,
    BackgroundId,
    VoiceCardEffectId,
    AvatarFrameId,
    NickEffectId,
    Badges
}

/// <summary>
/// The media a kind's asset slot accepts. Enforced at upload, before the file is ever referenced.
/// </summary>
public enum CosmeticAssetKind
{
    Image,
    Video,
    SpriteSheet,
    Font
}

/// <summary>
/// The named asset slots an item may carry. The name is the key in
/// <c>CosmeticItemEntity.AssetFileIds</c>, so it is part of stored data — rename with a migration.
/// </summary>
public enum CosmeticAssetSlot
{
    Primary,
    Poster,
    Small,

    /// <summary>
    /// The second, third and fourth files of a kind built out of several.
    /// </summary>
    /// <remarks>
    /// <para>Ordinal rather than named after a role, because the role belongs to the payload: a
    /// frame's payload says which slot is the band around the card and which is the character
    /// sitting on it, and a slot called <c>Surround</c> would be one kind's vocabulary in an enum
    /// every kind shares.</para>
    ///
    /// <para>Four in total is what the shapes we have asked for need — a band plus three pieces
    /// hung off it. Adding more later is free; taking one away is a migration, because the member
    /// name is the key in <c>CosmeticItemEntity.AssetFileIds</c>.</para>
    /// </remarks>
    Secondary,
    Tertiary,
    Quaternary
}
