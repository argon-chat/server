namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// How a picture drawn over a whole profile card is laid into it.
/// </summary>
/// <remarks>
/// The effect over a card, and only that. A frame used to share this and no longer can: one picture
/// fitted to the card's own shape cannot leave the card, cannot hold a corner still while an edge
/// stretches, and has nowhere to put a second piece — see <c>ProfileFramePayload</c>.
/// </remarks>
public sealed class CardLayerPayload : IValidatableCosmeticPayload
{
    /// <summary>
    /// <c>stretch</c> pulls the art to the card's exact shape; <c>cover</c> keeps its proportions
    /// and crops.
    /// </summary>
    [StringLength(8)]
    public string? Fit { get; set; }

    [Range(0.05d, 1d)]
    public double? Opacity { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Fit is { Length: > 0 } and not ("stretch" or "cover"))
            report.Error("fit is either 'stretch' or 'cover'");
    }
}

/// <summary>
/// A border drawn around somebody's profile card.
/// </summary>
/// <remarks>
/// <para>Its own kind rather than a background with a hole in it: a background sits under everything
/// and a frame sits over everything, and that difference is the whole point of one of them. It also
/// means the two are worn together, which is what people do with them.</para>
///
/// <para>Its files are optional and ordinal. A frame made only of a coloured ring carries none; one
/// made of art carries a band and up to three pieces hung off it, and which file is which is the
/// payload's business rather than the slot's — see <c>ProfileFramePayload</c>.</para>
/// </remarks>
public sealed class ProfileFrameKind : ICosmeticKind
{
    /// <summary>
    /// Generous for a band and mean for a photograph. A frame is line art with transparency around
    /// it, and anything approaching this is the wrong sort of picture for the job.
    /// </summary>
    private const long MaxPartBytes = 512 * 1024;

    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("profile.frame")
           .Describing("A border drawn around the profile card")
           .Rendering(RenderPrimitive.FrameAssembly)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(600)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<ProfileFramePayload>()
           .AllowsAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.Image, MaxPartBytes)
           .AllowsAsset(CosmeticAssetSlot.Secondary, CosmeticAssetKind.Image, MaxPartBytes)
           .AllowsAsset(CosmeticAssetSlot.Tertiary, CosmeticAssetKind.Image, MaxPartBytes)
           .AllowsAsset(CosmeticAssetSlot.Quaternary, CosmeticAssetKind.Image, MaxPartBytes)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}

/// <summary>
/// Something moving over the whole profile card, above the frame and everything else.
/// </summary>
/// <remarks>
/// <para>The layer above a frame, because an effect is weather and a frame is the window: snow falls
/// in front of the glass. Anything here is drawn over somebody's own words and picture, so it is
/// held to a transparency the card can still be read through, and it never takes a click.</para>
/// </remarks>
public sealed class ProfileEffectKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("profile.effect")
           .Describing("Something moving across the whole profile card")
           .Rendering(RenderPrimitive.CardLayer)
           .On(CosmeticSurface.ProfileCard | CosmeticSurface.OwnProfile)
           .Layer(700)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<CardLayerPayload>()
           .RequiresAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.Image, 1024 * 1024)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
