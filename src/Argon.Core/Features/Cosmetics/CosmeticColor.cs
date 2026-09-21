namespace Argon.Features.Cosmetics;

using System.Text.Json.Serialization;

/// <summary>
/// A colour, as the thirty-two bits it is.
/// </summary>
/// <remarks>
/// <para><b>Not a string.</b> A hex string is seven characters, a parse, a shape check and a second
/// shape check somewhere else that disagrees with the first — for a value that is one integer. It
/// is written that way in CSS, which is a reason for the client to format it and not a reason to
/// store it that way.</para>
///
/// <para>Alpha is the top byte, so an opaque colour is negative when read as a signed integer. That
/// is what <c>unchecked</c> is for, and it is why nothing here compares colours with
/// <c>&gt;</c>.</para>
/// </remarks>
public static class CosmeticColor
{
    /// <summary>Whether the colour is fully transparent, which is how "unset" is spelled.</summary>
    public static bool IsTransparent(int argb) => (uint)argb >> 24 is 0;
}

/// <summary>
/// Up to six colours, held as three longs and written as a list.
/// </summary>
/// <remarks>
/// <para><b>Three longs rather than an array, in memory.</b> A gradient is a short, fixed-ceiling
/// list of integers; an array of them is a heap allocation for something that fits in three fields,
/// and it arrives on a read path that runs per rendered name.</para>
///
/// <para><b>A list of numbers rather than three longs, on the wire.</b> Two colours packed into one
/// long exceed what a double can hold exactly, and JSON numbers are doubles everywhere the client
/// is — so the packed form would arrive in the browser quietly rounded, and the second colour of
/// somebody's name would be a colour nobody chose. The packing is this type's business and stops at
/// its own edge.</para>
///
/// <para>Six because a seventh is not distinguishable across a name or an edge, and because six is
/// what three longs hold.</para>
/// </remarks>
[JsonConverter(typeof(CosmeticGradientStjConverter))]
public sealed class CosmeticGradient
{
    public const int MaxStops = 6;

    private long pair0;
    private long pair1;
    private long pair2;

    private CosmeticGradient(int count) => Count = count;

    /// <summary>How many colours there are. Never larger than <see cref="MaxStops"/>.</summary>
    public int Count { get; }

    public int this[int index] => index switch
    {
        0 => Low(pair0),
        1 => High(pair0),
        2 => Low(pair1),
        3 => High(pair1),
        4 => Low(pair2),
        5 => High(pair2),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    public static CosmeticGradient Of(params ReadOnlySpan<int> colors)
    {
        if (colors.Length > MaxStops)
            throw new ArgumentOutOfRangeException(nameof(colors), $"a gradient holds {MaxStops} colours");

        var gradient = new CosmeticGradient(colors.Length);

        for (var at = 0; at < colors.Length; at++)
        {
            switch (at)
            {
                case 0: gradient.pair0 = Low(gradient.pair0, colors[at]); break;
                case 1: gradient.pair0 = High(gradient.pair0, colors[at]); break;
                case 2: gradient.pair1 = Low(gradient.pair1, colors[at]); break;
                case 3: gradient.pair1 = High(gradient.pair1, colors[at]); break;
                case 4: gradient.pair2 = Low(gradient.pair2, colors[at]); break;
                case 5: gradient.pair2 = High(gradient.pair2, colors[at]); break;
            }
        }

        return gradient;
    }

    public void Validate(ICosmeticPayloadReport report, string at, int atLeast, int atMost)
    {
        if (atMost > MaxStops)
            throw new ArgumentOutOfRangeException(nameof(atMost));

        if (Count < atLeast || Count > atMost)
            report.Error($"{at} has {Count} colours, and the range is {atLeast} to {atMost}");
    }

    /// <summary>The colours, in order. For a serializer and for nothing else.</summary>
    internal int[] ToArray()
    {
        var colors = new int[Count];

        for (var at = 0; at < Count; at++)
        {
            colors[at] = this[at];
        }

        return colors;
    }

    /// <summary>
    /// Refuses a list longer than the ceiling rather than truncating it.
    /// </summary>
    /// <remarks>
    /// Truncating would publish a gradient the operator did not author and give no sign of it. The
    /// count is checked again in validation, where the kind's own tighter ceiling applies — a ring
    /// takes four — and this is only the ceiling of the type itself.
    /// </remarks>
    internal static CosmeticGradient FromList(IReadOnlyList<int> colors)
    {
        if (colors.Count > MaxStops)
            throw new ArgumentOutOfRangeException(nameof(colors), $"a gradient holds {MaxStops} colours, and {colors.Count} arrived");

        Span<int> read = stackalloc int[colors.Count];

        for (var at = 0; at < colors.Count; at++)
        {
            read[at] = colors[at];
        }

        return Of(read);
    }

    private static int  Low(long pair)             => unchecked((int)(pair & 0xFFFFFFFFL));
    private static int  High(long pair)            => unchecked((int)((pair >> 32) & 0xFFFFFFFFL));
    private static long Low(long pair, int color)  => (pair & unchecked((long)0xFFFFFFFF00000000)) | (uint)color;
    private static long High(long pair, int color) => (pair & 0xFFFFFFFFL) | ((long)(uint)color << 32);
}
