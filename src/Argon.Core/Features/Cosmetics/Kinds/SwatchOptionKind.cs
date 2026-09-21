namespace Argon.Features.Cosmetics.Kinds;

public sealed class SwatchOptionPayload : IValidatableCosmeticPayload
{
    /// <summary>
    /// The colour, packed ARGB, which is the whole of what a colour is.
    /// </summary>
    /// <remarks>
    /// It was a hex string with a regular expression guarding it. The string was only ever read to
    /// be parsed, and the expression was the second of two hex checks in this namespace that had to
    /// agree — a colour is one integer, and storing it as one removes both.
    /// </remarks>
    public int Argb { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        // A swatch nobody can see is a row in a picker that does nothing when it is chosen.
        if (CosmeticColor.IsTransparent(Argb))
            report.Error("argb is fully transparent, so the swatch draws nothing");
    }
}

/// <summary>
/// One colour a treatment may be drawn in.
/// </summary>
/// <remarks>
/// The clearest case for options being items: a colour is a number, so there is nothing a release
/// could add that a row cannot. An operator picks a colour and a name key, and every picker that
/// reads this kind has one more.
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
