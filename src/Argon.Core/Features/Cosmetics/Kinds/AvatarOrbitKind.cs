namespace Argon.Features.Cosmetics.Kinds;

/// <summary>
/// Figures that travel a ring around an avatar, passing behind the face and back in front of it.
/// </summary>
/// <remarks>
/// <para>Stacks with <see cref="AvatarDecorationKind"/> rather than replacing it: a ring of thorns
/// around a face and a cat running round it are different things and people will wear both.</para>
///
/// <para>Every slot is a strip, and only the first is required. A satellite is always a picture —
/// unlike a frame, none of its parts can be painted from numbers — so a row with no file at all
/// would publish and draw nothing, which is the one refusal worth having here.</para>
/// </remarks>
public sealed class AvatarOrbitKind : ICosmeticKind
{
    /// <summary>
    /// The same ceiling a frame's parts get. A running figure is a strip of eight to sixteen
    /// drawings, and a strip that needs more than this is art for a screen rather than for a face.
    /// </summary>
    private const long MaxFigureBytes = 512 * 1024;

    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("avatar.orbit")
           .Describing("Figures travelling a ring around the avatar, hidden by the face as they pass behind it")
           .Rendering(RenderPrimitive.OrbitStage)
           .On(CosmeticSurface.Avatar)
           .Layer(250)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<AvatarOrbitPayload>()
           .RequiresAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.SpriteSheet, MaxFigureBytes)
           .AllowsAsset(CosmeticAssetSlot.Secondary, CosmeticAssetKind.SpriteSheet, MaxFigureBytes)
           .AllowsAsset(CosmeticAssetSlot.Tertiary, CosmeticAssetKind.SpriteSheet, MaxFigureBytes)
           .AllowsAsset(CosmeticAssetSlot.Quaternary, CosmeticAssetKind.SpriteSheet, MaxFigureBytes)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
