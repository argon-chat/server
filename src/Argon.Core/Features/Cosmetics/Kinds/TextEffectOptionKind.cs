namespace Argon.Features.Cosmetics.Kinds;

/// <summary>
/// A treatment carries nothing: what it does is the client file matching its slug.
/// </summary>
public sealed class TextEffectOptionPayload : ICosmeticWirePayload
{
    public ICosmeticPayload ToWire() => new PayloadTextEffect();
}

/// <summary>
/// One treatment a display name may be drawn with — a gradient, a glow, something that shimmers.
/// </summary>
/// <remarks>
/// <para>The one family of option that a row genuinely cannot add on its own, because a treatment
/// <i>is</i> code: rules and keyframes, which must ship in the client rather than arrive from the
/// database. Letting a row carry CSS is the thing this whole design refuses.</para>
///
/// <para>So a treatment is still an item — it is priced, owned, published and switched off exactly
/// like everything else — and the client additionally holds a file per slug. An item whose slug this
/// build has no file for is simply not offered, which is what makes an older client safe against
/// treatments released after it.</para>
/// </remarks>
public sealed class TextEffectOptionKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("option.text-effect")
            .Describing("A treatment a display name is drawn with")
            .Compositional()
            .Rendering(RenderPrimitive.TextStyle)
            .Layer(0)
            .Stacking(StackingRule.Single)
            .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
            .Payload<TextEffectOptionPayload>()
            .Entitlement(CosmeticEntitlement.UltimaOrOwned);
}
