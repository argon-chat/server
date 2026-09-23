namespace Argon.Features.Cosmetics.Kinds;

using ion.runtime;

/// <summary>
/// The part of a name's look only its wearer can decide: their own colours, how they are laid out,
/// and how the name is set.
/// </summary>
/// <remarks>
/// A colour is a number the wearer picks, not a row somebody published: ARGB, up to six of them, and
/// held as three longs in memory — see <see cref="CosmeticGradient"/>. On the wire it is a list of
/// int32s, because a JavaScript number cannot hold an i8 exactly.
/// </remarks>
public sealed class NicknameStyleTuning : IValidatableCosmeticPayload
{
    /// <summary>One colour is a flat name; two or more are a gradient across it.</summary>
    public CosmeticGradient? Colors { get; set; }

    /// <summary>Which way the gradient runs, in degrees clockwise from left-to-right.</summary>
    public int? Angle { get; set; }

    /// <summary>
    /// The shape the colours are laid out in. The contract's own enum, so the value stored and the value
    /// sent are one declaration rather than two that have to be kept in step.
    /// </summary>
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
/// <para>Two axes and the wearer's own colours, and no rows of its own. Which faces and treatments
/// exist is whatever rows <c>option.font</c> and <c>option.text-effect</c> have: a face is a file and
/// a treatment is code on the client, so each is a catalogue row the wearer picks. A colour is neither
/// — it is a number — so it is the wearer's, on <see cref="NicknameStyleTuning"/>.</para>
///
/// <para><b>Bare, so there is nothing to choose before the choosing starts.</b> What is owned is the
/// face and the treatment, each on its own row; there is nothing left on this kind to sell, so it
/// declares no entitlement.</para>
/// </remarks>
public sealed class NicknameStyleKind : ICosmeticKind
{
    public const string Key = "nickname.style";

    public const string FontAxis   = "font";
    public const string EffectAxis = "effect";

    public static void Describe(ICosmeticKindDescriptor d)
        => d.Keyed(Key)
            .Describing("Font, colour and motion applied to a display name")
            .Rendering(RenderPrimitive.TextStyle)
            .On(CosmeticSurface.ProfileCard | CosmeticSurface.NicknameInMessages | CosmeticSurface.MemberListRow)
            .Layer(400)
            .Stacking(StackingRule.Single)
            .Scoped(CosmeticScope.Global | CosmeticScope.PerSpace)
            .Bare()
            .Tuning<NicknameStyleTuning>()
            .Facet(FontAxis, "option.font")
            .Facet(EffectAxis, "option.text-effect");

    /// <summary>
    /// The look of a name as the wire carries it: the options by catalogue row, the rest as numbers.
    /// Null when there is nothing to say, which is a name not styled.
    /// </summary>
    public static WornNickname? ToWire(IReadOnlyDictionary<string, Guid> options, NicknameStyleTuning? tuning)
    {
        Guid? Axis(string id) => options.TryGetValue(id, out var itemId) ? itemId : null;

        var font   = Axis(FontAxis);
        var effect = Axis(EffectAxis);

        IonArray<int>? colors = null;

        if (tuning?.Colors is { Count: > 0 } gradient)
        {
            var stops = new List<int>(gradient.Count);

            for (var at = 0; at < gradient.Count; at++)
            {
                stops.Add(gradient[at]);
            }

            colors = new IonArray<int>(stops);
        }

        var said = font is not null || effect is not null || colors is not null
                || tuning is { Angle: not null } or { Shape: not null } or { Animate: not null }
                                  or { Weight: not null } or { LetterSpacingCentiEm: not null };

        if (!said)
            return null;

        return new WornNickname(
            font,
            effect,
            colors,
            (ushort?)tuning?.Angle,
            tuning?.Shape,
            tuning?.Animate,
            (ushort?)tuning?.Weight,
            tuning?.LetterSpacingCentiEm);
    }

    /// <summary>
    /// What a name's look asks for, split into the options chosen and the tuning to store.
    /// </summary>
    /// <remarks>
    /// Null when a value cannot even be held — more colours than a gradient has room for, a spacing
    /// or a weight outside the type it is stored as. Anything representable is returned as it is, and
    /// its bounds are the tuning's own validation's to check.
    /// </remarks>
    public static (Dictionary<string, Guid> Options, NicknameStyleTuning Tuning)? FromWire(WornNickname wire)
    {
        var options = new Dictionary<string, Guid>(2);

        if (wire.font is { } font)
            options[FontAxis] = font;
        if (wire.effect is { } effect)
            options[EffectAxis] = effect;

        if (wire.colors is { Count: > CosmeticGradient.MaxStops })
            return null;

        if (wire.letterSpacing is { } spacing && (spacing < sbyte.MinValue || spacing > sbyte.MaxValue))
            return null;

        if (wire.weight is > (ushort)short.MaxValue)
            return null;

        var tuning = new NicknameStyleTuning
        {
            Colors               = wire.colors is { Count: > 0 } stops ? CosmeticGradient.Of(stops.ToArray()) : null,
            Angle                = wire.angle,
            Shape                = wire.shape,
            Animate              = wire.animate,
            Weight               = (short?)wire.weight,
            LetterSpacingCentiEm = (sbyte?)wire.letterSpacing
        };

        return (options, tuning);
    }
}
