namespace Argon.Features.Cosmetics.Kinds;

/// <summary>
/// Things moving across somebody's whole profile card.
/// </summary>
/// <remarks>
/// <para>Its own kind rather than a richer <see cref="ProfileEffectKind"/>: an effect is one picture
/// laid on a card, and there is no number you can add to one picture that makes it two things
/// moving on different paths at different depths.</para>
///
/// <para><b>Its layer is only a sort order.</b> Where a scene is actually drawn is decided per
/// actor, in the payload — in front of a worn frame, over the card's own text, or behind its glass.
/// A kind-wide layer could give one answer, and the references need three at once.</para>
///
/// <para>The sheet is required and everything else is not. An actor is always a picture, so a row
/// with no file publishes and draws nothing, which is the one refusal worth having here — the same
/// line <see cref="AvatarOrbitKind"/> draws. A scene made of one painting still puts that painting
/// in the sheet: an atlas of one frame is a picture.</para>
/// </remarks>
public sealed class ProfileSceneKind : ICosmeticKind
{
    /// <summary>
    /// One sheet holds every drawing in the scene, so this is larger than a frame's part and is
    /// still the whole cosmetic rather than one piece of it.
    /// </summary>
    private const long MaxSheetBytes = 2 * 1024 * 1024;

    /// <summary>
    /// A self-animating file — an APNG, a GIF, an animated SVG — for the one or two actors that are
    /// a drawing rather than a strip.
    /// </summary>
    private const long MaxFigureBytes = 1024 * 1024;

    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("profile.scene")
           .Describing("Actors moving across the whole profile card, with a depth each")
           .Rendering(RenderPrimitive.SceneStage)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(750)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<ProfileScenePayload>()
           .Tuning<ProfileSceneTuning>()
           .RequiresAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.SpriteSheet, MaxSheetBytes)
           .AllowsAsset(CosmeticAssetSlot.Secondary, CosmeticAssetKind.Image, MaxFigureBytes)
           .AllowsAsset(CosmeticAssetSlot.Tertiary, CosmeticAssetKind.Image, MaxFigureBytes)
           .AllowsAsset(CosmeticAssetSlot.Quaternary, CosmeticAssetKind.Image, MaxFigureBytes)

           // The still a self-animating actor is replaced by when motion is off. A strip has its own
           // `still` frame and needs none of this; an APNG cannot be paused, so the only way to hold
           // one still is to draw something else.
           .AllowsAsset(CosmeticAssetSlot.Poster, CosmeticAssetKind.Image, MaxFigureBytes)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
