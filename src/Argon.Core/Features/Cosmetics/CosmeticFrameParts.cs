namespace Argon.Features.Cosmetics;

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
/// <para>Which is also why <see cref="Kind"/> is not checked against a list here. The client keeps
/// one file per movement and deleting the file removes it, exactly as it does for a text treatment —
/// so validating the set on this side would make adding a movement a server release too, and an
/// older client would still have to cope with a name it does not know. It copes by not moving the
/// part, which is the same way it copes with everything else it is too old for.</para>
/// </remarks>
public sealed class CosmeticFrameMotion
{
    public string? Kind { get; set; }

    /// <summary>How far it goes. Degrees, pixels or a fraction, depending on the movement.</summary>
    public double? Amount { get; set; }

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
    public int? Frames { get; set; }

    public int? Columns { get; set; }

    public int? Fps { get; set; }

    /// <summary>
    /// The frame to stand on when movement is off.
    /// </summary>
    /// <remarks>
    /// Named rather than assumed to be the first, because a strip's first frame is often the one
    /// mid-stride. Under a reduced-motion preference this is the whole cosmetic, so it is the
    /// author's choice and not the renderer's.
    /// </remarks>
    public int? Still { get; set; }
}

/// <summary>
/// One piece of a frame.
/// </summary>
/// <remarks>
/// <para><b>One flat type with a discriminator, rather than three.</b> A frame's parts are a list
/// because their order is the order they are drawn in, and that order is a property of the frame —
/// three typed lists would have to invent a rule for interleaving them, and any rule would be one
/// some frame wants broken.</para>
///
/// <para>The cost is that every part carries every field, so a <c>ring</c> could be handed a
/// <c>slice</c>. <see cref="ProfileFramePayload"/> refuses that by name rather than ignoring it: a
/// field that does nothing is an operator who believes it does something.</para>
///
/// <para>No data annotations here on purpose. <c>Validator.TryValidateObject</c> does not walk into
/// a nested object, so a <c>[Range]</c> on this type would be a rule that looks enforced and is not
/// — every number below is checked by hand in the payload's own validation.</para>
/// </remarks>
public sealed class CosmeticFramePart
{
    /// <summary><c>surround</c>, <c>prop</c> or <c>ring</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Which of the item's uploaded files this part draws. Named as the asset slot is.</summary>
    public string? Slot { get; set; }

    // surround — the band, expressed exactly as a nine-slice is.

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

    /// <summary><c>stretch</c>, <c>repeat</c>, <c>round</c> or <c>space</c>.</summary>
    public string? Repeat { get; set; }

    /// <summary>Whether the middle of the nine is drawn too, rather than left as a hole.</summary>
    public bool? Fill { get; set; }

    // prop — a piece at its own size, pinned to one of nine points.

    /// <summary>
    /// Which point of the card this part hangs off.
    /// </summary>
    /// <remarks>
    /// It is placed <i>outside, touching</i>: anchored to <c>top</c>, its lower edge rests on the
    /// card's upper edge, so the default is a thing sitting on the frame rather than a thing
    /// half-buried in it. One rule for all nine points, and <see cref="Dx"/>/<see cref="Dy"/> pull
    /// it in from there.
    /// </remarks>
    public string? Anchor { get; set; }

    public int? W { get; set; }
    public int? H { get; set; }

    /// <summary>Screen-wise nudge: right and down are positive, so a positive <c>dy</c> on a top
    /// anchor drags the part down onto the card.</summary>
    public int? Dx { get; set; }

    public int? Dy { get; set; }

    public CosmeticFrameSprite? Sprite { get; set; }

    // ring — the painted border, and the only part that needs no file.

    public int? Thickness { get; set; }

    /// <summary>One ARGB colour is flat; more make a gradient around the ring.</summary>
    public int[]? Colors { get; set; }

    public int? Angle { get; set; }

    /// <summary>How far the colour bleeds past the edge. Zero is a clean line.</summary>
    public double? Glow { get; set; }

    // Common to all three.

    /// <summary>Whether this part is drawn over the card's own content or under it.</summary>
    public bool? Over { get; set; }

    public double? Opacity { get; set; }

    /// <summary>
    /// How far this part pushes the card's own content in, per side.
    /// </summary>
    /// <remarks>
    /// Stated rather than taken from <see cref="Width"/>. A dense band of leaves wants the avatar
    /// out from under it and a sparse vine does not, and only the person who drew the picture knows
    /// which one it is — deriving this from the thickness would give the second one an indent it can
    /// never refuse.
    /// </remarks>
    public int[]? Inset { get; set; }

    public CosmeticFrameMotion? Motion { get; set; }
}

/// <summary>
/// A frame around somebody's profile card: a list of parts, drawn in the order they are listed.
/// </summary>
/// <remarks>
/// <para>It replaced a payload of four numbers — a width, some colours, an angle and a glow — which
/// can describe exactly one shape, a coloured edge. That shape is still here as a
/// <c>ring</c> part, because a plain coloured edge is a real frame and the cheapest one to give
/// away; it is now one thing a frame can contain rather than the only thing a frame can be.</para>
///
/// <para>Everything a part carries is a number or the name of an uploaded file, which is what keeps
/// a new frame a row somebody creates in the console rather than a release.</para>
/// </remarks>
public sealed class ProfileFramePayload : IValidatableCosmeticPayload
{
    public CosmeticFramePart[]? Parts { get; set; }

    private static readonly string[] SurroundFields = ["slice", "width", "outset", "repeat", "fill"];
    private static readonly string[] PropFields     = ["anchor", "w", "h", "dx", "dy", "sprite"];
    private static readonly string[] RingFields     = ["thickness", "colors", "angle", "glow"];

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Parts is not { Length: > 0 })
        {
            report.Error("parts needs at least one part for there to be a frame");
            return;
        }

        if (Parts.Length > CosmeticFrameLimits.MaxParts)
            report.Error($"parts is capped at {CosmeticFrameLimits.MaxParts}; a frame is not a scene");

        for (var index = 0; index < Parts.Length; index++)
        {
            ValidatePart(report, Parts[index], index);
        }
    }

    private static void ValidatePart(ICosmeticPayloadReport report, CosmeticFramePart part, int index)
    {
        var at = $"parts[{index}]";

        switch (part.Type)
        {
            case "surround":
                RefuseStrayFields(report, part, at, PropFields, RingFields);
                ValidateSurround(report, part, at);
                break;

            case "prop":
                RefuseStrayFields(report, part, at, SurroundFields, RingFields);
                ValidateProp(report, part, at);
                break;

            case "ring":
                RefuseStrayFields(report, part, at, SurroundFields, PropFields);
                ValidateRing(report, part, at);
                break;

            default:
                report.Error($"{at}.type is 'surround', 'prop' or 'ring'");
                return;
        }

        ValidateCommon(report, part, at);
    }

    private static void ValidateSurround(ICosmeticPayloadReport report, CosmeticFramePart part, string at)
    {
        RequireSlot(report, part, at);

        ValidateSides(report, part.Slice, $"{at}.slice", 0, CosmeticFrameLimits.MaxSlice, required: true);
        ValidateSides(report, part.Width, $"{at}.width", 0, CosmeticFrameLimits.MaxWidth, required: true);
        ValidateSides(report, part.Outset, $"{at}.outset", 0, CosmeticFrameLimits.MaxOutset, required: false);

        if (part.Width is { Length: 4 } && part.Width[0] + part.Width[1] + part.Width[2] + part.Width[3] == 0)
            report.Error($"{at}.width is zero on every side, so the band is drawn nowhere");

        if (part.Repeat is { Length: > 0 } and not ("stretch" or "repeat" or "round" or "space"))
            report.Error($"{at}.repeat is 'stretch', 'repeat', 'round' or 'space'");
    }

    private static void ValidateProp(ICosmeticPayloadReport report, CosmeticFramePart part, string at)
    {
        RequireSlot(report, part, at);

        var anchor = part.Anchor;

        if (!IsAnchor(anchor))
        {
            report.Error($"{at}.anchor is one of topLeft, top, topRight, left, center, right, bottomLeft, bottom, bottomRight");
            return;
        }

        var width  = Require(report, part.W, $"{at}.w", 1, CosmeticFrameLimits.MaxPropSize);
        var height = Require(report, part.H, $"{at}.h", 1, CosmeticFrameLimits.MaxPropSize);

        Bound(report, part.Dx, $"{at}.dx", -CosmeticFrameLimits.MaxOffset, CosmeticFrameLimits.MaxOffset);
        Bound(report, part.Dy, $"{at}.dy", -CosmeticFrameLimits.MaxOffset, CosmeticFrameLimits.MaxOffset);

        if (width is null || height is null)
            return;

        // The reach is what is bounded, not the nudge. A part 200px tall pulled 8px down still hangs
        // 192px over the card, and a rule that only read the nudge would have let it.
        var dx = part.Dx ?? 0;
        var dy = part.Dy ?? 0;

        ReportReach(report, $"{at} reaches above", anchor!.StartsWith("top", StringComparison.Ordinal) ? height.Value - dy : 0);
        ReportReach(report, $"{at} reaches below", anchor.StartsWith("bottom", StringComparison.Ordinal) ? height.Value + dy : 0);
        ReportReach(report, $"{at} reaches left", anchor is "left" or "topLeft" or "bottomLeft" ? width.Value - dx : 0);
        ReportReach(report, $"{at} reaches right", anchor is "right" or "topRight" or "bottomRight" ? width.Value + dx : 0);

        ValidateSprite(report, part.Sprite, $"{at}.sprite");
    }

    private static void ValidateRing(ICosmeticPayloadReport report, CosmeticFramePart part, string at)
    {
        if (part.Slot is { Length: > 0 })
            report.Error($"{at} is a ring and draws no file, so it takes no slot");

        Require(report, part.Thickness, $"{at}.thickness", 1, 8);

        if (part.Colors is not { Length: > 0 })
            report.Error($"{at}.colors needs at least one colour for there to be a ring");

        if (part.Colors is { Length: > 4 })
            report.Error($"{at}.colors is capped at four; more than that is not distinguishable on an edge");

        Bound(report, part.Angle, $"{at}.angle", 0, 360);
        Bound(report, part.Glow, $"{at}.glow", 0d, 1d);
    }

    private static void ValidateCommon(ICosmeticPayloadReport report, CosmeticFramePart part, string at)
    {
        Bound(report, part.Opacity, $"{at}.opacity", 0.05d, 1d);

        ValidateSides(report, part.Inset, $"{at}.inset", 0, CosmeticFrameLimits.MaxInset, required: false);

        if (part.Motion is null)
            return;

        if (part.Motion.Kind is not { Length: > 0 } kind)
        {
            report.Error($"{at}.motion.kind names no movement");
            return;
        }

        if (kind.Length > CosmeticFrameLimits.MaxMotionKeyLength)
            report.Error($"{at}.motion.kind is longer than {CosmeticFrameLimits.MaxMotionKeyLength} characters");

        Bound(report, part.Motion.Amount, $"{at}.motion.amount", -360d, 360d);
        Bound(report, part.Motion.PeriodMs, $"{at}.motion.periodMs", 120, 120_000);
        Bound(report, part.Motion.PhaseMs, $"{at}.motion.phaseMs", -120_000, 120_000);
    }

    private static void ValidateSprite(ICosmeticPayloadReport report, CosmeticFrameSprite? sprite, string at)
    {
        if (sprite is null)
            return;

        var frames = Require(report, sprite.Frames, $"{at}.frames", 2, CosmeticFrameLimits.MaxSpriteFrames);
        var columns = Require(report, sprite.Columns, $"{at}.columns", 1, CosmeticFrameLimits.MaxSpriteFrames);

        Bound(report, sprite.Fps, $"{at}.fps", 1, 60);

        if (frames is null || columns is null)
            return;

        // The strip is read as a grid, so a row that runs out halfway would play a blank frame.
        if (columns > frames || frames % columns is not 0)
            report.Error($"{at}.frames must divide evenly into rows of {columns}");

        if (sprite.Still is { } still && (still < 0 || still >= frames))
            report.Error($"{at}.still names frame {still}, and the strip has {frames}");
    }

    /// <summary>
    /// Refuses a field that belongs to a different sort of part.
    /// </summary>
    /// <remarks>
    /// Silently ignoring it would publish a frame that draws something other than what the operator
    /// filled in — the same reasoning that makes <c>MissingMemberHandling.Error</c> the setting on
    /// the way in.
    /// </remarks>
    private static void RefuseStrayFields(
        ICosmeticPayloadReport report,
        CosmeticFramePart part,
        string at,
        params string[][] foreign)
    {
        foreach (var group in foreign)
        {
            foreach (var field in group)
            {
                if (IsFilled(part, field))
                    report.Error($"{at} is a {part.Type} and has no '{field}'");
            }
        }
    }

    private static bool IsFilled(CosmeticFramePart part, string field) => field switch
    {
        "slice"     => part.Slice is not null,
        "width"     => part.Width is not null,
        "outset"    => part.Outset is not null,
        "repeat"    => part.Repeat is not null,
        "fill"      => part.Fill is not null,
        "anchor"    => part.Anchor is not null,
        "w"         => part.W is not null,
        "h"         => part.H is not null,
        "dx"        => part.Dx is not null,
        "dy"        => part.Dy is not null,
        "sprite"    => part.Sprite is not null,
        "thickness" => part.Thickness is not null,
        "colors"    => part.Colors is not null,
        "angle"     => part.Angle is not null,
        "glow"      => part.Glow is not null,
        _           => false
    };

    private static bool IsAnchor(string? anchor) => anchor is
        "topLeft" or "top" or "topRight"
        or "left" or "center" or "right"
        or "bottomLeft" or "bottom" or "bottomRight";

    private static void RequireSlot(ICosmeticPayloadReport report, CosmeticFramePart part, string at)
    {
        if (part.Slot is not { Length: > 0 } slot)
        {
            report.Error($"{at}.slot names no file, and a {part.Type} is a picture");
            return;
        }

        if (!Enum.TryParse<CosmeticAssetSlot>(slot, out _))
            report.Error($"{at}.slot '{slot}' is not an asset slot");
    }

    private static void ReportReach(ICosmeticPayloadReport report, string what, int reach)
    {
        if (reach > CosmeticFrameLimits.MaxOutset)
            report.Error($"{what} {reach}px, and {CosmeticFrameLimits.MaxOutset}px is as far as a frame leaves the card");
    }

    private static void ValidateSides(
        ICosmeticPayloadReport report,
        int[]? sides,
        string at,
        int low,
        int high,
        bool required)
    {
        if (sides is null)
        {
            if (required)
                report.Error($"{at} is missing; it is four numbers — top, right, bottom, left");

            return;
        }

        if (sides.Length is not 4)
        {
            report.Error($"{at} is four numbers — top, right, bottom, left — and has {sides.Length}");
            return;
        }

        for (var side = 0; side < 4; side++)
        {
            if (sides[side] < low || sides[side] > high)
                report.Error($"{at}[{side}] is {sides[side]}, and the range is {low} to {high}");
        }
    }

    private static int? Require(ICosmeticPayloadReport report, int? value, string at, int low, int high)
    {
        if (value is null)
        {
            report.Error($"{at} is missing");
            return null;
        }

        if (value < low || value > high)
        {
            report.Error($"{at} is {value}, and the range is {low} to {high}");
            return null;
        }

        return value;
    }

    private static void Bound(ICosmeticPayloadReport report, int? value, string at, int low, int high)
    {
        if (value is { } number && (number < low || number > high))
            report.Error($"{at} is {number}, and the range is {low} to {high}");
    }

    private static void Bound(ICosmeticPayloadReport report, double? value, string at, double low, double high)
    {
        if (value is { } number && (number < low || number > high))
            report.Error($"{at} is {number}, and the range is {low} to {high}");
    }
}
