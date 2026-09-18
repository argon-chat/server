namespace Argon.Features.Cosmetics.Kinds;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// The part of a name's look only its wearer can decide: their own colours, and which way they run.
/// </summary>
/// <remarks>
/// <para><b>Not an axis, because an axis is a list somebody else wrote.</b> A swatch is a colour an
/// operator published and priced; this is the colour a person picked because it is theirs. Both
/// reach the same property in the end, and which one wins is decided at the point of drawing rather
/// than here.</para>
///
/// <para>Hex rather than the packed ARGB the catalogue row uses. This is the value a colour input
/// produces and the value CSS consumes, and a format that has to be converted at both ends to be
/// stored in the middle is a format chosen for the middle's convenience.</para>
/// </remarks>
public sealed class NicknameStyleTuning : IValidatableCosmeticPayload
{
    /// <summary>One colour is a flat name; two or more are a gradient across it.</summary>
    public string[]? Stops { get; set; }

    /// <summary>Which way the gradient runs, in degrees clockwise from left-to-right.</summary>
    [Range(0, 360)]
    public int? Angle { get; set; }

    /// <summary>
    /// The shape the colours are laid out in: a line across, a circle out from the middle, or a
    /// sweep around it.
    /// </summary>
    /// <remarks>
    /// A closed list rather than free text, because it is written into a style attribute on
    /// everybody who sees the name.
    /// </remarks>
    [StringLength(8)]
    public string? Shape { get; set; }

    /// <summary>Whether the colours travel along the name rather than sitting still.</summary>
    public bool? Animate { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Shape is { Length: > 0 } and not ("linear" or "radial" or "conic"))
            report.Error("shape is one of 'linear', 'radial' or 'conic'");

        if (Stops is null)
            return;

        if (Stops.Length > 8)
            report.Error("stops is capped at eight; more than that is not distinguishable on a name");

        foreach (var stop in Stops)
        {
            if (!IsHexColour(stop))
                report.Error($"'{stop}' is not a colour; write it as #rrggbb");
        }
    }

    /// <summary>
    /// Exactly six hex digits behind a hash.
    /// </summary>
    /// <remarks>
    /// Narrow on purpose. This string is written into a style attribute on everybody who looks at
    /// the name, so what is allowed through is a shape with no room in it rather than whatever the
    /// browser happens to accept.
    /// </remarks>
    private static bool IsHexColour(string? value)
    {
        if (value is not { Length: 7 } || value[0] is not '#')
            return false;

        for (var at = 1; at < value.Length; at++)
        {
            if (!Uri.IsHexDigit(value[at]))
                return false;
        }

        return true;
    }
}

public sealed class NicknameStylePayload : IValidatableCosmeticPayload
{
    [Range(100, 900)]
    public int? Weight { get; set; }

    [Range(-0.1d, 0.5d)]
    public double? LetterSpacingEm { get; set; }

    /// <summary>
    /// Two or more ARGB stops, painted along the text. For a row that wants a gradient of its own
    /// rather than one composed from a swatch; when set it wins over the chosen colour.
    /// </summary>
    public int[]? GradientStops { get; set; }

    /// <summary>Named animation from the client's closed set. Null is a still name.</summary>
    [StringLength(64)]
    public string? AnimationId { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (GradientStops is { Length: 1 })
            report.Error("gradientStops needs at least two stops to describe a gradient");

        if (GradientStops is { Length: > 8 })
            report.Error("gradientStops is capped at eight; more than that is not distinguishable on a name");
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
           .Payload<NicknameStylePayload>()

           // No rows. Everything anybody sees comes from the three axes and the wearer's own
           // colours, so a row here would have carried nothing and the two that did were the same
           // offer twice.
           .Bare()
           .Tuning<NicknameStyleTuning>()
           .Facet("font", "option.font")
           .Facet("effect", "option.text-effect")
           .Facet("color", "option.swatch");

    // No Entitlement and no ProjectsToLegacy: both read a catalogue row, and this kind has none.
    // What an older client used to get in nickEffectId was the row's legacy id, which was never set.
}
