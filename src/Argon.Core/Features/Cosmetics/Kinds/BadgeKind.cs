namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

public sealed class BadgePayload
{
    /// <summary>
    /// Translation key for the tooltip. A key rather than a string because a badge outlives the
    /// language it was created in — the three badges this replaces are hardcoded English today.
    /// </summary>
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public string TooltipKey { get; set; } = null!;

    /// <summary>Optional ARGB tint applied to a monochrome icon. Null leaves the asset as authored.</summary>
    public int? Tint { get; set; }
}

/// <summary>
/// The small icons under a display name. Ordered, because the row is read left to right and the
/// order is the person's, not the catalogue's.
/// </summary>
public sealed class BadgeKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("profile.badge")
           .Describing("Icon shown beside the display name, with a tooltip")
           .Rendering(RenderPrimitive.IconBadge)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(300)
           .Stacking(StackingRule.Ordered)
           .MaxSlots(8)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<BadgePayload>()
           .RequiresAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.Image, 256 * 1024)
           .Entitlement(CosmeticEntitlement.Owned)
           .ProjectsToLegacy(LegacyCosmeticField.Badges);
}
