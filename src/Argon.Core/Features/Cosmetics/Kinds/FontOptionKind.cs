namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

public sealed class FontOptionPayload
{
    /// <summary>
    /// The CSS family the client asks for. Either a face already in the bundle, or the family name
    /// the uploaded file is registered under — the client builds the <c>@font-face</c> from the
    /// asset, so nothing here is resolved against what the viewer happens to have installed.
    /// </summary>
    [Required]
    [StringLength(96)]
    public string CssFamily { get; set; } = null!;
}

/// <summary>
/// One typeface a display name may be set in.
/// </summary>
/// <remarks>
/// <para>An item rather than a hard-coded list, so a new face is an upload from the admin console:
/// the file itself, and the same publish gate everything else goes through. Carrying the file is the
/// point — this is the row that names the face, and a face is served to every viewer of the name
/// rather than looked for on their machine.</para>
///
/// <para>Compositional: nobody wears a font. It is chosen on the nickname style's font axis.</para>
/// </remarks>
public sealed class FontOptionKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("option.font")
           .Describing("A typeface a display name may be set in")
           .Compositional()
           .Rendering(RenderPrimitive.TextStyle)
           .Layer(0)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<FontOptionPayload>()
           .AllowsAsset(CosmeticAssetSlot.Primary, CosmeticAssetKind.Font, 2 * 1024 * 1024)
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
