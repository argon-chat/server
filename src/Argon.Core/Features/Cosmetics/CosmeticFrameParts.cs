namespace Argon.Features.Cosmetics;

using System.Collections.Frozen;
using System.Text.Json.Serialization;

/// <summary>
/// What a frame's geometry is held to, so that a catalogue row cannot arrange itself over somebody
/// else's screen.
/// </summary>
/// <remarks>
/// A frame is the one cosmetic allowed outside the card it belongs to, which is the whole reason it
/// can hang a character over an edge — and also the reason it is the one cosmetic that needs a
/// ceiling. Everything else is bounded by the box it is drawn in.
/// </remarks>
public static class CosmeticFrameLimits
{
    /// <summary>
    /// Enough for a band and three pieces hung off it, which is what the shapes asked for need, and
    /// few enough that a frame cannot become a scene.
    /// </summary>
    public const int MaxParts = 8;

    /// <summary>How far a part may reach outside the card, on any one side.</summary>
    public const int MaxOutset = 96;

    /// <summary>How far a band may lie over the card.</summary>
    public const int MaxWidth = 64;

    /// <summary>How far a part may push the card's own content in.</summary>
    public const int MaxInset = 64;

    public const int MaxSlice    = 512;
    public const int MaxPropSize = 512;
    public const int MaxOffset   = 256;

    public const int MaxSpriteFrames = 64;

    /// <summary>Long enough for a name, short enough not to be storage.</summary>
    public const int MaxMotionKeyLength = 32;

    /// <summary>Colours around a ring. Four; a fifth is not distinguishable on an edge.</summary>
    public const int MaxRingColors = 4;

    /// <summary>
    /// The smallest a profile card is drawn, which is what the reach check measures against.
    /// </summary>
    /// <remarks>
    /// <para>A part pinned to <c>top</c> is centred horizontally, so how far it escapes sideways is
    /// its own half-width against the card's — and the card's width is the client's, not this
    /// model's. Measuring against the smallest card the client ever draws makes the check
    /// pessimistic rather than wrong: a part that passes here passes at every width.</para>
    ///
    /// <para>Without it the free axis went unchecked, and a 512px prop pinned to <c>top</c> and
    /// nudged sideways could hang a third of a screen past the card while every rule reported it
    /// fine.</para>
    /// </remarks>
    public const int MinCardWidth = 320;

    /// <inheritdoc cref="MinCardWidth"/>
    public const int MinCardHeight = 160;
}

/// <summary>How the middle of a nine-slice is laid along an edge.</summary>
public enum CosmeticFrameRepeat
{
    Stretch,
    Repeat,
    Round,
    Space
}

/// <summary>
/// Which of the card's nine points a part hangs off.
/// </summary>
/// <remarks>
/// It is placed outside, touching: anchored to <see cref="Top"/>, a part's lower edge rests on the
/// card's upper edge, so the default is a thing sitting on the frame rather than a thing half-buried
/// in it. One rule for all nine points, and the part's nudge pulls it in from there.
/// </remarks>
public enum CosmeticFrameAnchor
{
    TopLeft,
    Top,
    TopRight,
    Left,
    Center,
    Right,
    BottomLeft,
    Bottom,
    BottomRight
}

/// <summary>
/// How a part moves, as numbers.
/// </summary>
/// <remarks>
/// <para><b>The vocabulary lives in the client and the numbers live here.</b> A row carrying
/// keyframes would be CSS arriving from the database onto everybody who opens a profile, which is
/// the line every payload in this system draws. So a row names a movement and says how far, how
/// often and how far out of step it is; what that movement actually is, is a file in the client.</para>
///
/// <para><see cref="Kind"/> is the one string left in this file, and it is a string for the same
/// reason a text treatment's slug is: the client keeps one file per movement, deleting the file
/// removes it, and checking the set on this side would make adding a movement a server release. An
/// older client copes with a name it does not know by not moving the part.</para>
/// </remarks>
public sealed class CosmeticFrameMotion
{
    public string? Kind { get; set; }

    /// <summary>How far it goes. Degrees, pixels or hundredths, depending on the movement.</summary>
    public short? Amount { get; set; }

    public int? PeriodMs { get; set; }

    /// <summary>
    /// How far out of step this part starts.
    /// </summary>
    /// <remarks>
    /// <b>This is the difference between a frame that moves and a frame that is alive.</b> Parts
    /// sharing a period and a phase move as one sheet, which reads as a picture being animated;
    /// giving each a phase of its own reads as leaves in the same wind.
    /// </remarks>
    public int? PhaseMs { get; set; }

    internal void Validate(ICosmeticPayloadReport report, string at)
    {
        if (Kind is not { Length: > 0 } named)
        {
            report.Error($"{at}.kind names no movement");
            return;
        }

        if (named.Length > CosmeticFrameLimits.MaxMotionKeyLength)
            report.Error($"{at}.kind is longer than {CosmeticFrameLimits.MaxMotionKeyLength} characters");

        CosmeticPayloadChecks.Bound(report, Amount, $"{at}.amount", (short)-360, (short)360);
        CosmeticPayloadChecks.Bound(report, PeriodMs, $"{at}.periodMs", 120, 120_000);
        CosmeticPayloadChecks.Bound(report, PhaseMs, $"{at}.phaseMs", -120_000, 120_000);
    }
}

/// <summary>
/// A part whose picture is a strip of frames rather than one drawing.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="CosmeticFrameMotion"/> because it is not a movement — it is how the
/// file is read. The two compose: a bird whose wings are a sprite can also bob.
/// </remarks>
public sealed class CosmeticFrameSprite
{
    public int? Frames  { get; set; }
    public int? Columns { get; set; }
    public int? Fps     { get; set; }

    /// <summary>
    /// The frame to stand on when movement is off.
    /// </summary>
    /// <remarks>
    /// Named rather than assumed to be the first, because a strip's first frame is often the one
    /// mid-stride. Under a reduced-motion preference this is the whole cosmetic, so it is the
    /// author's choice and not the renderer's.
    /// </remarks>
    public int? Still { get; set; }

    internal void Validate(ICosmeticPayloadReport report, string at)
    {
        var frames  = CosmeticPayloadChecks.Require(report, Frames, $"{at}.frames", 2, CosmeticFrameLimits.MaxSpriteFrames);
        var columns = CosmeticPayloadChecks.Require(report, Columns, $"{at}.columns", 1, CosmeticFrameLimits.MaxSpriteFrames);

        CosmeticPayloadChecks.Bound(report, Fps, $"{at}.fps", 1, 60);

        if (frames is not { } count || columns is not { } across)
            return;

        // The strip is read as a grid, so a row that runs out halfway would play a blank frame.
        if (across > count || count % across is not 0)
            report.Error($"{at}.frames must divide evenly into rows of {across}");

        if (Still is { } still && (still < 0 || still >= count))
            report.Error($"{at}.still names frame {still}, and the strip has {count}");
    }
}

/// <summary>
/// One piece of a frame.
/// </summary>
/// <remarks>
/// <para><b>Three shapes, three types.</b> It was one flat type with a string discriminator and
/// every field on every part, which meant a <c>ring</c> could be handed a <c>slice</c> — and the
/// only way to refuse that was a list of field names matched against a switch over the same names
/// written out twice. A subtype cannot be handed a field it does not have, so the refusal is the
/// type system's and the list is gone.</para>
///
/// <para>The order of <see cref="ProfileFramePayload.Parts"/> is still the order they are drawn in,
/// which is why they are one list and not three.</para>
///
/// <para>No data annotations here on purpose. <c>Validator.TryValidateObject</c> does not walk into
/// a nested object, so a <c>[Range]</c> on this type would be a rule that looks enforced and is not
/// — every number below is checked by hand.</para>
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CosmeticFrameSurround), "surround")]
[JsonDerivedType(typeof(CosmeticFrameProp), "prop")]
[JsonDerivedType(typeof(CosmeticFrameRing), "ring")]
public abstract class CosmeticFramePart
{
    /// <summary>What a <c>type</c> may say, which is the whole of it.</summary>
    internal static readonly FrozenDictionary<string, Type> Shapes = new Dictionary<string, Type>
    {
        ["surround"] = typeof(CosmeticFrameSurround),
        ["prop"]     = typeof(CosmeticFrameProp),
        ["ring"]     = typeof(CosmeticFrameRing)
    }.ToFrozenDictionary();

    internal const string ShapeNames = "'surround', 'prop' or 'ring'";

    /// <summary>Whether this part is drawn over the card's own content or under it.</summary>
    public bool? Over { get; set; }

    /// <summary>
    /// How solid the part is, in percent.
    /// </summary>
    /// <remarks>
    /// Whole percent rather than a fraction. Nothing here is a measurement — it is a number an
    /// operator types into a box — and a hundred steps is finer than anybody can see on a frame.
    /// </remarks>
    public byte? OpacityPct { get; set; }

    /// <summary>
    /// How far this part pushes the card's own content in, per side.
    /// </summary>
    /// <remarks>
    /// Stated rather than taken from a band's width. A dense band of leaves wants the avatar out
    /// from under it and a sparse vine does not, and only the person who drew the picture knows
    /// which one it is — deriving this from the thickness would give the second one an indent it can
    /// never refuse.
    /// </remarks>
    public int[]? Inset { get; set; }

    public CosmeticFrameMotion? Motion { get; set; }

    internal void Validate(ICosmeticPayloadReport report, string at)
    {
        CosmeticPayloadChecks.Bound(report, OpacityPct, $"{at}.opacityPct", (byte)5, (byte)100);
        CosmeticPayloadChecks.FourSides(report, Inset, $"{at}.inset", 0, CosmeticFrameLimits.MaxInset, required: false);

        Motion?.Validate(report, $"{at}.motion");

        ValidateShape(report, at);
    }

    private protected abstract void ValidateShape(ICosmeticPayloadReport report, string at);

    /// <summary>The file this part draws, for a part that draws one.</summary>
    private protected static void RequireSlot(ICosmeticPayloadReport report, CosmeticAssetSlot? slot, string at, string shape)
    {
        if (slot is null)
            report.Error($"{at}.slot names no file, and a {shape} is a picture");
    }
}

/// <summary>
/// The band, expressed exactly as a nine-slice is.
/// </summary>
public sealed class CosmeticFrameSurround : CosmeticFramePart
{
    public CosmeticAssetSlot? Slot { get; set; }

    /// <summary>Where to cut the source picture, in its own pixels: top, right, bottom, left.</summary>
    public int[]? Slice { get; set; }

    /// <summary>
    /// How thick the band is on the card, per side.
    /// </summary>
    /// <remarks>
    /// <b>This is where a frame's shape comes from.</b> Around the whole card, along the top alone,
    /// top and bottom, bottom alone — all four are which of these four numbers are not zero. There
    /// is no shape field and no enum of shapes, because a shape is not a thing to choose from a list.
    /// </remarks>
    public int[]? Width { get; set; }

    /// <summary>How far the band reaches outside the card, per side.</summary>
    public int[]? Outset { get; set; }

    public CosmeticFrameRepeat? Repeat { get; set; }

    /// <summary>Whether the middle of the nine is drawn too, rather than left as a hole.</summary>
    public bool? Fill { get; set; }

    private protected override void ValidateShape(ICosmeticPayloadReport report, string at)
    {
        RequireSlot(report, Slot, at, "surround");

        CosmeticPayloadChecks.FourSides(report, Slice, $"{at}.slice", 0, CosmeticFrameLimits.MaxSlice, required: true);
        CosmeticPayloadChecks.FourSides(report, Width, $"{at}.width", 0, CosmeticFrameLimits.MaxWidth, required: true);
        CosmeticPayloadChecks.FourSides(report, Outset, $"{at}.outset", 0, CosmeticFrameLimits.MaxOutset, required: false);

        if (Width is { Length: CosmeticPayloadChecks.Sides } sides && sides[0] + sides[1] + sides[2] + sides[3] is 0)
            report.Error($"{at}.width is zero on every side, so the band is drawn nowhere");
    }
}

/// <summary>
/// A piece at its own size, pinned to one of the card's nine points.
/// </summary>
public sealed class CosmeticFrameProp : CosmeticFramePart
{
    public CosmeticAssetSlot? Slot { get; set; }

    public CosmeticFrameAnchor? Anchor { get; set; }

    public int? W { get; set; }
    public int? H { get; set; }

    /// <summary>
    /// Screen-wise nudge: right and down are positive, so a positive <c>dy</c> on a top anchor drags
    /// the part down onto the card.
    /// </summary>
    public int? Dx { get; set; }

    public int? Dy { get; set; }

    public CosmeticFrameSprite? Sprite { get; set; }

    private protected override void ValidateShape(ICosmeticPayloadReport report, string at)
    {
        RequireSlot(report, Slot, at, "prop");

        Sprite?.Validate(report, $"{at}.sprite");

        if (Anchor is not { } anchor)
        {
            report.Error($"{at}.anchor names no point of the card");
            return;
        }

        var width  = CosmeticPayloadChecks.Require(report, W, $"{at}.w", 1, CosmeticFrameLimits.MaxPropSize);
        var height = CosmeticPayloadChecks.Require(report, H, $"{at}.h", 1, CosmeticFrameLimits.MaxPropSize);

        CosmeticPayloadChecks.Bound(report, Dx, $"{at}.dx", -CosmeticFrameLimits.MaxOffset, CosmeticFrameLimits.MaxOffset);
        CosmeticPayloadChecks.Bound(report, Dy, $"{at}.dy", -CosmeticFrameLimits.MaxOffset, CosmeticFrameLimits.MaxOffset);

        if (width is not { } w || height is not { } h)
            return;

        var dx = Dx ?? 0;
        var dy = Dy ?? 0;

        // The reach is what is bounded, not the nudge: a part 200px tall pulled 8px down still hangs
        // 192px over the card. Both axes are measured — on the one the anchor names the part is
        // pushed clear of the edge, and on the one it does not it is centred, so what escapes is its
        // half-width against the smallest card the client draws. Only the anchored axis used to be
        // checked, which let a 512px prop pinned to top and nudged sideways hang 320px past the card.
        ReportReach(report, $"{at} reaches above", anchor is
            CosmeticFrameAnchor.TopLeft or CosmeticFrameAnchor.Top or CosmeticFrameAnchor.TopRight
                ? h - dy
                : Centred(h, -dy, CosmeticFrameLimits.MinCardHeight));

        ReportReach(report, $"{at} reaches below", anchor is
            CosmeticFrameAnchor.BottomLeft or CosmeticFrameAnchor.Bottom or CosmeticFrameAnchor.BottomRight
                ? h + dy
                : Centred(h, dy, CosmeticFrameLimits.MinCardHeight));

        ReportReach(report, $"{at} reaches left", anchor is
            CosmeticFrameAnchor.TopLeft or CosmeticFrameAnchor.Left or CosmeticFrameAnchor.BottomLeft
                ? w - dx
                : Centred(w, -dx, CosmeticFrameLimits.MinCardWidth));

        ReportReach(report, $"{at} reaches right", anchor is
            CosmeticFrameAnchor.TopRight or CosmeticFrameAnchor.Right or CosmeticFrameAnchor.BottomRight
                ? w + dx
                : Centred(w, dx, CosmeticFrameLimits.MinCardWidth));
    }

    /// <summary>
    /// How far a centred part of this size, nudged this far, passes the edge of the smallest card.
    /// </summary>
    private static int Centred(int size, int nudge, int card) => Math.Max(0, ((size - card) / 2) + nudge);

    private static void ReportReach(ICosmeticPayloadReport report, string what, int reach)
    {
        if (reach > CosmeticFrameLimits.MaxOutset)
            report.Error($"{what} {reach}px, and {CosmeticFrameLimits.MaxOutset}px is as far as a frame leaves the card");
    }
}

/// <summary>
/// The painted border, and the only part that needs no file.
/// </summary>
public sealed class CosmeticFrameRing : CosmeticFramePart
{
    public int? Thickness { get; set; }

    /// <summary>One colour is a flat ring; more make a gradient around it.</summary>
    public CosmeticGradient? Colors { get; set; }

    public int? Angle { get; set; }

    /// <summary>How far the colour bleeds past the edge, in percent. Zero is a clean line.</summary>
    public byte? GlowPct { get; set; }

    private protected override void ValidateShape(ICosmeticPayloadReport report, string at)
    {
        CosmeticPayloadChecks.Require(report, Thickness, $"{at}.thickness", 1, 8);

        if (Colors is null)
            report.Error($"{at}.colors needs at least one colour for there to be a ring");
        else
            Colors.Validate(report, $"{at}.colors", atLeast: 1, atMost: CosmeticFrameLimits.MaxRingColors);

        CosmeticPayloadChecks.Bound(report, Angle, $"{at}.angle", 0, 360);
        CosmeticPayloadChecks.Bound(report, GlowPct, $"{at}.glowPct", (byte)0, (byte)100);
    }
}

/// <summary>
/// A frame around somebody's profile card: a list of parts, drawn in the order they are listed.
/// </summary>
/// <remarks>
/// <para>It replaced a payload of four numbers — a width, some colours, an angle and a glow — which
/// can describe exactly one shape, a coloured edge. That shape is still here as a
/// <see cref="CosmeticFrameRing"/>, because a plain coloured edge is a real frame and the cheapest
/// one to give away; it is now one thing a frame can contain rather than the only thing a frame can
/// be.</para>
///
/// <para>Everything a part carries is a number, an enum member or the slot of an uploaded file,
/// which is what keeps a new frame a row somebody creates in the console rather than a release.</para>
/// </remarks>
public sealed class ProfileFramePayload : IValidatableCosmeticPayload
{
    public CosmeticFramePart[]? Parts { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Parts is not { Length: > 0 } parts)
        {
            report.Error("parts needs at least one part for there to be a frame");
            return;
        }

        if (parts.Length > CosmeticFrameLimits.MaxParts)
            report.Error($"parts is capped at {CosmeticFrameLimits.MaxParts}; a frame is not a scene");

        for (var index = 0; index < parts.Length; index++)
        {
            parts[index].Validate(report, $"parts[{index}]");
        }
    }
}
