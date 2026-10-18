namespace Argon.Features.Cosmetics;

using ion.runtime;

/// <summary>
/// What a scene's geometry is held to.
/// </summary>
/// <remarks>
/// A scene is bounded by the card it plays on, so most of these are about cost rather than reach:
/// how many things may move at once, and how far a number may be from something a person drew.
/// </remarks>
public static class CosmeticSceneLimits
{
    /// <summary>
    /// Enough for the busiest reference — a figure, a second figure, a prop and a scatter — and few
    /// enough that a card is still a card.
    /// </summary>
    public const int MaxActors = 8;

    /// <summary>
    /// Copies one emitter may put on a card.
    /// </summary>
    /// <remarks>
    /// Each is an element with four animations on it, so this is the one number here that is a
    /// budget rather than a taste. Forty-eight falling flakes on a card is already more than any of
    /// the references use.
    /// </remarks>
    public const int MaxEmitterCount = 48;

    /// <summary>Stops in one envelope. Past this it is not a curve, it is a track.</summary>
    public const int MaxStops = 8;

    public const double MaxSizePct   = 400d;
    public const double MaxOffsetPct = 400d;
    public const double MaxOutsetPct = 25d;
    public const double MaxScale     = 8d;
    public const double MaxRotateDeg = 3600d;

    /// <summary>
    /// How far a drop shadow is thrown, and how far it is spread, in per cent of the card's width.
    /// </summary>
    /// <remarks>
    /// The reach is capped rather than the cost. A shadow half a card away is further than any light
    /// source a card can imply; the expense is the filter itself, which is why the field is opt-in
    /// rather than defaulted.
    /// </remarks>
    public const double MaxShadowOffsetPct = 50d;

    public const double MaxShadowBlurPct = 50d;

    /// <summary>
    /// Pitch amplitude of a fluttering copy. Ninety degrees is edge-on, past which it is simply the
    /// other face of the same drawing.
    /// </summary>
    public const double MaxFlutterDeg = 90d;

    /// <summary>The rise and fall of a wingbeat or an edge-on pass, in per cent of the card's width.</summary>
    public const double MaxBobPct = 25d;

    /// <summary>How far a sprite's path bows off its own chord.</summary>
    public const double MaxBowPct = 200d;

    /// <summary>A sprite's lean into its own arc. Half a turn each way covers every lean there is.</summary>
    public const double MaxBankDeg = 180d;

    /// <summary>
    /// How far a rooted thing leans — the wobble on the way up and the breathing afterwards.
    /// </summary>
    public const double MaxGrowDeg = 45d;

    /// <summary>The strip a retreat keeps along a side or the foot, in per cent of the card's width. Half from each side is the whole card.</summary>
    public const double MaxRetreatStripPct = 50d;

    /// <summary>How soft the edge of a kept strip is. Past a quarter of the card it is a fade, not an edge.</summary>
    public const double MaxRetreatSoftPct = 25d;

    public const int MaxSlice        = 512;
    public const int MaxSpriteFrames = 64;
    public const int MaxAtlasPx      = 4096;

    public const int MaxPeriodMs = 600_000;
    public const int MinPeriodMs = 240;

    /// <summary>Below this width a card is a tile in a picker, and a scene on it is a smear.</summary>
    public const int MaxMinWidthPx = 512;
}

/// <summary>
/// One stop of an envelope: where along the actor's own cycle, and what the channel is worth there.
/// </summary>
/// <remarks>
/// <para><b>An envelope is a shape, not a track.</b> It says how a single number moves between its
/// own smallest and largest value over one cycle — nothing here names a property, a unit or a
/// duration, because those belong to the channel it is attached to.</para>
///
/// <para>No data annotations on purpose: see <c>CosmeticFramePart</c>. Every number below is checked
/// by hand in <see cref="ProfileScenePayload"/>.</para>
/// </remarks>
public sealed class CosmeticSceneStop
{
    /// <summary>Where along the cycle, 0 to 1.</summary>
    public double? At { get; set; }

    /// <summary>What the channel is worth here.</summary>
    public double? V { get; set; }
}

/// <summary>
/// A rectangle of the scene's atlas, and how to walk it if it is a strip.
/// </summary>
/// <remarks>
/// <b>One file per scene rather than one per actor.</b> Asset slots are a closed set of six shared
/// by every kind, and the busiest reference wants five different drawings — so the drawings are
/// rectangles of one picture, which is also one request instead of five.
/// </remarks>
public sealed class CosmeticSceneAtlas
{
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? W { get; set; }
    public int? H { get; set; }

    /// <summary>Frames in the strip. Absent or one — an ordinary picture.</summary>
    public int? Frames { get; set; }

    public int? Columns { get; set; }
    public int? Fps { get; set; }

    /// <summary>
    /// The frame to stand on when movement is off. Named rather than assumed to be the first, for
    /// the same reason a frame's sprite names it: a strip's first frame is often mid-stride.
    /// </summary>
    public int? Still { get; set; }
}

/// <summary>The pixel size of the sheet every atlas rectangle is cut from.</summary>
/// <remarks>No data annotations, for the reason stated on <see cref="CosmeticSceneStop"/>.</remarks>
public sealed class CosmeticSceneSheet
{
    public int? W { get; set; }
    public int? H { get; set; }
}

/// <summary>
/// Where on the stage something is, as a point of the card plus a nudge.
/// </summary>
/// <remarks>
/// <para><b>The nudge is a percentage of the card's width, and so is every other size here.</b> A
/// card is 384px wide in a popover and 320px in settings, and its height is whatever its owner's
/// bio, roles and board come to — up to sixteen rows of them. A percentage of the height would
/// stretch a figure by however much somebody else's board is stretching the card.</para>
///
/// <para>The anchor is where the actor's <i>centre</i> goes.</para>
/// </remarks>
public sealed class CosmeticScenePoint
{
    public string? Anchor { get; set; }

    /// <summary>Per cent of the card's width, right positive.</summary>
    public double? DxPct { get; set; }

    /// <summary>Per cent of the card's width, down positive.</summary>
    public double? DyPct { get; set; }
}

/// <summary>
/// The permanent lean a grown thing keeps once it has finished growing.
/// </summary>
/// <remarks>
/// <b>The cheapest biology there is.</b> A tree that finishes growing and then stands perfectly
/// rigid reads as a sticker of a tree. Two numbers — how far it leans and how long a lean takes —
/// are the whole of the difference.
/// </remarks>
public sealed class CosmeticSceneSway
{
    public double? Deg { get; set; }
    public int? Ms { get; set; }
}

/// <summary>
/// How something grows into place: extended out of a root, and uncovered as it goes.
/// </summary>
/// <remarks>
/// <para>The reveal was once the whole of it, because the thing this exists for is flowers growing:
/// a band that scaled up would inflate, and a band that is uncovered grows.</para>
///
/// <para>A mask alone turned out to be a wipe — it uncovers a picture that is already at full size,
/// so a tree at half growth is the bottom half of an adult tree rather than a small whole tree. So
/// the reveal is now one of four channels about the point <see cref="From"/> names: extension out of
/// <see cref="FromScale"/>, a decaying <see cref="WobbleDeg"/> while it extends, the reveal itself,
/// and <see cref="Sway"/> for ever afterwards. Equal ends install no reveal, and the grow is then
/// its extension, wobble, duration and breathing.</para>
/// </remarks>
public sealed class CosmeticSceneGrow
{
    public double? FromPct { get; set; }
    public double? ToPct { get; set; }
    public int? DurationMs { get; set; }

    /// <summary>How long it stands finished before going round again.</summary>
    public int? HoldMs { get; set; }

    public string? Repeat { get; set; }

    /// <summary>
    /// How small it starts, as a fraction of its finished size.
    /// </summary>
    /// <remarks>
    /// <c>1</c> restores a pure reveal — the old behaviour, kept reachable because a band swept in
    /// from its own edge is still the right thing for a border.
    /// </remarks>
    public double? FromScale { get; set; }

    /// <summary><c>y</c>, <c>x</c> or <c>both</c> — which axis extends ahead of the other.</summary>
    public string? LeadAxis { get; set; }

    /// <summary>A rotation that decays over the extension. The decay shape is code; this is its amplitude.</summary>
    public double? WobbleDeg { get; set; }

    /// <summary>
    /// How soft the reveal's edge is, 0 to 1.
    /// </summary>
    /// <remarks>
    /// A hard mask edge on a radial reveal is an iris ring crossing the picture. Softening it is what
    /// makes the same animation read as something spreading rather than something being uncovered.
    /// </remarks>
    public double? Softness { get; set; }

    public CosmeticSceneSway? Sway { get; set; }

    /// <summary>
    /// Where the reveal starts: <c>edge</c>, or one of the nine anchor names.
    /// </summary>
    /// <remarks>
    /// <b>The difference between a shutter and a thing that grows.</b> Swept in from the side it lies
    /// on, a band arrives in layers — which is what a blind does. Spread out of a point, the same
    /// picture reads as something creeping over the card from where it is rooted, because that is
    /// what growth looks like: it radiates from one place.
    ///
    /// <c>edge</c>, the default, sweeps from the edge the actor lies on. An anchor makes it radial
    /// from that point of the stage.
    /// </remarks>
    public string? From { get; set; }
}

/// <summary>
/// A shadow thrown by the actor's own drawing.
/// </summary>
/// <remarks>
/// <b>A filter over the alpha, not a rectangle under the box.</b> It follows the shape that is
/// actually drawn, so a tree throws a tree-shaped shadow and a petal a petal-shaped one — which is
/// the only version of this worth having on a picture cut out of a sheet.
///
/// Opt-in, and absent by default, because a filter is a layer: it is the one thing here whose cost
/// is paid per actor per card, on a member list drawing dozens of cards at once.
/// </remarks>
public sealed class CosmeticSceneShadow
{
    /// <summary>Per cent of the card's width, right positive.</summary>
    public double? DxPct { get; set; }

    /// <summary>Per cent of the card's width, down positive.</summary>
    public double? DyPct { get; set; }

    public double? BlurPct { get; set; }

    public double? Alpha { get; set; }
}

/// <summary>
/// The actor withdrawing from the card's reading zone, and holding there.
/// </summary>
/// <remarks>
/// <para><b>What it keeps is what covers no text.</b> After <see cref="AtMs"/>, over
/// <see cref="DurationMs"/>, the actor withdraws from everything below the glass line and inside the
/// strips, and holds there; what is above the glass line, along the strips and past the card's edge
/// stays. So a picture may take the whole card while it plays and still leave the card readable
/// once it has.</para>
///
/// <para>No data annotations, for the reason stated on <see cref="CosmeticSceneStop"/>.</para>
/// </remarks>
public sealed class CosmeticSceneRetreat
{
    /// <summary>When it starts, from the card's opening.</summary>
    public int? AtMs { get; set; }

    /// <summary>How long the withdrawal takes.</summary>
    public int? DurationMs { get; set; }

    /// <summary>The strip along each side it keeps, in per cent of the card's width.</summary>
    public double? EdgePct { get; set; }

    /// <summary>The strip along the stage's bottom edge it keeps, in per cent of the card's width.</summary>
    public double? FootPct { get; set; }

    /// <summary>How soft the strips' edges are, in per cent of the card's width.</summary>
    public double? SoftPct { get; set; }

    /// <summary>
    /// How much of it stays over the reading zone, 0 to 1.
    /// </summary>
    /// <remarks>
    /// Below 1 by refusal rather than by range: a whole passes every range and changes nothing,
    /// which on a card is a retreat somebody set and will wait for.
    /// </remarks>
    public double? Remain { get; set; }
}

/// <summary>
/// One thing on a scene.
/// </summary>
/// <remarks>
/// <para><b>One flat type with a discriminator, rather than one per sort.</b> The same reason a
/// frame's parts are one list: the order they are listed in is the order they are drawn in, and that
/// order is a property of the scene. Typed lists would have to invent a rule for interleaving them.</para>
///
/// <para>The cost is that every actor carries every field, so a <c>band</c> could be handed a
/// <c>durationMs</c>. <see cref="ProfileScenePayload"/> refuses that by name rather than ignoring
/// it — a field that does nothing is an operator who believes it does something.</para>
///
/// <para>No data annotations here, for the reason stated on <see cref="CosmeticSceneStop"/>.</para>
/// </remarks>
public sealed class CosmeticSceneActor
{
    /// <summary><c>wash</c>, <c>sprite</c>, <c>band</c>, <c>wrap</c> or <c>emitter</c>.</summary>
    public string? Type { get; set; }

    /// <summary>Which uploaded file this actor draws. Named as the asset slot is.</summary>
    public string? Slot { get; set; }

    /// <summary><c>atlas</c> — a rectangle of the sheet; <c>file</c> — the slot's whole picture.</summary>
    public string? Source { get; set; }

    public CosmeticSceneAtlas? Atlas { get; set; }

    /// <summary>
    /// A self-animating file starts afresh every time the card is shown.
    /// </summary>
    /// <remarks>
    /// Only a file has a clock of its own. A rectangle of the sheet is moved by this code's
    /// animations, which begin with the card whatever this says, so it is refused there.
    /// </remarks>
    public bool? Replay { get; set; }

    /// <summary>
    /// Which band of the card this is drawn in: <c>deep</c>, <c>over</c> or <c>front</c>.
    /// </summary>
    /// <remarks>
    /// <b>Depth is data here rather than a layer on the kind.</b> Whether a figure passes in front
    /// of a worn frame's thorns or behind them is a property of that figure — a snowman rolling over
    /// the card and the flakes falling behind its glass are one scene and two answers. A kind-wide
    /// layer could only give one.
    /// </remarks>
    public string? Depth { get; set; }

    /// <summary>Parts of the card that hide this actor. Only <c>avatar</c> today.</summary>
    public string[]? Occlude { get; set; }

    public int? DelayMs { get; set; }

    public CosmeticSceneStop[]? Opacity { get; set; }

    public CosmeticSceneShadow? Shadow { get; set; }

    /// <summary>Withdrawing from the card's reading zone after a while, and holding there. Every sort but a scatter.</summary>
    public CosmeticSceneRetreat? Retreat { get; set; }

    // wash

    /// <summary><c>cover</c>, <c>contain</c> or <c>stretch</c>.</summary>
    public string? Fit { get; set; }

    public int? PeriodMs { get; set; }

    // sprite and emitter

    /// <summary>Per cent of the card's width.</summary>
    public double? SizePct { get; set; }

    public int? DurationMs { get; set; }

    /// <summary>
    /// A rise and fall, in per cent of the card's width.
    /// </summary>
    /// <remarks>
    /// <b>One name, two periods, and neither of them is here.</b> On a sprite it rides the wingbeat,
    /// whose period is the atlas' own <c>frames / fps</c> so that it cannot drift out of step with
    /// the drawing. On an emitter it is the dip at each edge-on pass of the flutter, which comes
    /// twice a swing. Both are read off something the actor already carries, so neither is data.
    /// </remarks>
    public double? BobPct { get; set; }

    // wash, sprite and emitter

    /// <summary>
    /// <c>loop</c>, <c>once</c> or <c>pingPong</c>.
    /// </summary>
    /// <remarks>
    /// <b><c>once</c> is what makes a scene that ends.</b> A card somebody opens can play a thing
    /// through and then simply be decorated — the branch finished growing, the petals came to rest —
    /// and that is a different sort of cosmetic from weather, which is the only sort a looping
    /// actor can be. It lives on the actor rather than on the scene because the two mix: petals that
    /// settle, over a sky that keeps moving.
    /// </remarks>
    public string? Repeat { get; set; }

    // sprite

    public CosmeticScenePoint? From { get; set; }
    public CosmeticScenePoint? To { get; set; }

    /// <summary><c>linear</c>, <c>in</c>, <c>out</c> or <c>inOut</c>.</summary>
    public string? Ease { get; set; }

    /// <summary><c>none</c> or <c>mirror</c>. A figure with no front takes <c>none</c>.</summary>
    public string? Turn { get; set; }

    public CosmeticSceneStop[]? Rotate { get; set; }

    /// <summary>How far the path arcs off the straight line between its two anchors.</summary>
    public double? BowPct { get; set; }

    /// <summary>
    /// <c>auto</c>, <c>x</c> or <c>y</c> — which axis the bow displaces.
    /// </summary>
    /// <remarks>
    /// <b>Named because the perpendicular is not computable.</b> An anchor's Y is in <c>cqh</c> and
    /// its X in <c>cqw</c>, so the true normal to the chord needs the card's aspect ratio, which
    /// nothing knows at draw time. <c>auto</c> takes the larger anchor delta, which is right for
    /// most paths and wrong for a diagonal on an unusually tall card; this is the override.
    /// </remarks>
    public string? BowAxis { get; set; }

    /// <summary>How far it leans into its own arc: nose up on entry, level at the apex, down on exit.</summary>
    public double? BankDeg { get; set; }

    // sprite and wash

    public CosmeticSceneStop[]? Scale { get; set; }

    /// <summary>
    /// How far past the card's edge the actor may show, in per cent of the card's width. Zero clips
    /// at the card.
    /// </summary>
    public double? SpillPct { get; set; }

    // band and emitter

    /// <summary><c>top</c>, <c>right</c>, <c>bottom</c> or <c>left</c>.</summary>
    public string? Edge { get; set; }

    // band and wrap

    /// <summary>Where to cut the source picture, in its own pixels: top, right, bottom, left.</summary>
    public int[]? Slice { get; set; }

    /// <summary>How far past the stage's own edge it reaches.</summary>
    public double? OutsetPct { get; set; }

    /// <summary><c>stretch</c>, <c>repeat</c>, <c>round</c> or <c>space</c>.</summary>
    public string? Tile { get; set; }

    public CosmeticSceneGrow? Grow { get; set; }

    // band

    /// <summary>How thick the band is, as a per cent of the card's width.</summary>
    public double? ThicknessPct { get; set; }

    /// <summary>
    /// How wide the pieces at the two ends of a band are drawn, in per cent of the card's width.
    /// </summary>
    /// <remarks>
    /// <b>Stated rather than taken from the slice.</b> The slice is in the source picture's pixels
    /// and this is on the card, and nothing knows how big the source is until it has loaded — which
    /// is after the band has been drawn. It defaults to the band's own thickness, which is right for
    /// a border cut from a square tile and wrong for anything else.
    /// </remarks>
    public double? CornerPct { get; set; }

    // wrap

    /// <summary>
    /// How thick the band is on each side, in per cent of the card's width: top, right, bottom, left.
    /// </summary>
    /// <remarks>
    /// <b>This is where a wrap's shape comes from.</b> All the way round, down two sides only, open
    /// at the top — every one of those is which of these four numbers are not zero. There is no shape
    /// field, because a shape is not a thing to choose from a list. (The same sentence is true of
    /// <c>ProfileFramePayload</c>'s surround, and for the same reason.)
    ///
    /// Per cent of the WIDTH on every side, including the top and bottom ones: a card's height is
    /// whatever its owner's bio and board come to, and a thickness that followed it would be a
    /// different cosmetic on every profile.
    /// </remarks>
    public double[]? WidthPct { get; set; }

    // emitter

    public int? Count { get; set; }

    /// <summary>
    /// What the scatter is worked out from.
    /// </summary>
    /// <remarks>
    /// <b>The scatter is deterministic and this is why.</b> Everybody looking at the same profile
    /// sees the same flakes in the same places, every preview of the row matches the card, and a
    /// screenshot test is reproducible. <c>Math.random()</c> would cost all three for nothing.
    /// </remarks>
    public int? Seed { get; set; }

    public double? SizeVarPct { get; set; }
    public int? DurationVarMs { get; set; }

    /// <summary>How far a copy may wander across its own travel.</summary>
    public double? DriftPct { get; set; }

    public double? SwayPct { get; set; }
    public int? SwayPeriodMs { get; set; }

    /// <summary>
    /// How much the sway's period varies from copy to copy.
    /// </summary>
    /// <remarks>
    /// <b>The unison killer.</b> One period shared by every copy makes the whole field pulse
    /// together, which is the single loudest tell that a scatter is forty copies of one animation
    /// rather than forty things falling.
    /// </remarks>
    public int? SwayVarMs { get; set; }

    public double? SpinDeg { get; set; }

    /// <summary>
    /// Pitch amplitude of the flutter. Zero is the flat drift a scatter had before there was one.
    /// </summary>
    /// <remarks>
    /// A thing in the fluttering regime is fastest sideways exactly when it is edge-on and stalls
    /// face-on, so the pitch runs ninety degrees out of phase with the sway rather than alongside
    /// it. The phase is code; this is how far it goes. The tilt axis is drawn per copy, so tumbling
    /// needs no field of its own — it is what an off-horizontal axis already looks like.
    /// </remarks>
    public double? FlutterDeg { get; set; }

    /// <summary>
    /// How strongly a copy's size drives its speed and its opacity.
    /// </summary>
    /// <remarks>
    /// The size draw already varies per copy, so depth costs no second draw: a larger copy is nearer,
    /// and so falls faster and sits denser. Zero leaves a field of copies that are all the same
    /// distance away, which is what makes one read as a sheet.
    /// </remarks>
    public double? DepthSpreadPct { get; set; }
}

/// <summary>
/// What the wearer decided about a scene, which is one thing: how much of their card it plays on.
/// </summary>
/// <remarks>
/// Offered only when the row says <c>choice</c>. A scene drawn for the part of a card above the
/// board looks broken on a card the board has stretched, and the person who drew it is the one who
/// knows which of those it is.
/// </remarks>
public sealed class ProfileSceneTuning : IValidatableCosmeticPayload
{
    public string? Reach { get; set; }

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Reach is { Length: > 0 } and not ("content" or "card"))
            report.Error("reach is either 'content' or 'card'");
    }
}

/// <summary>
/// Things moving on somebody's whole profile card: a list of actors, drawn in the order they are
/// listed within each band of depth.
/// </summary>
/// <remarks>
/// <para>Everything an actor carries is a number, a name from a closed list, or the name of an
/// uploaded file — which is what keeps a new scene a row somebody creates in the console rather
/// than a release. The client holds the vocabulary: what a <c>band</c> is, how a path is walked,
/// what <c>deep</c> means. This holds the amounts.</para>
///
/// <para>The card's own height is not one of those amounts and cannot be: it is whatever the wearer's
/// bio, roles and board come to. Every size here is a per cent of the card's <i>width</i>, and
/// everything else is pinned to an edge.</para>
/// </remarks>
public sealed class ProfileScenePayload : IValidatableCosmeticPayload, ICosmeticWirePayload
{
    public string? Reach { get; set; }

    public string? DefaultReach { get; set; }

    /// <summary>Below this card width the scene is not drawn at all.</summary>
    public int? MinWidthPx { get; set; }

    /// <summary>
    /// The pixel size of the sheet in <c>Primary</c>.
    /// </summary>
    /// <remarks>
    /// <b>Declared rather than measured, because the thing that needs it is CSS.</b> Cutting one
    /// rectangle out of a sheet is a background size and a background offset, and both are ratios
    /// between the sheet and the cell — a number the browser cannot be asked for before it has
    /// loaded the picture, and the renderer draws before that. The same reason a nine-slice's
    /// <c>slice</c> is in the source's own pixels.
    /// </remarks>
    public CosmeticSceneSheet? Sheet { get; set; }

    public CosmeticSceneActor[]? Actors { get; set; }

    /// <summary>
    /// The scene as Ion carries it: one typed case per sort of actor, so a field one sort refuses is
    /// a field its case does not have.
    /// </summary>
    public ICosmeticPayload ToWire() => SceneWire.PayloadOf(this);

    // `shadow` and `replay` are in none of these on purpose, and so are registered in none of them:
    // every sort of actor draws something, so every sort of actor may throw a shadow of it; and
    // whether there is a clock to restart is a question of the source, not of the sort, so `replay`
    // is refused where it applies rather than by name.
    private static readonly SortField[] WashFields =
    [
        new("fit", actor => actor.Fit is not null),
        new("periodMs", actor => actor.PeriodMs is not null)
    ];

    private static readonly SortField[] SpriteFields =
    [
        new("from", actor => actor.From is not null),
        new("to", actor => actor.To is not null),
        new("ease", actor => actor.Ease is not null),
        new("turn", actor => actor.Turn is not null),
        new("rotate", actor => actor.Rotate is not null),
        new("bowPct", actor => actor.BowPct is not null),
        new("bowAxis", actor => actor.BowAxis is not null),
        new("bankDeg", actor => actor.BankDeg is not null)
    ];

    private static readonly SortField[] BandFields =
    [
        new("thicknessPct", actor => actor.ThicknessPct is not null),
        new("cornerPct", actor => actor.CornerPct is not null)
    ];

    private static readonly SortField[] WrapFields = [new("widthPct", actor => actor.WidthPct is not null)];

    private static readonly SortField[] EmitterFields =
    [
        new("count", actor => actor.Count is not null),
        new("seed", actor => actor.Seed is not null),
        new("sizeVarPct", actor => actor.SizeVarPct is not null),
        new("durationVarMs", actor => actor.DurationVarMs is not null),
        new("driftPct", actor => actor.DriftPct is not null),
        new("swayPct", actor => actor.SwayPct is not null),
        new("swayPeriodMs", actor => actor.SwayPeriodMs is not null),
        new("swayVarMs", actor => actor.SwayVarMs is not null),
        new("spinDeg", actor => actor.SpinDeg is not null),
        new("flutterDeg", actor => actor.FlutterDeg is not null),
        new("depthSpreadPct", actor => actor.DepthSpreadPct is not null)
    ];

    /// <summary>
    /// What the two nine-slices both carry.
    /// </summary>
    /// <remarks>
    /// A wrap is the same picture cut the same way and revealed the same way; all that differs is
    /// which sides of the card it is laid along. So the cut, the reach past the edge, how the middle
    /// is tiled and how it is uncovered are one group, and what is left to each of them is its own
    /// shape: a band's thickness on the one side it lies on, a wrap's four.
    /// </remarks>
    /// <summary>What the two nine-slices share: where to cut the picture and how to lay it.</summary>
    private static readonly SortField[] SliceFields =
    [
        new("slice", actor => actor.Slice is not null),
        new("outsetPct", actor => actor.OutsetPct is not null),
        new("tile", actor => actor.Tile is not null)
    ];

    /// <summary>
    /// Being revealed into place — a band, a wrap, and a picture over the whole card.
    /// </summary>
    /// <remarks>
    /// <b>Not a nine-slice's private business.</b> It started as one, and the first thing anybody
    /// asked for that the engine could not draw was a tree: one picture of a whole card, spreading
    /// out of its own root. Growing into place is orthogonal to what shape the thing is, so it
    /// belongs to the three actors that stand still. A sprite and a scatter are already going
    /// somewhere; being uncovered as well is two answers to one question.
    /// </remarks>
    private static readonly SortField[] GrowFields = [new("grow", actor => actor.Grow is not null)];

    /// <summary>What a sprite and an emitter both carry, and nothing else does.</summary>
    /// <remarks>
    /// <b><c>bobPct</c> is here rather than on either of them.</b> Both sorts have one and they mean
    /// different things by it — a wingbeat's rise and fall on the one, the dip at each edge-on pass
    /// on the other — but a field named in both <see cref="SpriteFields"/> and
    /// <see cref="EmitterFields"/> is foreign to both, so each sort would refuse its own field.
    /// A shared meaning is not what puts a name in this group; a shared set of owners is.
    /// </remarks>
    private static readonly SortField[] TravelFields =
    [
        new("sizePct", actor => actor.SizePct is not null),
        new("durationMs", actor => actor.DurationMs is not null),
        new("bobPct", actor => actor.BobPct is not null)
    ];

    /// <summary>What a band and an emitter both carry. A wrap has no one edge to lie on.</summary>
    private static readonly SortField[] EdgeFields = [new("edge", actor => actor.Edge is not null)];

    /// <summary>What a wash and a sprite both carry.</summary>
    private static readonly SortField[] ScaleFields = [new("scale", actor => actor.Scale is not null)];

    /// <summary>
    /// How an actor's own cycle repeats — everything but the two nine-slices.
    /// </summary>
    /// <remarks>
    /// <b>Shared, because a scene that ends is a scene, not a special case.</b> This started out as a
    /// sprite's alone, and a scene whose whole idea was "it plays through once and leaves the card
    /// looking like something" could not be written: its washes and its scatters would go round
    /// forever underneath the parts that had finished. A band and a wrap have no <c>repeat</c> only
    /// because theirs is inside <c>grow</c>, where the hold lives with it.
    /// </remarks>
    private static readonly SortField[] CycleFields = [new("repeat", actor => actor.Repeat is not null)];

    /// <summary>
    /// Showing past the card's edge — a picture over the whole card, and a figure crossing it.
    /// </summary>
    /// <remarks>
    /// The two nine-slices already reach past the edge by <c>outsetPct</c>, and a scatter's spill is
    /// not drawn; a name that does nothing is refused rather than kept, for the reason the type gives.
    /// </remarks>
    private static readonly SortField[] SpillFields = [new("spillPct", actor => actor.SpillPct is not null)];

    /// <summary>
    /// Withdrawing from the reading zone — everything but a scatter.
    /// </summary>
    /// <remarks>
    /// A scatter is its copies, each already on a cycle of its own, and one that should stop
    /// covering the card plays once — which <c>repeat</c> already says.
    /// </remarks>
    private static readonly SortField[] RetreatFields = [new("retreat", actor => actor.Retreat is not null)];

    public void Validate(ICosmeticPayloadReport report)
    {
        if (Reach is not ("content" or "card" or "choice"))
        {
            report.Error("reach is 'content', 'card' or 'choice'");
        }
        else if (Reach is "choice")
        {
            if (DefaultReach is not ("content" or "card"))
                report.Error("defaultReach is 'content' or 'card' when reach is 'choice'");
        }
        else if (DefaultReach is not null)
        {
            // Refused rather than ignored: a field nothing reads is a setting somebody will fill in
            // and then wonder why nothing happened.
            report.Error("defaultReach is only read when reach is 'choice'");
        }

        Bound(report, MinWidthPx, "minWidthPx", 0, CosmeticSceneLimits.MaxMinWidthPx);

        if (Actors is not { Length: > 0 })
        {
            report.Error("actors needs at least one actor for there to be a scene");
            return;
        }

        if (Actors.Length > CosmeticSceneLimits.MaxActors)
            report.Error($"actors is capped at {CosmeticSceneLimits.MaxActors}");

        var sheet = ValidateSheet(report);

        for (var index = 0; index < Actors.Length; index++)
        {
            if (Actors[index] is not { } actor)
            {
                report.Error($"actors[{index}] is null");
                continue;
            }

            ValidateActor(report, actor, index, sheet);
        }
    }

    /// <summary>
    /// The sheet is declared exactly when something cuts a rectangle out of it.
    /// </summary>
    /// <remarks>
    /// Both halves are refusals rather than defaults. A scene reading the sheet without one has no
    /// ratio to cut with and would draw whatever the browser made of a missing number; a scene
    /// carrying one that nothing reads is two dimensions somebody measured and will expect to see
    /// used, which is the same trap as a <c>defaultReach</c> nothing consults.
    /// </remarks>
    private SheetSize? ValidateSheet(ICosmeticPayloadReport report)
    {
        var read = false;

        foreach (var actor in Actors!)
        {
            // A null actor reads nothing: it is reported where the actors are walked one by one.
            if (actor is { Source: null or "atlas" })
            {
                read = true;
                break;
            }
        }

        if (!read)
        {
            if (Sheet is not null)
                report.Error("sheet is only read when an actor reads a rectangle out of it");

            return null;
        }

        if (Sheet is null)
        {
            report.Error("sheet is missing, and an actor reads a rectangle out of it");
            return null;
        }

        var width = Require(report, Sheet.W, "sheet.w", 1, CosmeticSceneLimits.MaxAtlasPx);
        var height = Require(report, Sheet.H, "sheet.h", 1, CosmeticSceneLimits.MaxAtlasPx);

        return width is { } across && height is { } down ? new SheetSize(across, down) : null;
    }

    /// <summary>A sheet that passed <see cref="ValidateSheet"/>: both sides there and in range.</summary>
    private readonly record struct SheetSize(int Width, int Height);

    private static void ValidateActor(
        ICosmeticPayloadReport report,
        CosmeticSceneActor actor,
        int index,
        SheetSize? sheet)
    {
        var at = $"actors[{index}]";

        switch (actor.Type)
        {
            case "wash":
                RefuseStrayFields(report, actor, at, SpriteFields, BandFields, WrapFields, EmitterFields, TravelFields, EdgeFields, SliceFields);
                ValidateWash(report, actor, at);
                break;

            case "sprite":
                RefuseStrayFields(report, actor, at, WashFields, BandFields, WrapFields, EmitterFields, EdgeFields, SliceFields, GrowFields);
                ValidateSprite(report, actor, at);
                break;

            case "band":
                RefuseStrayFields(report, actor, at, WashFields, SpriteFields, WrapFields, EmitterFields, TravelFields, ScaleFields, CycleFields, SpillFields);
                ValidateBand(report, actor, at);
                break;

            case "wrap":
                RefuseStrayFields(report, actor, at, WashFields, SpriteFields, BandFields, EmitterFields, TravelFields, ScaleFields, CycleFields, EdgeFields, SpillFields);
                ValidateWrap(report, actor, at);
                break;

            case "emitter":
                RefuseStrayFields(report, actor, at, WashFields, SpriteFields, BandFields, WrapFields, ScaleFields, SliceFields, GrowFields, SpillFields, RetreatFields);
                ValidateEmitter(report, actor, at);
                break;

            default:
                report.Error($"{at}.type is 'wash', 'sprite', 'band', 'wrap' or 'emitter'");
                return;
        }

        ValidateCommon(report, actor, at, sheet);
    }

    private static void ValidateWash(ICosmeticPayloadReport report, CosmeticSceneActor actor, string at)
    {
        if (actor.Fit is { Length: > 0 } and not ("cover" or "contain" or "stretch"))
            report.Error($"{at}.fit is 'cover', 'contain' or 'stretch'");

        Bound(report, actor.PeriodMs, $"{at}.periodMs", CosmeticSceneLimits.MinPeriodMs, CosmeticSceneLimits.MaxPeriodMs);
        Bound(report, actor.SpillPct, $"{at}.spillPct", 0d, CosmeticSceneLimits.MaxOutsetPct);

        // The spill box is the card plus the hang, and `cover` is the one fit that fills it with the
        // picture as drawn: `contain` leaves the hang empty and `stretch` fills it with distortion.
        if (actor.SpillPct is > 0d && actor.Fit is { Length: > 0 } and not "cover")
            report.Error($"{at}.spillPct needs fit 'cover': a picture that hangs past the card has to cover it first");

        ValidateEnvelope(report, actor.Scale, $"{at}.scale", 0.1d, CosmeticSceneLimits.MaxScale);
        ValidateGrow(report, actor.Grow, $"{at}.grow");
    }

    private static void ValidateSprite(ICosmeticPayloadReport report, CosmeticSceneActor actor, string at)
    {
        RequireDouble(report, actor.SizePct, $"{at}.sizePct", 1d, CosmeticSceneLimits.MaxSizePct);
        Require(report, actor.DurationMs, $"{at}.durationMs", CosmeticSceneLimits.MinPeriodMs, CosmeticSceneLimits.MaxPeriodMs);

        ValidatePoint(report, actor.From, $"{at}.from");
        ValidatePoint(report, actor.To, $"{at}.to");

        if (actor.Ease is { Length: > 0 } and not ("linear" or "in" or "out" or "inOut"))
            report.Error($"{at}.ease is 'linear', 'in', 'out' or 'inOut'");

        if (actor.Turn is { Length: > 0 } and not ("none" or "mirror"))
            report.Error($"{at}.turn is 'none' or 'mirror'");

        if (actor.BowAxis is { Length: > 0 } and not ("auto" or "x" or "y"))
            report.Error($"{at}.bowAxis is 'auto', 'x' or 'y'");

        Bound(report, actor.BowPct, $"{at}.bowPct", -CosmeticSceneLimits.MaxBowPct, CosmeticSceneLimits.MaxBowPct);
        Bound(report, actor.BankDeg, $"{at}.bankDeg", -CosmeticSceneLimits.MaxBankDeg, CosmeticSceneLimits.MaxBankDeg);
        Bound(report, actor.BobPct, $"{at}.bobPct", 0d, CosmeticSceneLimits.MaxBobPct);
        Bound(report, actor.SpillPct, $"{at}.spillPct", 0d, CosmeticSceneLimits.MaxOutsetPct);

        ValidateEnvelope(report, actor.Scale, $"{at}.scale", 0.1d, CosmeticSceneLimits.MaxScale);
        ValidateEnvelope(report, actor.Rotate, $"{at}.rotate", -CosmeticSceneLimits.MaxRotateDeg, CosmeticSceneLimits.MaxRotateDeg);
    }

    private static void ValidateBand(ICosmeticPayloadReport report, CosmeticSceneActor actor, string at)
    {
        if (!IsEdge(actor.Edge))
            report.Error($"{at}.edge is 'top', 'right', 'bottom' or 'left'");

        // border-image-source takes a whole picture and cannot be handed a rectangle of one, so a
        // band carries its own file. Refused rather than drawn some other way: a band that quietly
        // stopped being a nine-slice would distort its own ends on every card of a different width.
        if (actor.Source is not "file")
            report.Error($"{at} is a nine-slice band, so it needs a file of its own rather than a rectangle of the sheet");

        ValidateSides(report, actor.Slice, $"{at}.slice", 0, CosmeticSceneLimits.MaxSlice);

        RequireDouble(report, actor.ThicknessPct, $"{at}.thicknessPct", 1d, 100d);
        Bound(report, actor.CornerPct, $"{at}.cornerPct", 0d, 100d);
        Bound(report, actor.OutsetPct, $"{at}.outsetPct", 0d, CosmeticSceneLimits.MaxOutsetPct);

        if (actor.Tile is { Length: > 0 } and not ("stretch" or "repeat" or "round" or "space"))
            report.Error($"{at}.tile is 'stretch', 'repeat', 'round' or 'space'");

        ValidateGrow(report, actor.Grow, $"{at}.grow");
    }

    private static void ValidateWrap(ICosmeticPayloadReport report, CosmeticSceneActor actor, string at)
    {
        // The same refusal a band gets, for the same reason: border-image-source takes a whole
        // picture, and a rectangle of a sheet is not one.
        if (actor.Source is not "file")
            report.Error($"{at} is a nine-slice wrap, so it needs a file of its own rather than a rectangle of the sheet");

        ValidateSides(report, actor.Slice, $"{at}.slice", 0, CosmeticSceneLimits.MaxSlice);
        ValidateSidesDouble(report, actor.WidthPct, $"{at}.widthPct", 0d, 100d);

        // Four sides of nothing is a wrap somebody meant to shape and left empty — it publishes, it
        // renders, and it draws no pixels at all, which on a card is indistinguishable from a file
        // that failed to load.
        if (actor.WidthPct is { Length: 4 } widths && widths[0] + widths[1] + widths[2] + widths[3] is 0d)
            report.Error($"{at}.widthPct is zero on every side, so the wrap is drawn nowhere");

        Bound(report, actor.OutsetPct, $"{at}.outsetPct", 0d, CosmeticSceneLimits.MaxOutsetPct);

        if (actor.Tile is { Length: > 0 } and not ("stretch" or "repeat" or "round" or "space"))
            report.Error($"{at}.tile is 'stretch', 'repeat', 'round' or 'space'");

        ValidateGrow(report, actor.Grow, $"{at}.grow");
    }

    private static void ValidateEmitter(ICosmeticPayloadReport report, CosmeticSceneActor actor, string at)
    {
        if (!IsEdge(actor.Edge))
            report.Error($"{at}.edge is 'top', 'right', 'bottom' or 'left'");

        // Every copy has to sit at a different point of the same cycle, and the only way to do that
        // is a negative delay on an animation this code controls. A self-animating file plays to its
        // own clock, so forty copies of one would fall in step.
        if (actor.Source is "file")
            report.Error($"{at} is an emitter and reads the atlas; a self-animating file cannot be spread out");

        var count = Require(report, actor.Count, $"{at}.count", 1, CosmeticSceneLimits.MaxEmitterCount);

        Bound(report, actor.Seed, $"{at}.seed", 0, 65_535);

        var size = RequireDouble(report, actor.SizePct, $"{at}.sizePct", 1d, CosmeticSceneLimits.MaxSizePct);
        var duration = Require(report, actor.DurationMs, $"{at}.durationMs", CosmeticSceneLimits.MinPeriodMs, CosmeticSceneLimits.MaxPeriodMs);

        Bound(report, actor.SizeVarPct, $"{at}.sizeVarPct", 0d, CosmeticSceneLimits.MaxSizePct);
        Bound(report, actor.DurationVarMs, $"{at}.durationVarMs", 0, CosmeticSceneLimits.MaxPeriodMs);
        Bound(report, actor.DriftPct, $"{at}.driftPct", 0d, 200d);
        Bound(report, actor.SwayPct, $"{at}.swayPct", 0d, 100d);
        Bound(report, actor.SwayPeriodMs, $"{at}.swayPeriodMs", CosmeticSceneLimits.MinPeriodMs, 120_000);
        Bound(report, actor.SwayVarMs, $"{at}.swayVarMs", 0, 120_000);
        Bound(report, actor.SpinDeg, $"{at}.spinDeg", 0d, CosmeticSceneLimits.MaxRotateDeg);
        Bound(report, actor.FlutterDeg, $"{at}.flutterDeg", 0d, CosmeticSceneLimits.MaxFlutterDeg);
        Bound(report, actor.BobPct, $"{at}.bobPct", 0d, CosmeticSceneLimits.MaxBobPct);
        Bound(report, actor.DepthSpreadPct, $"{at}.depthSpreadPct", 0d, 100d);

        // Spread wider than the middle and the smallest copies come out at nothing, which is a scene
        // with holes in it rather than a scene with variety.
        if (size is { } middle && actor.SizeVarPct is { } spread && spread > middle)
            report.Error($"{at}.sizeVarPct is wider than sizePct, so some copies come out at no size at all");

        if (duration is { } span && actor.DurationVarMs is { } vary && vary > span)
            report.Error($"{at}.durationVarMs is wider than durationMs, so some copies never finish");

        // The same trap one period in. Read off the actor rather than a checked value because the
        // period is optional: a spread against a period nobody set is a spread of nothing.
        if (actor.SwayPeriodMs is { } swing && actor.SwayVarMs is { } scatter && scatter > swing)
            report.Error($"{at}.swayVarMs is wider than swayPeriodMs, so some copies come out with no sway at all");

        _ = count;
    }

    private static void ValidateCommon(
        ICosmeticPayloadReport report,
        CosmeticSceneActor actor,
        string at,
        SheetSize? sheet)
    {
        var source = actor.Source ?? "atlas";

        if (source is not ("atlas" or "file"))
        {
            report.Error($"{at}.source is 'atlas' or 'file'");
        }
        else if (source is "atlas")
        {
            if (actor.Atlas is null)
                report.Error($"{at}.atlas is missing, and this actor reads the sheet");
            else
                ValidateAtlas(report, actor.Atlas, $"{at}.atlas", sheet);
        }
        else if (actor.Atlas is not null)
        {
            report.Error($"{at} draws a whole file and has no atlas rectangle");
        }

        // Refused where it applies rather than by name: whether there is a clock to restart is a
        // question of the source, not of the sort. `false` passes everywhere, because the console's
        // toggle writes it.
        if (source is "atlas" && actor.Replay is true)
            report.Error($"{at}.replay is only read for a file; a rectangle of the sheet has no clock of its own");

        if (actor.Slot is { Length: > 0 } slot && !CosmeticAssetSlots.TryParseName(slot, out _))
            report.Error($"{at}.slot '{slot}' is not an asset slot");

        if (actor.Depth is { Length: > 0 } and not ("deep" or "over" or "front"))
            report.Error($"{at}.depth is 'deep', 'over' or 'front'");

        if (actor.Occlude is { Length: > 0 } occlude)
        {
            for (var index = 0; index < occlude.Length; index++)
            {
                if (occlude[index] is not { } part)
                {
                    report.Error($"{at}.occlude[{index}] is null");
                }
                else if (part is not "avatar")
                {
                    report.Error($"{at}.occlude names '{part}', and the only part of a card that hides anything is 'avatar'");
                }
            }
        }

        Bound(report, actor.DelayMs, $"{at}.delayMs", -120_000, 120_000);

        // Checked here rather than three times over: a band is the only actor without one, and it is
        // refused a `repeat` by name before this runs.
        if (actor.Repeat is { Length: > 0 } and not ("loop" or "once" or "pingPong"))
            report.Error($"{at}.repeat is 'loop', 'once' or 'pingPong'");

        ValidateEnvelope(report, actor.Opacity, $"{at}.opacity", 0d, 1d);

        // Checked here for the same reason `repeat` is: every sort of actor draws something, so
        // every sort of actor may throw a shadow of it, and no sort is refused one by name.
        ValidateShadow(report, actor.Shadow, $"{at}.shadow");

        // And here, because the one sort that has no retreat is refused one by name before this runs.
        ValidateRetreat(report, actor.Retreat, $"{at}.retreat");
    }

    /// <summary>
    /// An envelope: two or more stops, running from the start of the cycle to the end of it.
    /// </summary>
    /// <remarks>
    /// <b>Both ends are required and the order is strict.</b> The client turns these into a CSS
    /// easing function, which is a shape over a whole cycle — a list that starts at 0.3 or doubles
    /// back has no meaning there, and would come out as whatever the browser made of it.
    /// </remarks>
    private static void ValidateEnvelope(
        ICosmeticPayloadReport report,
        CosmeticSceneStop[]? stops,
        string at,
        double low,
        double high)
    {
        if (stops is null)
            return;

        if (stops.Length < 2)
        {
            report.Error($"{at} needs at least two stops; one stop is a constant, and a constant is the field's own value");
            return;
        }

        if (stops.Length > CosmeticSceneLimits.MaxStops)
        {
            report.Error($"{at} is capped at {CosmeticSceneLimits.MaxStops} stops");
            return;
        }

        var previous = double.NegativeInfinity;

        for (var index = 0; index < stops.Length; index++)
        {
            var where = $"{at}[{index}]";

            if (stops[index] is not { } stop)
            {
                report.Error($"{where} is null");
                return;
            }

            if (stop.At is not { } position)
            {
                report.Error($"{where}.at is missing");
                return;
            }

            if (position < 0d || position > 1d)
            {
                report.Error($"{where}.at is {position}, and the range is 0 to 1");
                return;
            }

            if (position <= previous)
            {
                report.Error($"{where}.at is {position}, which does not come after {previous}");
                return;
            }

            previous = position;

            if (stop.V is not { } value)
            {
                report.Error($"{where}.v is missing");
                return;
            }

            if (value < low || value > high)
                report.Error($"{where}.v is {value}, and the range is {low} to {high}");
        }

        if (stops[0].At != 0d)
            report.Error($"{at} starts at {stops[0].At}, and an envelope covers the whole cycle from 0");

        if (stops[^1].At != 1d)
            report.Error($"{at} ends at {stops[^1].At}, and an envelope covers the whole cycle to 1");
    }

    private static void ValidatePoint(ICosmeticPayloadReport report, CosmeticScenePoint? point, string at)
    {
        if (point is null)
        {
            report.Error($"{at} is missing");
            return;
        }

        if (!IsAnchor(point.Anchor))
        {
            report.Error($"{at}.anchor is one of topLeft, top, topRight, left, center, right, bottomLeft, bottom, bottomRight");
            return;
        }

        Bound(report, point.DxPct, $"{at}.dxPct", -CosmeticSceneLimits.MaxOffsetPct, CosmeticSceneLimits.MaxOffsetPct);
        Bound(report, point.DyPct, $"{at}.dyPct", -CosmeticSceneLimits.MaxOffsetPct, CosmeticSceneLimits.MaxOffsetPct);
    }

    private static void ValidateGrow(ICosmeticPayloadReport report, CosmeticSceneGrow? grow, string at)
    {
        if (grow is null)
            return;

        var from = RequireDouble(report, grow.FromPct, $"{at}.fromPct", 0d, 100d);
        var to = RequireDouble(report, grow.ToPct, $"{at}.toPct", 0d, 100d);

        Require(report, grow.DurationMs, $"{at}.durationMs", CosmeticSceneLimits.MinPeriodMs, CosmeticSceneLimits.MaxPeriodMs);
        Bound(report, grow.HoldMs, $"{at}.holdMs", 0, CosmeticSceneLimits.MaxPeriodMs);

        if (grow.Repeat is { Length: > 0 } and not ("loop" or "once" or "pingPong"))
            report.Error($"{at}.repeat is 'loop', 'once' or 'pingPong'");

        if (grow.From is not null && grow.From is not "edge" && !IsAnchor(grow.From))
            report.Error($"{at}.from is 'edge' or one of topLeft, top, topRight, left, center, right, bottomLeft, bottom, bottomRight");

        if (grow.LeadAxis is { Length: > 0 } and not ("y" or "x" or "both"))
            report.Error($"{at}.leadAxis is 'y', 'x' or 'both'");

        Bound(report, grow.FromScale, $"{at}.fromScale", 0.01d, 1d);
        Bound(report, grow.WobbleDeg, $"{at}.wobbleDeg", 0d, CosmeticSceneLimits.MaxGrowDeg);
        Bound(report, grow.Softness, $"{at}.softness", 0d, 1d);

        ValidateSway(report, grow.Sway, $"{at}.sway");

        // Equal ends are a grow with no reveal in it, which is what a picture drawn whole wants.
        if (from is { } start && to is { } end && start > end)
            report.Error($"{at}.fromPct is {start} and toPct is {end}; growth runs outwards");
    }

    /// <summary>
    /// The lean a grown thing keeps afterwards: how far, and how long a lean takes.
    /// </summary>
    /// <remarks>
    /// Both are required once the object is there, for the reason <see cref="ValidateShadow"/> gives:
    /// a sway of an amplitude and no period, or a period and no amplitude, is not half a sway.
    /// </remarks>
    private static void ValidateSway(ICosmeticPayloadReport report, CosmeticSceneSway? sway, string at)
    {
        if (sway is null)
            return;

        RequireDouble(report, sway.Deg, $"{at}.deg", 0d, CosmeticSceneLimits.MaxGrowDeg);
        Require(report, sway.Ms, $"{at}.ms", CosmeticSceneLimits.MinPeriodMs, CosmeticSceneLimits.MaxPeriodMs);
    }

    /// <summary>
    /// A retreat is when and how long, and then what it keeps.
    /// </summary>
    /// <remarks>
    /// The two times are required and the strips default to nothing, because a retreat that keeps
    /// nothing is still a retreat: the picture goes and the text is read. What is refused is the
    /// opposite — a <c>remain</c> of a whole passes the range and leaves the card exactly as covered
    /// as it was, which is the same trap as a wrap of four zero widths.
    /// </remarks>
    private static void ValidateRetreat(ICosmeticPayloadReport report, CosmeticSceneRetreat? retreat, string at)
    {
        if (retreat is null)
            return;

        Require(report, retreat.AtMs, $"{at}.atMs", 0, CosmeticSceneLimits.MaxPeriodMs);
        Require(report, retreat.DurationMs, $"{at}.durationMs", CosmeticSceneLimits.MinPeriodMs, CosmeticSceneLimits.MaxPeriodMs);

        Bound(report, retreat.EdgePct, $"{at}.edgePct", 0d, CosmeticSceneLimits.MaxRetreatStripPct);
        Bound(report, retreat.FootPct, $"{at}.footPct", 0d, CosmeticSceneLimits.MaxRetreatStripPct);
        Bound(report, retreat.SoftPct, $"{at}.softPct", 0d, CosmeticSceneLimits.MaxRetreatSoftPct);

        if (retreat.Remain is { } remain && remain >= 1d)
            report.Error($"{at}.remain is {remain}; a retreat that leaves everything behind is not a retreat");
        else
            Bound(report, retreat.Remain, $"{at}.remain", 0d, 1d);
    }

    /// <summary>
    /// A shadow is four numbers or it is absent.
    /// </summary>
    /// <remarks>
    /// <b>Every member required rather than defaulted.</b> The four are one description of one light:
    /// a throw with no blur is a hard double of the drawing, and a blur with no alpha is a shadow at
    /// whatever opacity the renderer picked. Defaults here would be the renderer choosing where the
    /// sun is, which is exactly the decision the person who drew the actor is making.
    /// </remarks>
    private static void ValidateShadow(ICosmeticPayloadReport report, CosmeticSceneShadow? shadow, string at)
    {
        if (shadow is null)
            return;

        RequireDouble(report, shadow.DxPct, $"{at}.dxPct", -CosmeticSceneLimits.MaxShadowOffsetPct, CosmeticSceneLimits.MaxShadowOffsetPct);
        RequireDouble(report, shadow.DyPct, $"{at}.dyPct", -CosmeticSceneLimits.MaxShadowOffsetPct, CosmeticSceneLimits.MaxShadowOffsetPct);
        RequireDouble(report, shadow.BlurPct, $"{at}.blurPct", 0d, CosmeticSceneLimits.MaxShadowBlurPct);
        RequireDouble(report, shadow.Alpha, $"{at}.alpha", 0d, 1d);
    }

    private static void ValidateAtlas(
        ICosmeticPayloadReport report,
        CosmeticSceneAtlas atlas,
        string at,
        SheetSize? sheet)
    {
        Bound(report, atlas.X, $"{at}.x", 0, CosmeticSceneLimits.MaxAtlasPx);
        Bound(report, atlas.Y, $"{at}.y", 0, CosmeticSceneLimits.MaxAtlasPx);

        var width = Require(report, atlas.W, $"{at}.w", 1, CosmeticSceneLimits.MaxAtlasPx);
        var height = Require(report, atlas.H, $"{at}.h", 1, CosmeticSceneLimits.MaxAtlasPx);

        if (atlas.Frames is null)
        {
            if (atlas.Columns is not null || atlas.Fps is not null || atlas.Still is not null)
                report.Error($"{at} names no frames, so it is one picture and has no columns, fps or still");

            ValidateFitsTheSheet(report, atlas, at, sheet, width, height, 1, 1);
            return;
        }

        var frames = Require(report, atlas.Frames, $"{at}.frames", 1, CosmeticSceneLimits.MaxSpriteFrames);
        var columns = Require(report, atlas.Columns, $"{at}.columns", 1, CosmeticSceneLimits.MaxSpriteFrames);

        Bound(report, atlas.Fps, $"{at}.fps", 1, 60);

        if (frames is not { } frameCount || columns is not { } columnCount)
            return;

        if (columnCount > frameCount || frameCount % columnCount is not 0)
            report.Error($"{at}.frames must divide evenly into rows of {columnCount}");
        else
            ValidateFitsTheSheet(report, atlas, at, sheet, width, height, columnCount, frameCount / columnCount);

        if (atlas.Still is { } still && (still < 0 || still >= frameCount))
            report.Error($"{at}.still names frame {still}, and the strip has {frameCount}");
    }

    /// <summary>
    /// Every cell of a rectangle lies inside the sheet it is cut from.
    /// </summary>
    /// <remarks>
    /// <b>The whole strip, not the first cell.</b> A rectangle that fits and a strip of four that
    /// runs off the end are the same four numbers in the console, and the second publishes a
    /// cosmetic whose later frames are empty sheet — a figure that blinks out rather than a number
    /// somebody typed one digit too large. This is the mistake operators actually make, and nothing
    /// downstream can tell it from a drawing with gaps in it.
    /// </remarks>
    private static void ValidateFitsTheSheet(
        ICosmeticPayloadReport report,
        CosmeticSceneAtlas atlas,
        string at,
        SheetSize? sheet,
        int? width,
        int? height,
        int columns,
        int rows)
    {
        if (sheet is not { } bounds)
            return;

        if (width is not { } cell || height is not { } row)
            return;

        var right = (atlas.X ?? 0) + (columns * cell);

        if (right > bounds.Width)
            report.Error($"{at} runs {right - bounds.Width}px past the right edge of the sheet");

        var bottom = (atlas.Y ?? 0) + (rows * row);

        if (bottom > bounds.Height)
            report.Error($"{at} runs {bottom - bounds.Height}px past the bottom of the sheet");
    }

    private static void RefuseStrayFields(
        ICosmeticPayloadReport report,
        CosmeticSceneActor actor,
        string at,
        params SortField[][] foreign)
    {
        foreach (var group in foreign)
        {
            foreach (var field in group)
            {
                if (field.IsFilled(actor))
                    report.Error($"{at} is a {actor.Type} and has no '{field.Name}'");
            }
        }
    }

    /// <summary>A field only some sorts of actor carry: its name in the document, and whether an actor has it.</summary>
    private sealed record SortField(string Name, Func<CosmeticSceneActor, bool> IsFilled);

    private static bool IsAnchor(string? anchor) => anchor is
        "topLeft" or "top" or "topRight"
        or "left" or "center" or "right"
        or "bottomLeft" or "bottom" or "bottomRight";

    private static bool IsEdge(string? edge) => edge is "top" or "right" or "bottom" or "left";

    private static void ValidateSides(
        ICosmeticPayloadReport report,
        int[]? sides,
        string at,
        int low,
        int high)
    {
        if (sides is null)
        {
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

    /// <summary>
    /// The same as <see cref="ValidateSides"/>, for the four sides a wrap measures in per cent of a
    /// card's width.
    /// </summary>
    private static void ValidateSidesDouble(
        ICosmeticPayloadReport report,
        double[]? sides,
        string at,
        double low,
        double high)
    {
        if (sides is null)
        {
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

    /// <summary>
    /// The same as <see cref="Require(ICosmeticPayloadReport, int?, string, int, int)"/>, for the
    /// fractional sizes a scene measures in per cent of a card's width.
    /// </summary>
    private static double? RequireDouble(ICosmeticPayloadReport report, double? value, string at, double low, double high)
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

/// <summary>
/// A validated scene, turned into the typed actors Ion carries.
/// </summary>
/// <remarks>
/// <para>Only ever handed a payload that passed <see cref="ProfileScenePayload.Validate"/>, so a field
/// the validation requires is present here, every word is one of its closed list, and the
/// null-forgiving reads below are what that check bought.</para>
///
/// <para>The sort is the case. The stored document is one flat type with a discriminator, and the
/// validation refuses a field that belongs to another sort by name; on the wire that refusal is the
/// type's, because a case has only the fields its sort may carry.</para>
///
/// <para>What an actor leaves out arrives absent rather than as a default. Several of the defaults are
/// not constants — a band's corner is its own thickness — and all of them are the renderer's, so the
/// wire says only what the row said.</para>
/// </remarks>
internal static class SceneWire
{
    public static PayloadProfileScene PayloadOf(ProfileScenePayload payload)
    {
        var actors = new List<ISceneActor>(payload.Actors!.Length);

        foreach (var actor in payload.Actors)
        {
            actors.Add(ActorOf(actor));
        }

        return new PayloadProfileScene(
            ReachRuleOf(payload.Reach!),
            ReachOf(payload.DefaultReach),
            (ushort?)payload.MinWidthPx,
            payload.Sheet is { } sheet ? new SceneSheet((ushort)sheet.W!.Value, (ushort)sheet.H!.Value) : null,
            new IonArray<ISceneActor>(actors));
    }

    private static ISceneActor ActorOf(CosmeticSceneActor actor)
    {
        var slot = actor.Slot is { Length: > 0 } named
            ? CosmeticAssetSlots.ToWire(Enum.Parse<CosmeticAssetSlot>(named))
            : (AssetSlot?)null;

        var source  = SourceOf(actor.Source);
        var atlas   = AtlasOf(actor.Atlas);
        var depth   = DepthOf(actor.Depth);
        var occlude = OccludersOf(actor.Occlude);
        var opacity = EnvelopeOf(actor.Opacity);
        var shadow  = ShadowOf(actor.Shadow);

        return actor.Type switch
        {
            "wash" => new SceneWash(slot, source, atlas, actor.Replay, depth, occlude, actor.DelayMs, opacity, shadow,
                FitOf(actor.Fit),
                actor.PeriodMs,
                RepeatOf(actor.Repeat),
                EnvelopeOf(actor.Scale),
                actor.SpillPct,
                GrowOf(actor.Grow),
                RetreatOf(actor.Retreat)),

            "sprite" => new SceneSprite(slot, source, atlas, actor.Replay, depth, occlude, actor.DelayMs, opacity, shadow,
                actor.SizePct!.Value,
                actor.DurationMs!.Value,
                actor.BobPct,
                RepeatOf(actor.Repeat),
                PointOf(actor.From!),
                PointOf(actor.To!),
                EaseOf(actor.Ease),
                TurnOf(actor.Turn),
                EnvelopeOf(actor.Rotate),
                actor.BowPct,
                BowAxisOf(actor.BowAxis),
                actor.BankDeg,
                EnvelopeOf(actor.Scale),
                actor.SpillPct,
                RetreatOf(actor.Retreat)),

            "band" => new SceneBand(slot, source, atlas, actor.Replay, depth, occlude, actor.DelayMs, opacity, shadow,
                EdgeOf(actor.Edge!),
                SliceOf(actor.Slice!),
                actor.ThicknessPct!.Value,
                actor.CornerPct,
                actor.OutsetPct,
                TileOf(actor.Tile),
                GrowOf(actor.Grow),
                RetreatOf(actor.Retreat)),

            "wrap" => new SceneWrap(slot, source, atlas, actor.Replay, depth, occlude, actor.DelayMs, opacity, shadow,
                SliceOf(actor.Slice!),
                new SceneWidths(actor.WidthPct![0], actor.WidthPct[1], actor.WidthPct[2], actor.WidthPct[3]),
                actor.OutsetPct,
                TileOf(actor.Tile),
                GrowOf(actor.Grow),
                RetreatOf(actor.Retreat)),

            "emitter" => new SceneEmitter(slot, source, atlas, actor.Replay, depth, occlude, actor.DelayMs, opacity, shadow,
                EdgeOf(actor.Edge!),
                (ushort)actor.Count!.Value,
                (ushort?)actor.Seed,
                actor.SizePct!.Value,
                actor.DurationMs!.Value,
                actor.BobPct,
                RepeatOf(actor.Repeat),
                actor.SizeVarPct,
                actor.DurationVarMs,
                actor.DriftPct,
                actor.SwayPct,
                actor.SwayPeriodMs,
                actor.SwayVarMs,
                actor.SpinDeg,
                actor.FlutterDeg,
                actor.DepthSpreadPct),

            _ => throw new ArgumentOutOfRangeException(nameof(actor), actor.Type, "a scene actor this build has no case for")
        };
    }

    private static SceneAtlas? AtlasOf(CosmeticSceneAtlas? atlas)
        => atlas is null
            ? null
            : new SceneAtlas(
                (ushort?)atlas.X,
                (ushort?)atlas.Y,
                (ushort)atlas.W!.Value,
                (ushort)atlas.H!.Value,
                (ushort?)atlas.Frames,
                (ushort?)atlas.Columns,
                (ushort?)atlas.Fps,
                (ushort?)atlas.Still);

    private static IonArray<SceneStop>? EnvelopeOf(CosmeticSceneStop[]? stops)
    {
        if (stops is null)
            return null;

        var wire = new List<SceneStop>(stops.Length);

        foreach (var stop in stops)
        {
            wire.Add(new SceneStop(stop.At!.Value, stop.V!.Value));
        }

        return new IonArray<SceneStop>(wire);
    }

    private static IonArray<SceneOccluder>? OccludersOf(string[]? parts)
    {
        if (parts is null)
            return null;

        var wire = new List<SceneOccluder>(parts.Length);

        foreach (var part in parts)
        {
            if (part is "avatar")
                wire.Add(SceneOccluder.Avatar);
        }

        return new IonArray<SceneOccluder>(wire);
    }

    private static SceneShadow? ShadowOf(CosmeticSceneShadow? shadow)
        => shadow is null
            ? null
            : new SceneShadow(shadow.DxPct!.Value, shadow.DyPct!.Value, shadow.BlurPct!.Value, shadow.Alpha!.Value);

    private static SceneRetreat? RetreatOf(CosmeticSceneRetreat? retreat)
        => retreat is null
            ? null
            : new SceneRetreat(
                retreat.AtMs!.Value,
                retreat.DurationMs!.Value,
                retreat.EdgePct,
                retreat.FootPct,
                retreat.SoftPct,
                retreat.Remain);

    private static SceneGrow? GrowOf(CosmeticSceneGrow? grow)
        => grow is null
            ? null
            : new SceneGrow(
                grow.FromPct!.Value,
                grow.ToPct!.Value,
                grow.DurationMs!.Value,
                grow.HoldMs,
                RepeatOf(grow.Repeat),
                grow.FromScale,
                LeadAxisOf(grow.LeadAxis),
                grow.WobbleDeg,
                grow.Softness,
                grow.Sway is { } sway ? new SceneSway(sway.Deg!.Value, sway.Ms!.Value) : null,
                GrowOriginOf(grow.From));

    private static ScenePoint PointOf(CosmeticScenePoint point)
        => new(AnchorOf(point.Anchor!), point.DxPct, point.DyPct);

    private static SceneSlice SliceOf(int[] sides)
        => new((ushort)sides[0], (ushort)sides[1], (ushort)sides[2], (ushort)sides[3]);

    private static SceneReachRule ReachRuleOf(string reach) => reach switch
    {
        "content" => SceneReachRule.Content,
        "card"    => SceneReachRule.Card,
        "choice"  => SceneReachRule.Choice,
        _         => throw new ArgumentOutOfRangeException(nameof(reach), reach, null)
    };

    private static SceneReach? ReachOf(string? reach) => reach switch
    {
        "content" => SceneReach.Content,
        "card"    => SceneReach.Card,
        _         => null
    };

    private static SceneSource? SourceOf(string? source) => source switch
    {
        "atlas" => SceneSource.Atlas,
        "file"  => SceneSource.File,
        _       => null
    };

    private static SceneDepth? DepthOf(string? depth) => depth switch
    {
        "deep"  => SceneDepth.Deep,
        "over"  => SceneDepth.Over,
        "front" => SceneDepth.Front,
        _       => null
    };

    private static SceneFit? FitOf(string? fit) => fit switch
    {
        "cover"   => SceneFit.Cover,
        "contain" => SceneFit.Contain,
        "stretch" => SceneFit.Stretch,
        _         => null
    };

    private static SceneRepeat? RepeatOf(string? repeat) => repeat switch
    {
        "loop"     => SceneRepeat.Loop,
        "once"     => SceneRepeat.Once,
        "pingPong" => SceneRepeat.PingPong,
        _          => null
    };

    private static SceneEase? EaseOf(string? ease) => ease switch
    {
        "linear" => SceneEase.Linear,
        "in"     => SceneEase.In,
        "out"    => SceneEase.Out,
        "inOut"  => SceneEase.InOut,
        _        => null
    };

    private static SceneTurn? TurnOf(string? turn) => turn switch
    {
        "none"   => SceneTurn.None,
        "mirror" => SceneTurn.Mirror,
        _        => null
    };

    private static SceneBowAxis? BowAxisOf(string? axis) => axis switch
    {
        "auto" => SceneBowAxis.Auto,
        "x"    => SceneBowAxis.X,
        "y"    => SceneBowAxis.Y,
        _      => null
    };

    private static SceneLeadAxis? LeadAxisOf(string? axis) => axis switch
    {
        "y"    => SceneLeadAxis.Y,
        "x"    => SceneLeadAxis.X,
        "both" => SceneLeadAxis.Both,
        _      => null
    };

    private static SceneTile? TileOf(string? tile) => tile switch
    {
        "stretch" => SceneTile.Stretch,
        "repeat"  => SceneTile.Repeat,
        "round"   => SceneTile.Round,
        "space"   => SceneTile.Space,
        _         => null
    };

    private static SceneEdge EdgeOf(string edge) => edge switch
    {
        "top"    => SceneEdge.Top,
        "right"  => SceneEdge.Right,
        "bottom" => SceneEdge.Bottom,
        "left"   => SceneEdge.Left,
        _        => throw new ArgumentOutOfRangeException(nameof(edge), edge, null)
    };

    private static SceneAnchor AnchorOf(string anchor) => anchor switch
    {
        "topLeft"     => SceneAnchor.TopLeft,
        "top"         => SceneAnchor.Top,
        "topRight"    => SceneAnchor.TopRight,
        "left"        => SceneAnchor.Left,
        "center"      => SceneAnchor.Center,
        "right"       => SceneAnchor.Right,
        "bottomLeft"  => SceneAnchor.BottomLeft,
        "bottom"      => SceneAnchor.Bottom,
        "bottomRight" => SceneAnchor.BottomRight,
        _             => throw new ArgumentOutOfRangeException(nameof(anchor), anchor, null)
    };

    private static SceneGrowOrigin? GrowOriginOf(string? origin) => origin switch
    {
        null          => null,
        "edge"        => SceneGrowOrigin.Edge,
        "topLeft"     => SceneGrowOrigin.TopLeft,
        "top"         => SceneGrowOrigin.Top,
        "topRight"    => SceneGrowOrigin.TopRight,
        "left"        => SceneGrowOrigin.Left,
        "center"      => SceneGrowOrigin.Center,
        "right"       => SceneGrowOrigin.Right,
        "bottomLeft"  => SceneGrowOrigin.BottomLeft,
        "bottom"      => SceneGrowOrigin.Bottom,
        "bottomRight" => SceneGrowOrigin.BottomRight,
        _             => throw new ArgumentOutOfRangeException(nameof(origin), origin, null)
    };
}
