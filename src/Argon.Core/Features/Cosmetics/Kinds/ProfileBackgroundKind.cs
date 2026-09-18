namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

public sealed class ProfileBackgroundPayload
{
    /// <summary>Whether the clip restarts when it ends. Off means one pass and a held last frame.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>
    /// How strongly the card's own colour is laid over the clip, 0 for none and 1 for opaque. The
    /// card's text sits on top of this, so a background that ships a dark clip and no tint is how a
    /// profile becomes unreadable in light theme.
    /// </summary>
    [Range(0d, 1d)]
    public double TintOpacity { get; set; } = 0.35d;
}

/// <summary>
/// The full-bleed clip behind the profile card — the five bundled backgrounds today.
/// </summary>
public sealed class ProfileBackgroundKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("profile.background")
           .Describing("Full-bleed animated background behind the profile card")
           .Rendering(RenderPrimitive.VideoLayer)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(100)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<ProfileBackgroundPayload>()
           .RequiresAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.Video, 8 * 1024 * 1024)
           .AllowsAsset(CosmeticAssetSlot.Poster, CosmeticAssetKind.Image, 1024 * 1024)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned)
           .ProjectsToLegacy(LegacyCosmeticField.BackgroundId);
}
