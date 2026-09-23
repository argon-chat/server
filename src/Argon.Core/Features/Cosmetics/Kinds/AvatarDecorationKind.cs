namespace Argon.Features.Cosmetics.Kinds;

public sealed class AvatarDecorationPayload : IValidatableCosmeticPayload, ICosmeticWirePayload
{
    /// <summary>
    /// How far the avatar is inset inside the decoration, as a percentage of its own size. A frame
    /// that draws a ring needs the avatar pulled in; an overlay that sits on top needs zero.
    /// </summary>
    /// <remarks>
    /// Whole percent. It was a double, which offered an operator a precision that does not survive
    /// being multiplied by a forty-pixel avatar.
    /// </remarks>
    public byte InsetPct { get; set; }

    /// <summary>Whether the decoration is drawn under the avatar rather than over it.</summary>
    public bool Beneath { get; set; }

    /// <summary>Past this the avatar is more decoration than face.</summary>
    private const byte MaxInsetPct = 40;

    public void Validate(ICosmeticPayloadReport report)
    {
        if (InsetPct > MaxInsetPct)
            report.Error($"insetPct is {InsetPct}, and the range is 0 to {MaxInsetPct}");
    }

    public ICosmeticPayload ToWire() => new PayloadAvatarDecoration(InsetPct, Beneath);
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
            .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
