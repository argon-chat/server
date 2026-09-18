namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

public sealed class AvatarDecorationPayload
{
    /// <summary>
    /// How far the avatar is inset inside the decoration, as a percentage of its own size. A frame
    /// that draws a ring needs the avatar pulled in; an overlay that sits on top needs zero.
    /// </summary>
    [Range(0d, 40d)]
    public double InsetPct { get; set; }

    /// <summary>Whether the decoration is drawn under the avatar rather than over it.</summary>
    public bool Beneath { get; set; }
}

/// <summary>
/// The frame or ornament around an avatar, rendered wherever an avatar is.
/// </summary>
public sealed class AvatarDecorationKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("avatar.decoration")
           .Describing("Frame or ornament composited with the avatar")
           .Rendering(RenderPrimitive.ImageLayer)
           .On(CosmeticSurface.Avatar)
           .Layer(200)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<AvatarDecorationPayload>()
           .RequiresAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.Image, 2 * 1024 * 1024)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned)
           .ProjectsToLegacy(LegacyCosmeticField.AvatarFrameId);
}
