namespace Argon.Features.Cosmetics.Kinds;

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
/// payload's business rather than the slot's — see <see cref="ProfileFramePayload"/>.</para>
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
