namespace Argon.Features.Cosmetics;

/// <summary>
/// What an orbit's geometry is held to, so that a catalogue row cannot arrange itself over somebody
/// else's screen.
/// </summary>
/// <remarks>
/// An avatar's ornament is drawn wherever an avatar is — a message row, a member list, a mention —
/// and unlike a frame it has no card whose width to belong to. Everything here is a percentage of
/// the face's own size for exactly that reason: the same row has to read at 96 pixels and at 22.
/// </remarks>
public static class CosmeticOrbitLimits
{
    /// <summary>
    /// How many figures one face may carry.
    /// </summary>
    /// <remarks>
    /// <para>Four was chosen for one large figure and its companions, and it is the wrong shape of
    /// limit: a swarm of small things crawling over a face is a different cosmetic from a raven
    /// circling one, and four of them is not a swarm. What actually has to be bounded is the cost —
    /// each figure is its own handful of animations, two of which are paint — and eight of something
    /// 20 per cent of a face is cheaper to look at than four of something the size of one.</para>
    ///
    /// <para>Eight rather than any other number because the console's own list editor holds eight,
    /// and a payload that accepts more than the form can hold is a row nobody can finish editing.</para>
    /// </remarks>
    public const int MaxSatellites = 8;

    /// <summary>How far from the centre of the face a figure may travel, as a percentage of it.</summary>
    public const double MaxRadiusPct = 200d;

    /// <summary>How big a figure may be drawn, as a percentage of the face.</summary>
    public const double MaxSizePct = 200d;

    /// <summary>
    /// How far past the edge of the face the whole arrangement may reach, as a percentage of it.
    /// </summary>
    /// <remarks>
    /// <b>The one ceiling that is not about any single number.</b> A modest radius and a large
    /// figure reach as far as a large radius and a modest figure, and the thing that lands on a
    /// neighbouring message is the sum. So the sum is what is checked.
    /// </remarks>
    public const double MaxReachPct = 150d;

    /// <summary>Fast enough to read as running, slow enough not to be a strobe.</summary>
    public const int MinPeriodMs = 240;

    public const int MaxPeriodMs = 120_000;

    public const int MaxSpriteFrames = 64;

    /// <summary>How far a figure may be scaled by the near and far ends of its travel.</summary>
    public const double MinDepthScale = 0.1d;

    public const double MaxDepthScale = 4d;

    /// <summary>How far a figure may blur at the far point, as a percentage of the face.</summary>
    public const double MaxBlurPct = 12d;

    /// <summary>How far a figure may rise and fall as it runs, as a percentage of the face.</summary>
    public const double MaxBobPct = 50d;

    /// <summary>Above this a decoration is a drawing rather than a face with a decoration on it.</summary>
    public const int MaxMinSizePx = 512;
}

/// <summary>
/// A figure whose picture is a strip of frames rather than one drawing.
/// </summary>
/// <remarks>
/// <para>Its own type rather than the frame's, deliberately. A kind is one file on each side and
/// deleting that file removes the kind; a kind that reached into another kind's payload types would
/// take that property away from both of them.</para>
///
/// <para>No data annotations here: <c>Validator.TryValidateObject</c> does not walk into a nested
/// object, so a <c>[Range]</c> would be a rule that looks enforced and is not.</para>
/// </remarks>
public sealed class CosmeticOrbitSprite
{
    public int? Frames { get; set; }

    public int? Columns { get; set; }

    public int? Fps { get; set; }

    /// <summary>
    /// The frame to stand on when nothing is moving.
    /// </summary>
    /// <remarks>
    /// Named rather than assumed to be the first, because a strip's first frame is often the one
    /// mid-stride. Under a reduced-motion preference, and on every face too small to run an orbit
    /// on, this frame is the whole cosmetic — so it is the author's choice and not the renderer's.
    /// </remarks>
    public int? Still { get; set; }
}

/// <summary>
/// One figure travelling a ring around a face.
/// </summary>
/// <remarks>
/// <para><b>A ring on a sphere, described by where its plane lies rather than by a shape on the
/// screen.</b> An ellipse and a phase would draw the same picture and would not say which half of
/// it is nearer — and which half is nearer is the whole of it, because that is what decides when
/// the face hides the figure and when the figure covers the face.</para>
///
/// <para>So the path is a circle of <see cref="RadiusPct"/> about the centre of the face, its plane
/// tipped out of the screen by <see cref="TiltDeg"/> and leaned by <see cref="YawDeg"/>. Everything
/// the client draws — the ellipse, the depth, which half passes behind — falls out of those three.
/// </para>
///
/// <para>One flat type with a discriminator, as a frame's parts are, so that a second sort of thing
/// in the ring can be added later without inventing a rule for interleaving two lists.</para>
///
/// <para>No data annotations here on purpose, for the same reason as the sprite above — every
/// number is checked by hand in <see cref="AvatarOrbitPayload"/>.</para>
/// </remarks>
public sealed class CosmeticOrbitSatellite
{
    /// <summary><c>satellite</c>, and so far only that.</summary>
    public string? Type { get; set; }

    /// <summary>The asset slot holding this figure's picture.</summary>
    public string? Slot { get; set; }

    public CosmeticOrbitSprite? Sprite { get; set; }

    /// <summary>How far from the centre of the face it travels, as a percentage of the face.</summary>
    /// <remarks>
    /// <para>Fifty is the edge of the face. Below that the figure spends its near half over the face
    /// and its far half entirely hidden, which is a thing to do deliberately and not by accident.
    /// </para>
    ///
    /// <para>Past fifty plus half its own width is the number that matters, and it is not obvious
    /// why. The figure crosses from behind the face to in front of it at the two points where it is
    /// furthest to the side — and if any of it is over the face at that moment, that part of it
    /// appears in one frame. Clear of the face there, and the crossing cannot be seen at all.</para>
    /// </remarks>
    public double? RadiusPct { get; set; }

    /// <summary>
    /// How far the ring is tipped out of the screen, in degrees.
    /// </summary>
    /// <remarks>
    /// <para>Zero is a halo lying flat against the screen: a full circle, nothing ever behind
    /// anything. Ninety is edge-on: the figure runs a straight line across the face, half of it
    /// behind.</para>
    ///
    /// <para>Both halves of the range are worth having, and they are different cosmetics. A shallow
    /// tilt is an ellipse tall enough to clear the head — a figure circling a face. Past sixty the
    /// ellipse is flatter than the face, so the figure crosses the face itself on its near half and
    /// goes behind the head on its far one: on the avatar rather than around it, which is the whole
    /// reason for a kind that can put something behind a head.</para>
    /// </remarks>
    public double? TiltDeg { get; set; }

    /// <summary>How far the ring is leaned over on the screen, in degrees.</summary>
    public double? YawDeg { get; set; }

    /// <summary>How far that lean rocks either side of itself, in degrees.</summary>
    /// <remarks>
    /// <para><b>What turns one circle into a sphere.</b> A figure on a fixed ring walks the same
    /// line for ever. Rocking the ring sweeps that line across the face and back, so one figure
    /// covers the whole of it — and it leans with the ring, which is what anything walking on a
    /// curved surface does.</para>
    ///
    /// <para>A rock rather than a turn all the way round: a ring that kept going would carry the
    /// figure upside down and hold it there for a quarter of every sweep.</para>
    /// </remarks>
    public double? YawSwingDeg { get; set; }

    /// <summary>How long one rock takes.</summary>
    public int? YawSwingMs { get; set; }

    /// <summary>How long one circuit takes.</summary>
    public int? PeriodMs { get; set; }

    /// <summary>
    /// How far along the circuit it starts.
    /// </summary>
    /// <remarks>
    /// <b>The difference between two figures and one figure drawn twice.</b> Two satellites sharing
    /// a period and a phase are one picture moving; giving each a phase of its own is two creatures.
    /// </remarks>
    public int? PhaseMs { get; set; }

    /// <summary>Which way round it goes.</summary>
    public bool? Reverse { get; set; }

    /// <summary>
    /// Where on the circuit it stands when nothing is moving, in degrees.
    /// </summary>
    /// <remarks>
    /// Ninety puts it at the nearest point, in front and below the face, which is the pose that
    /// reads as a figure rather than as a smudge beside a head. It is what a reduced-motion
    /// preference sees, and what every face too small to animate shows — so it is worth choosing.
    /// </remarks>
    public double? StillDeg { get; set; }

    /// <summary>How wide the figure is drawn, as a percentage of the face.</summary>
    public double? WidthPct { get; set; }

    /// <summary>How tall the figure is drawn, as a percentage of the face.</summary>
    public double? HeightPct { get; set; }

    public double? Opacity { get; set; }

    /// <summary>How far the figure is tilted on the spot, in degrees — leaning into the turn.</summary>
    public double? LeanDeg { get; set; }

    /// <summary>
    /// Whether the picture is mirrored to face the way it is going.
    /// </summary>
    /// <remarks>
    /// For anything with a front. A cat drawn facing right runs backwards for half of every circuit
    /// without this, and there is no arrangement of the other numbers that fixes it.
    /// </remarks>
    public bool? FaceTravel { get; set; }

    /// <summary>How it turns round when it does: <c>mirror</c> or <c>spin</c>.</summary>
    /// <remarks>
    /// <b>Which one is right follows from how the picture was drawn, not from taste.</b> A creature
    /// seen from the side turns round by being mirrored. One seen from above does not — mirrored, a
    /// spider pointing down a vertical ring still points down, and it crawls backwards for half of
    /// every lap. <c>spin</c> turns it through half a circle, which points it back the way it came.
    /// </remarks>
    public string? Turn { get; set; }

    /// <summary>How big the figure is at the nearest point of its travel, as a multiplier.</summary>
    public double? NearScale { get; set; }

    /// <summary>How big it is at the farthest point.</summary>
    /// <remarks>
    /// Near and far rather than a camera distance, because these are the two numbers an author can
    /// actually see. The client moves between them along the circuit, and the ring's own width
    /// follows — which is what a ring seen in perspective does.
    /// </remarks>
    public double? FarScale { get; set; }

    /// <summary>How much of its brightness it loses at the farthest point, from 0 to 1.</summary>
    public double? Dim { get; set; }

    /// <summary>How much of its opacity it loses at the farthest point, from 0 to 1.</summary>
    public double? Haze { get; set; }

    /// <summary>How far out of focus it goes at the farthest point, as a percentage of the face.</summary>
    public double? BlurPct { get; set; }

    /// <summary>
    /// Whether the face hides it while it passes behind.
    /// </summary>
    /// <remarks>
    /// On for anything solid, and the reason this kind exists at all. Off for a thing that is meant
    /// to be a light rather than an object — a will-o'-the-wisp reads as a ghost precisely because
    /// the head does not stop it.
    /// </remarks>
    public bool? Occlude { get; set; }

    /// <summary>How far it rises and falls as it goes, as a percentage of the face.</summary>
    public double? BobPct { get; set; }

    /// <summary>
    /// How long one rise and fall takes.
    /// </summary>
    /// <remarks>
    /// Deliberately its own period rather than a fraction of the circuit. A bounce that divides the
    /// lap evenly lands in the same place every time round, and the eye finds that immediately.
    /// </remarks>
    public int? BobPeriodMs { get; set; }
}

/// <summary>
/// Figures travelling a ring around a face, passing behind it and in front of it.
/// </summary>
/// <remarks>
/// <para><b>Not an avatar decoration with more fields.</b> A decoration is one picture composited
/// with the face at a size — there is no arrangement of an inset and a flag that puts half of a
/// picture behind a head and the other half in front of it, because the picture is one layer and a
/// layer is on one side. This is a list of bodies with positions in three dimensions, and the thing
/// it can express that nothing else can is occlusion.</para>
///
/// <para>Which is also why it is a kind of its own and not a reshape of that one: both are worth
/// wearing, they stack, and a ring of thorns that never moves should not have to carry twenty
/// numbers about depth to say so.</para>
/// </remarks>
public sealed class AvatarOrbitPayload : IValidatableCosmeticPayload
{
    public CosmeticOrbitSatellite[]? Satellites { get; set; }

    /// <summary>
    /// The size of face below which the whole arrangement stands still, in pixels.
    /// </summary>
    /// <remarks>
    /// <para>Zero, and nothing, unless somebody decides otherwise: an arrangement moves wherever it
    /// is drawn. At or below the number the client draws the frame at <c>stillDeg</c> and runs
    /// nothing at all.</para>
    ///
    /// <para><b>It is here because a member list draws hundreds of faces at once.</b> Each figure
    /// costs a handful of composited animations, two of which — the strip and the blur — are paint
    /// rather than transform, so a window full of people wearing this is a list that never settles.
    /// The sizes it decides between are the product's own: a message head is 36 pixels, a member row
    /// 34, and the smallest face anywhere 22.</para>
    ///
    /// <para>A number rather than a rule in the client, because where the line falls depends on what
    /// was drawn: one bright shape survives smaller than a bird with two wings. And defaulting it to
    /// nothing rather than to something, because a renderer that quietly stopped animating would be
    /// a cosmetic that is live, correct and invisible — the failure <see cref="CosmeticKindGate"/>
    /// refuses for a kind's own switch, for the same reason.</para>
    /// </remarks>
    public int? MinSizePx { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Satellites is not { Length: > 0 })
        {
            report.Error("satellites needs at least one figure for there to be an orbit");
            return;
        }

        if (Satellites.Length > CosmeticOrbitLimits.MaxSatellites)
            report.Error($"satellites is capped at {CosmeticOrbitLimits.MaxSatellites}, which is what the console's own list holds");

        Bound(report, MinSizePx, "minSizePx", 0, CosmeticOrbitLimits.MaxMinSizePx);

        for (var index = 0; index < Satellites.Length; index++)
        {
            ValidateSatellite(report, Satellites[index], index);
        }
    }

    private static void ValidateSatellite(ICosmeticPayloadReport report, CosmeticOrbitSatellite satellite, int index)
    {
        var at = $"satellites[{index}]";

        if (satellite.Type is not "satellite")
        {
            report.Error($"{at}.type is 'satellite'");
            return;
        }

        RequireSlot(report, satellite, at);

        var radius = Require(report, satellite.RadiusPct, $"{at}.radiusPct", 0d, CosmeticOrbitLimits.MaxRadiusPct);
        var width  = Require(report, satellite.WidthPct, $"{at}.widthPct", 1d, CosmeticOrbitLimits.MaxSizePct);
        var height = Require(report, satellite.HeightPct, $"{at}.heightPct", 1d, CosmeticOrbitLimits.MaxSizePct);

        Require(report, satellite.PeriodMs, $"{at}.periodMs", CosmeticOrbitLimits.MinPeriodMs, CosmeticOrbitLimits.MaxPeriodMs);

        Bound(report, satellite.TiltDeg, $"{at}.tiltDeg", 0d, 90d);
        Bound(report, satellite.YawDeg, $"{at}.yawDeg", -180d, 180d);
        Bound(report, satellite.YawSwingDeg, $"{at}.yawSwingDeg", 0d, 180d);

        Bound(report, satellite.YawSwingMs, $"{at}.yawSwingMs",
            CosmeticOrbitLimits.MinPeriodMs, CosmeticOrbitLimits.MaxPeriodMs);

        Bound(report, satellite.PhaseMs, $"{at}.phaseMs", -CosmeticOrbitLimits.MaxPeriodMs, CosmeticOrbitLimits.MaxPeriodMs);
        Bound(report, satellite.StillDeg, $"{at}.stillDeg", 0d, 360d);

        Bound(report, satellite.Opacity, $"{at}.opacity", 0.05d, 1d);
        Bound(report, satellite.LeanDeg, $"{at}.leanDeg", -180d, 180d);

        if (satellite.Turn is { Length: > 0 } and not ("mirror" or "spin"))
            report.Error($"{at}.turn is 'mirror' or 'spin'");

        var near = Bound(report, satellite.NearScale, $"{at}.nearScale",
            CosmeticOrbitLimits.MinDepthScale, CosmeticOrbitLimits.MaxDepthScale);

        var far = Bound(report, satellite.FarScale, $"{at}.farScale",
            CosmeticOrbitLimits.MinDepthScale, CosmeticOrbitLimits.MaxDepthScale);

        Bound(report, satellite.Dim, $"{at}.dim", 0d, 1d);
        Bound(report, satellite.Haze, $"{at}.haze", 0d, 1d);
        Bound(report, satellite.BlurPct, $"{at}.blurPct", 0d, CosmeticOrbitLimits.MaxBlurPct);

        var bob = Bound(report, satellite.BobPct, $"{at}.bobPct", 0d, CosmeticOrbitLimits.MaxBobPct);

        Bound(report, satellite.BobPeriodMs, $"{at}.bobPeriodMs",
            CosmeticOrbitLimits.MinPeriodMs, CosmeticOrbitLimits.MaxPeriodMs);

        ValidateSprite(report, satellite.Sprite, $"{at}.sprite");

        ReportReach(report, at, satellite, radius, width, height, bob, Math.Max(near ?? 1d, far ?? 1d));
    }

    /// <summary>
    /// How far past the edge of the face the whole figure gets, at its largest.
    /// </summary>
    /// <remarks>
    /// The diagonal only for a figure that turns, because only a box that turns ever presents a
    /// corner. One that lies square to the screen never reaches past half its own side, and charging
    /// it for a corner it does not have put half again as much air around every ornament that only
    /// ever mirrors. The same arithmetic the client does to work out how much room to reserve, kept
    /// here because this is where a row is refused rather than quietly clipped.
    /// </remarks>
    private static void ReportReach(
        ICosmeticPayloadReport report,
        string at,
        CosmeticOrbitSatellite satellite,
        double? radius,
        double? width,
        double? height,
        double? bob,
        double scale)
    {
        if (radius is not { } distance || width is not { } across || height is not { } down)
            return;

        var turns = satellite.FaceTravel is not false && satellite.Turn is "spin";
        var leans = satellite.LeanDeg is not (null or 0d);

        var half = turns || leans
            ? Math.Sqrt((across * across) + (down * down)) / 2d
            : Math.Max(across, down) / 2d;

        var reach = ((distance + half + (bob ?? 0d)) * scale) - 50d;

        if (reach > CosmeticOrbitLimits.MaxReachPct)
        {
            report.Error(
                $"{at} reaches {reach:0} per cent of the face past its edge, and " +
                $"{CosmeticOrbitLimits.MaxReachPct:0} is as far as an ornament goes");
        }
    }

    private static void ValidateSprite(ICosmeticPayloadReport report, CosmeticOrbitSprite? sprite, string at)
    {
        if (sprite is null)
            return;

        var frames  = Require(report, sprite.Frames, $"{at}.frames", 2, CosmeticOrbitLimits.MaxSpriteFrames);
        var columns = Require(report, sprite.Columns, $"{at}.columns", 1, CosmeticOrbitLimits.MaxSpriteFrames);

        Bound(report, sprite.Fps, $"{at}.fps", 1, 60);

        if (frames is null || columns is null)
            return;

        // The strip is read as a grid, so a row that runs out halfway would play a blank frame.
        if (columns > frames || frames % columns is not 0)
            report.Error($"{at}.frames must divide evenly into rows of {columns}");

        if (sprite.Still is { } still && (still < 0 || still >= frames))
            report.Error($"{at}.still names frame {still}, and the strip has {frames}");
    }

    private static void RequireSlot(ICosmeticPayloadReport report, CosmeticOrbitSatellite satellite, string at)
    {
        if (satellite.Slot is not { Length: > 0 } slot)
        {
            report.Error($"{at}.slot names no file, and a satellite is a picture");
            return;
        }

        if (!Enum.TryParse<CosmeticAssetSlot>(slot, out _))
            report.Error($"{at}.slot '{slot}' is not an asset slot");
    }

    private static double? Require(ICosmeticPayloadReport report, double? value, string at, double low, double high)
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

    private static double? Bound(ICosmeticPayloadReport report, double? value, string at, double low, double high)
    {
        if (value is not { } number)
            return null;

        if (number < low || number > high)
        {
            report.Error($"{at} is {number}, and the range is {low} to {high}");
            return null;
        }

        return number;
    }
}
