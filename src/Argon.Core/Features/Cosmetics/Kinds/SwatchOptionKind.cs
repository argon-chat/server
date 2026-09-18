namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

public sealed class SwatchOptionPayload
{
    /// <summary>Six-digit hex, which is the whole of what a colour is.</summary>
    [Required]
    [RegularExpression("^#[0-9a-fA-F]{6}$")]
    [StringLength(7)]
    public string Hex { get; set; } = null!;
}

/// <summary>
/// One colour a treatment may be drawn in.
/// </summary>
/// <remarks>
/// The clearest case for options being items: a colour is a string, so there is nothing a release
/// could add that a row cannot. An operator types a hex and a name key, and every picker that reads
/// this kind has one more colour.
/// </remarks>
public sealed class SwatchOptionKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("option.swatch")
           .Describing("A colour a nickname treatment is drawn in")
           .Compositional()
           .Rendering(RenderPrimitive.TextStyle)
           .Layer(0)
           .Stacking(StackingRule.Single)
           .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
           .Payload<SwatchOptionPayload>()
           .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
