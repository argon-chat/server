namespace Argon.Features.Cosmetics;

/// <summary>
/// Where a cosmetic is allowed to render. A kind names every surface it belongs on, and the client
/// asks for one surface at a time, so a kind that renders in two places is declared once here rather
/// than wired into two components.
/// </summary>
/// <remarks>
/// Bit positions are stable: a surface this build has no kind for is left out rather than renumbered,
/// so adding one later does not move the others.
/// </remarks>
[Flags]
public enum CosmeticSurface
{
    None               = 0,
    ProfileCard        = 1 << 0,
    OwnProfile         = 1 << 1,
    Avatar             = 1 << 2,
    NicknameInMessages = 1 << 3,
    MemberListRow      = 1 << 4
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
    /// <summary>A picture fitted inside the thing it decorates, which is what an avatar's ornament is.</summary>
    ImageLayer,

    /// <summary>
    /// A frame around a profile card, assembled out of parts an operator arranged with numbers.
    /// </summary>
    /// <remarks>
    /// No other member can express a list of parts: a frame that surrounds a card, or lies along its
    /// top alone, or hangs a character over its edge, is several pieces with an order.
    /// </remarks>
    FrameAssembly,

    /// <summary>The font, colour and motion of written text.</summary>
    TextStyle
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
/// The media a kind's asset slot accepts. Enforced at upload, before the file is ever referenced.
/// </summary>
public enum CosmeticAssetKind
{
    Image,
    Font
}

/// <summary>
/// The named asset slots an item may carry. The name is the key in
/// <c>CosmeticItemEntity.AssetFileIds</c>, so it is part of stored data — rename with a migration.
/// </summary>
/// <remarks>
/// Spelled one way everywhere: <see cref="CosmeticAssetSlots.KeyOf"/> is both the key in that map
/// and the name a payload names a slot by. A payload asking for <c>primary</c> against a map keyed
/// <c>Primary</c> is a frame that quietly draws nothing.
/// </remarks>
public enum CosmeticAssetSlot
{
    Primary,

    /// <summary>
    /// The second, third and fourth files of a kind built out of several.
    /// </summary>
    /// <remarks>
    /// <para>Ordinal rather than named after a role, because the role belongs to the payload: a
    /// frame's payload says which slot is the band around the card and which is the character
    /// sitting on it, and a slot called <c>Surround</c> would be one kind's vocabulary in an enum
    /// every kind shares.</para>
    ///
    /// <para>Four in total is what the shapes we have asked for need — a band plus three pieces hung
    /// off it. Adding more later is free; taking one away is a migration, because the member name is
    /// the key in <c>CosmeticItemEntity.AssetFileIds</c>.</para>
    /// </remarks>
    Secondary,
    Tertiary,
    Quaternary
}

/// <summary>
/// How a slot is spelled, in the payload that names it and in the map that holds its file.
/// </summary>
public static class CosmeticAssetSlots
{
    public static string KeyOf(CosmeticAssetSlot slot) => slot switch
    {
        CosmeticAssetSlot.Primary    => "primary",
        CosmeticAssetSlot.Secondary  => "secondary",
        CosmeticAssetSlot.Tertiary   => "tertiary",
        CosmeticAssetSlot.Quaternary => "quaternary",
        _                            => throw new ArgumentOutOfRangeException(nameof(slot))
    };
}
