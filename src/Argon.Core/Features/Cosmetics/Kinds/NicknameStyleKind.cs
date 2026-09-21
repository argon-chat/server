namespace Argon.Features.Cosmetics.Kinds;

/// <summary>
/// The shape a name's colours are laid out in.
/// </summary>
/// <remarks>
/// A closed set rather than free text, because it is written into a style attribute on everybody
/// who sees the name.
/// </remarks>
public enum NicknameGradientShape
{
    Linear,
    Radial,
    Conic
}

/// <summary>
/// The part of a name's look only its wearer can decide: their own colours, how they are laid out,
/// and how the name is set.
/// </summary>
/// <remarks>
/// <para><b>Not an axis, because an axis is a list somebody else wrote.</b> A swatch is a colour an
/// operator published and priced; this is the colour a person picked because it is theirs. Both
/// reach the same property in the end, and which one wins is decided at the point of drawing rather
/// than here.</para>
///
/// <para>The colours are packed ARGB rather than hex strings. A colour input produces a string and
/// CSS consumes one, and both of those are the client's business — what is stored is the number,
/// checked once, with no parser between it and the database. See
/// <see cref="CosmeticGradient"/>.</para>
/// </remarks>
public sealed class NicknameStyleTuning : IValidatableCosmeticPayload
{
    /// <summary>One colour is a flat name; two or more are a gradient across it.</summary>
    public CosmeticGradient? Colors { get; set; }

    /// <summary>Which way the gradient runs, in degrees clockwise from left-to-right.</summary>
    public int? Angle { get; set; }

    public NicknameGradientShape? Shape { get; set; }

    /// <summary>Whether the colours travel along the name rather than sitting still.</summary>
    public bool? Animate { get; set; }

    /// <summary>The weight the face is set at, in the usual hundreds.</summary>
    public short? Weight { get; set; }

    /// <summary>
    /// How far the letters are pushed apart, in hundredths of an em.
    /// </summary>
    /// <remarks>
    /// Hundredths rather than a fraction: the useful range is a tenth of an em either side of
    /// nothing, and the step below a hundredth is not a step anybody can see in a member list.
    /// </remarks>
    public sbyte? LetterSpacingCentiEm { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        Colors?.Validate(report, "colors", atLeast: 1, atMost: CosmeticGradient.MaxStops);

        CosmeticPayloadChecks.Bound(report, Angle, "angle", 0, 360);
        CosmeticPayloadChecks.Bound(report, Weight, "weight", (short)100, (short)900);
        CosmeticPayloadChecks.Bound(report, LetterSpacingCentiEm, "letterSpacingCentiEm", (sbyte)-10, (sbyte)50);
    }
}

/// <summary>
/// The font, colour and motion of a display name wherever it is written.
/// </summary>
/// <remarks>
/// <para>Three axes, a set of colours its wearer picks, and no rows of its own. Which faces,
/// treatments and colours exist is whatever rows <c>option.font</c>, <c>option.text-effect</c> and
/// <c>option.swatch</c> have — so every combination of them is already on offer, where a picker of
/// pre-baked styles would have needed each combination authored and published.</para>
///
/// <para><b>Bare, so there is nothing to choose before the choosing starts.</b> Two rows existed
/// here for a while and they were the same offer twice: whatever either of them said, the axes and
/// the wearer's own colours said it after. What is owned is the face, the treatment and the colour,
/// each priced on its own row — and the font file sits on <c>option.font</c>, because that is the
/// row that names a face and a face is served to every viewer of the name.</para>
///
/// <para>No entitlement either: it reads a catalogue row, and this kind has none.</para>
/// </remarks>
public sealed class NicknameStyleKind : ICosmeticKind
{
    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed("nickname.style")
            .Describing("Font, colour and motion applied to a display name")
            .Rendering(RenderPrimitive.TextStyle)
            .On(CosmeticSurface.ProfileCard | CosmeticSurface.NicknameInMessages | CosmeticSurface.MemberListRow)
            .Layer(400)
            .Stacking(StackingRule.Single)
            .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
            .Bare()
            .Tuning<NicknameStyleTuning>()
            .Facet("font", "option.font")
            .Facet("effect", "option.text-effect")
            .Facet("color", "option.swatch");
}
