namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;

/// <summary>
/// Every refusal a scene's payload can make, by the exact words it makes it in.
/// </summary>
/// <remarks>
/// <para>The words are the console's: they are what an operator reads under the field they got wrong,
/// so a refusal that names the wrong field, or two refusals for one mistake, is a defect even when
/// the row is rightly kept out. Each case below starts from a scene that passes and changes one
/// thing, and expects exactly the refusals that one thing earns.</para>
///
/// <para>Numbers are printed with the invariant culture here so that a decimal point reads the same
/// on every machine.</para>
/// </remarks>
[TestFixture]
[SetCulture("")]
public class CosmeticSceneRefusalTests
{
    private const string AnchorWords = "topLeft, top, topRight, left, center, right, bottomLeft, bottom, bottomRight";

    private static readonly string[] Anchors =
        ["topLeft", "top", "topRight", "left", "center", "right", "bottomLeft", "bottom", "bottomRight"];

    private static readonly string[] Sorts = ["wash", "sprite", "band", "wrap", "emitter"];

    /// <summary>A valid value for every field some sorts own and others refuse by name.</summary>
    private static readonly Dictionary<string, Action<CosmeticSceneActor>> Fill = new()
    {
        ["fit"]            = actor => actor.Fit = "cover",
        ["periodMs"]       = actor => actor.PeriodMs = 1000,
        ["sizePct"]        = actor => actor.SizePct = 20d,
        ["durationMs"]     = actor => actor.DurationMs = 4000,
        ["bobPct"]         = actor => actor.BobPct = 2d,
        ["from"]           = actor => actor.From = new CosmeticScenePoint { Anchor = "top" },
        ["to"]             = actor => actor.To = new CosmeticScenePoint { Anchor = "bottom" },
        ["repeat"]         = actor => actor.Repeat = "loop",
        ["ease"]           = actor => actor.Ease = "linear",
        ["turn"]           = actor => actor.Turn = "none",
        ["rotate"]         = actor => actor.Rotate = Envelope(0d, 90d),
        ["bowPct"]         = actor => actor.BowPct = 10d,
        ["bowAxis"]        = actor => actor.BowAxis = "auto",
        ["bankDeg"]        = actor => actor.BankDeg = 10d,
        ["scale"]          = actor => actor.Scale = Envelope(1d, 2d),
        ["spillPct"]       = actor => actor.SpillPct = 0d,
        ["edge"]           = actor => actor.Edge = "top",
        ["slice"]          = actor => actor.Slice = [8, 8, 8, 8],
        ["thicknessPct"]   = actor => actor.ThicknessPct = 10d,
        ["cornerPct"]      = actor => actor.CornerPct = 10d,
        ["widthPct"]       = actor => actor.WidthPct = [6d, 6d, 6d, 6d],
        ["outsetPct"]      = actor => actor.OutsetPct = 2d,
        ["tile"]           = actor => actor.Tile = "round",
        ["grow"]           = actor => actor.Grow = Grow(),
        ["retreat"]        = actor => actor.Retreat = Retreat(),
        ["count"]          = actor => actor.Count = 4,
        ["seed"]           = actor => actor.Seed = 7,
        ["sizeVarPct"]     = actor => actor.SizeVarPct = 1d,
        ["durationVarMs"]  = actor => actor.DurationVarMs = 100,
        ["driftPct"]       = actor => actor.DriftPct = 10d,
        ["swayPct"]        = actor => actor.SwayPct = 10d,
        ["swayPeriodMs"]   = actor => actor.SwayPeriodMs = 1000,
        ["swayVarMs"]      = actor => actor.SwayVarMs = 100,
        ["spinDeg"]        = actor => actor.SpinDeg = 90d,
        ["flutterDeg"]     = actor => actor.FlutterDeg = 30d,
        ["depthSpreadPct"] = actor => actor.DepthSpreadPct = 50d
    };

    /// <summary>Which sort owns which of those fields. Everything else is refused by name.</summary>
    private static readonly Dictionary<string, string[]> Owned = new()
    {
        ["wash"]    = ["fit", "periodMs", "scale", "repeat", "spillPct", "grow", "retreat"],
        ["sprite"]  =
        [
            "from", "to", "ease", "turn", "rotate", "bowPct", "bowAxis", "bankDeg",
            "sizePct", "durationMs", "bobPct", "scale", "repeat", "spillPct", "retreat"
        ],
        ["band"]    = ["thicknessPct", "cornerPct", "edge", "slice", "outsetPct", "tile", "grow", "retreat"],
        ["wrap"]    = ["widthPct", "slice", "outsetPct", "tile", "grow", "retreat"],
        ["emitter"] =
        [
            "count", "seed", "sizeVarPct", "durationVarMs", "driftPct", "swayPct", "swayPeriodMs", "swayVarMs",
            "spinDeg", "flutterDeg", "depthSpreadPct", "sizePct", "durationMs", "bobPct", "edge", "repeat"
        ]
    };

    private static CosmeticSceneAtlas Cell() => new() { X = 0, Y = 0, W = 16, H = 16 };

    private static CosmeticSceneStop[] Envelope(double start, double end)
        => [new CosmeticSceneStop { At = 0d, V = start }, new CosmeticSceneStop { At = 1d, V = end }];

    private static CosmeticSceneGrow Grow() => new() { FromPct = 0d, ToPct = 100d, DurationMs = 2000 };

    private static CosmeticSceneRetreat Retreat() => new() { AtMs = 3000, DurationMs = 800 };

    private static CosmeticSceneActor Wash(Action<CosmeticSceneActor>? change = null)
        => Changed(new CosmeticSceneActor { Type = "wash", Source = "atlas", Atlas = Cell() }, change);

    private static CosmeticSceneActor Sprite(Action<CosmeticSceneActor>? change = null)
        => Changed(new CosmeticSceneActor
        {
            Type       = "sprite",
            Source     = "atlas",
            Atlas      = Cell(),
            SizePct    = 20d,
            DurationMs = 4000,
            From       = new CosmeticScenePoint { Anchor = "topRight" },
            To         = new CosmeticScenePoint { Anchor = "bottomLeft" }
        }, change);

    private static CosmeticSceneActor Band(Action<CosmeticSceneActor>? change = null)
        => Changed(new CosmeticSceneActor
        {
            Type         = "band",
            Source       = "file",
            Slot         = "Secondary",
            Edge         = "bottom",
            Slice        = [8, 8, 8, 8],
            ThicknessPct = 10d
        }, change);

    private static CosmeticSceneActor Wrap(Action<CosmeticSceneActor>? change = null)
        => Changed(new CosmeticSceneActor
        {
            Type     = "wrap",
            Source   = "file",
            Slot     = "Tertiary",
            Slice    = [8, 8, 8, 8],
            WidthPct = [6d, 0d, 6d, 0d]
        }, change);

    private static CosmeticSceneActor Emitter(Action<CosmeticSceneActor>? change = null)
        => Changed(new CosmeticSceneActor
        {
            Type       = "emitter",
            Source     = "atlas",
            Atlas      = Cell(),
            Edge       = "top",
            Count      = 12,
            SizePct    = 4d,
            DurationMs = 9000
        }, change);

    private static CosmeticSceneActor OfSort(string sort, Action<CosmeticSceneActor>? change = null) => sort switch
    {
        "wash"    => Wash(change),
        "sprite"  => Sprite(change),
        "band"    => Band(change),
        "wrap"    => Wrap(change),
        "emitter" => Emitter(change),
        _         => throw new ArgumentOutOfRangeException(nameof(sort), sort, null)
    };

    private static CosmeticSceneActor Changed(CosmeticSceneActor actor, Action<CosmeticSceneActor>? change)
    {
        change?.Invoke(actor);

        return actor;
    }

    /// <summary>A scene of these actors, with a sheet exactly when one of them reads it.</summary>
    private static ProfileScenePayload Scene(CosmeticSceneActor actor, Action<ProfileScenePayload>? change = null)
    {
        var scene = new ProfileScenePayload
        {
            Reach  = "card",
            Sheet  = actor.Source is null or "atlas" ? new CosmeticSceneSheet { W = 256, H = 256 } : null,
            Actors = [actor]
        };

        change?.Invoke(scene);

        return scene;
    }

    private static IReadOnlyList<string> ErrorsOf(IValidatableCosmeticPayload payload)
    {
        var report = new CosmeticPayloadReport();

        payload.Validate(report);

        return report.Errors;
    }

    /// <summary>
    /// Strings one step away from each word: one character changed, one missing, one too many.
    /// </summary>
    /// <remarks>
    /// The compiler matches a closed set of strings by length and then by character, so a word that is
    /// almost right takes paths through that match a plainly wrong one never does.
    /// </remarks>
    private static IEnumerable<string> NearMisses(params string[] words)
    {
        var misses = new HashSet<string> { "~" };

        foreach (var word in words)
        {
            for (var index = 0; index < word.Length; index++)
            {
                misses.Add(string.Concat(word.AsSpan(0, index), "~", word.AsSpan(index + 1)));
            }

            misses.Add(word[..^1]);
            misses.Add(word + "~");
        }

        misses.Remove(string.Empty);
        misses.ExceptWith(words);

        return misses;
    }

    private static TestCaseData Refused(string because, IValidatableCosmeticPayload payload, params string[] refusals)
        => new TestCaseData(payload, refusals).SetArgDisplayNames(because);

    private static TestCaseData Accepted(string because, IValidatableCosmeticPayload payload)
        => new TestCaseData(payload).SetArgDisplayNames(because);

    [TestCaseSource(nameof(SceneRefusals))]
    [TestCaseSource(nameof(SortRefusals))]
    [TestCaseSource(nameof(CommonRefusals))]
    [TestCaseSource(nameof(PartRefusals))]
    [TestCaseSource(nameof(AtlasRefusals))]
    public void A_value_outside_its_rule_is_refused_in_so_many_words(IValidatableCosmeticPayload payload, string[] refusals)
        => Assert.That(ErrorsOf(payload), Is.EqualTo(refusals));

    [TestCaseSource(nameof(Acceptances))]
    public void A_value_inside_its_rule_is_accepted(IValidatableCosmeticPayload payload)
        => Assert.That(ErrorsOf(payload), Is.Empty);

    /// <summary>The one rule that is a table rather than a check: a field belongs to the sorts that draw with it.</summary>
    [TestCaseSource(nameof(ForeignFields))]
    public void A_field_another_sort_owns_is_refused_by_its_name(string sort, string field)
    {
        var actor = OfSort(sort, Fill[field]);

        Assert.That(ErrorsOf(Scene(actor)), Is.EqualTo(new[] { $"actors[0] is a {sort} and has no '{field}'" }));
    }

    [TestCaseSource(nameof(OwnFields))]
    public void A_field_a_sort_owns_is_its_own(string sort, string field)
        => Assert.That(ErrorsOf(Scene(OfSort(sort, Fill[field]))), Is.Empty);

    /// <summary>A word one character off is as wrong as any other word, and refused the same way.</summary>
    [TestCaseSource(nameof(ClosedWords))]
    public void A_near_miss_of_a_closed_word_is_refused(string field, string[] words, Func<string, IValidatableCosmeticPayload> build, string refusal)
    {
        Assert.Multiple(() =>
        {
            foreach (var word in words)
            {
                Assert.That(ErrorsOf(build(word)), Is.Empty, $"{field} '{word}'");
            }

            foreach (var miss in NearMisses(words))
            {
                Assert.That(ErrorsOf(build(miss)), Is.EqualTo(new[] { refusal.Replace("{0}", miss) }), $"{field} '{miss}'");
            }
        });
    }

    private static IEnumerable<TestCaseData> ForeignFields()
    {
        foreach (var sort in Sorts)
        {
            foreach (var field in Fill.Keys.Except(Owned[sort]))
            {
                yield return new TestCaseData(sort, field);
            }
        }
    }

    private static IEnumerable<TestCaseData> OwnFields()
    {
        foreach (var sort in Sorts)
        {
            foreach (var field in Owned[sort])
            {
                yield return new TestCaseData(sort, field);
            }
        }
    }

    private static IEnumerable<TestCaseData> ClosedWords()
    {
        TestCaseData Words(string field, string[] words, Func<string, IValidatableCosmeticPayload> build, string refusal)
            => new TestCaseData(field, words, build, refusal).SetArgDisplayNames(field);

        yield return Words("reach", ["content", "card", "choice"],
            word => Scene(Wash(), scene =>
            {
                scene.Reach        = word;
                scene.DefaultReach = word is "choice" ? "card" : null;
            }),
            "reach is 'content', 'card' or 'choice'");

        yield return Words("defaultReach", ["content", "card"],
            word => Scene(Wash(), scene =>
            {
                scene.Reach        = "choice";
                scene.DefaultReach = word;
            }),
            "defaultReach is 'content' or 'card' when reach is 'choice'");

        yield return Words("tuning reach", ["content", "card"],
            word => new ProfileSceneTuning { Reach = word },
            "reach is either 'content' or 'card'");

        yield return Words("type", Sorts,
            word => Scene(Sorts.Contains(word) ? OfSort(word) : Wash(actor => actor.Type = word)),
            "actors[0].type is 'wash', 'sprite', 'band', 'wrap' or 'emitter'");

        yield return Words("source", ["atlas", "file"],
            word => Scene(Wash(actor =>
            {
                actor.Source = word;
                actor.Atlas  = word is "atlas" ? Cell() : null;
            }), scene => scene.Sheet = word is "atlas" ? new CosmeticSceneSheet { W = 256, H = 256 } : null),
            "actors[0].source is 'atlas' or 'file'");

        yield return Words("fit", ["cover", "contain", "stretch"],
            word => Scene(Wash(actor => actor.Fit = word)),
            "actors[0].fit is 'cover', 'contain' or 'stretch'");

        yield return Words("repeat", ["loop", "once", "pingPong"],
            word => Scene(Wash(actor => actor.Repeat = word)),
            "actors[0].repeat is 'loop', 'once' or 'pingPong'");

        yield return Words("depth", ["deep", "over", "front"],
            word => Scene(Wash(actor => actor.Depth = word)),
            "actors[0].depth is 'deep', 'over' or 'front'");

        yield return Words("occlude", ["avatar"],
            word => Scene(Wash(actor => actor.Occlude = [word])),
            "actors[0].occlude names '{0}', and the only part of a card that hides anything is 'avatar'");

        yield return Words("ease", ["linear", "in", "out", "inOut"],
            word => Scene(Sprite(actor => actor.Ease = word)),
            "actors[0].ease is 'linear', 'in', 'out' or 'inOut'");

        yield return Words("turn", ["none", "mirror"],
            word => Scene(Sprite(actor => actor.Turn = word)),
            "actors[0].turn is 'none' or 'mirror'");

        yield return Words("bowAxis", ["auto", "x", "y"],
            word => Scene(Sprite(actor => actor.BowAxis = word)),
            "actors[0].bowAxis is 'auto', 'x' or 'y'");

        yield return Words("anchor", Anchors,
            word => Scene(Sprite(actor => actor.From = new CosmeticScenePoint { Anchor = word })),
            $"actors[0].from.anchor is one of {AnchorWords}");

        yield return Words("band edge", ["top", "right", "bottom", "left"],
            word => Scene(Band(actor => actor.Edge = word)),
            "actors[0].edge is 'top', 'right', 'bottom' or 'left'");

        yield return Words("emitter edge", ["top", "right", "bottom", "left"],
            word => Scene(Emitter(actor => actor.Edge = word)),
            "actors[0].edge is 'top', 'right', 'bottom' or 'left'");

        yield return Words("band tile", ["stretch", "repeat", "round", "space"],
            word => Scene(Band(actor => actor.Tile = word)),
            "actors[0].tile is 'stretch', 'repeat', 'round' or 'space'");

        yield return Words("wrap tile", ["stretch", "repeat", "round", "space"],
            word => Scene(Wrap(actor => actor.Tile = word)),
            "actors[0].tile is 'stretch', 'repeat', 'round' or 'space'");

        yield return Words("grow repeat", ["loop", "once", "pingPong"],
            word => Scene(Band(actor => actor.Grow = Changed(Grow(), grow => grow.Repeat = word))),
            "actors[0].grow.repeat is 'loop', 'once' or 'pingPong'");

        yield return Words("grow from", ["edge", .. Anchors],
            word => Scene(Band(actor => actor.Grow = Changed(Grow(), grow => grow.From = word))),
            $"actors[0].grow.from is 'edge' or one of {AnchorWords}");

        yield return Words("grow leadAxis", ["y", "x", "both"],
            word => Scene(Band(actor => actor.Grow = Changed(Grow(), grow => grow.LeadAxis = word))),
            "actors[0].grow.leadAxis is 'y', 'x' or 'both'");
    }

    private static CosmeticSceneGrow Changed(CosmeticSceneGrow grow, Action<CosmeticSceneGrow> change)
    {
        change(grow);

        return grow;
    }

    private static IEnumerable<TestCaseData> SceneRefusals()
    {
        yield return Refused("no reach", Scene(Wash(), scene => scene.Reach = null), "reach is 'content', 'card' or 'choice'");

        yield return Refused("a choice with no default", Scene(Wash(), scene => scene.Reach = "choice"),
            "defaultReach is 'content' or 'card' when reach is 'choice'");

        yield return Refused("a default for a reach that is not a choice", Scene(Wash(), scene => scene.DefaultReach = "content"),
            "defaultReach is only read when reach is 'choice'");

        yield return Refused("a negative minimum width", Scene(Wash(), scene => scene.MinWidthPx = -1),
            "minWidthPx is -1, and the range is 0 to 512");

        yield return Refused("a minimum width past a picker tile", Scene(Wash(), scene => scene.MinWidthPx = 513),
            "minWidthPx is 513, and the range is 0 to 512");

        yield return Refused("no actors", new ProfileScenePayload { Reach = "card" },
            "actors needs at least one actor for there to be a scene");

        yield return Refused("an empty list of actors", new ProfileScenePayload { Reach = "card", Actors = [] },
            "actors needs at least one actor for there to be a scene");

        yield return Refused("nine actors", Scene(Wash(), scene => scene.Actors = Enumerable.Range(0, 9).Select(_ => Wash()).ToArray()),
            "actors is capped at 8");

        yield return Refused("a sheet nothing reads", Scene(Band(), scene => scene.Sheet = new CosmeticSceneSheet { W = 256, H = 256 }),
            "sheet is only read when an actor reads a rectangle out of it");

        yield return Refused("no sheet for an actor that reads one", Scene(Wash(), scene => scene.Sheet = null),
            "sheet is missing, and an actor reads a rectangle out of it");

        yield return Refused("no sheet for an actor that reads one by default", Scene(Wash(actor => actor.Source = null), scene => scene.Sheet = null),
            "sheet is missing, and an actor reads a rectangle out of it");

        yield return Refused("no sheet for the second actor", Scene(Band(), scene => scene.Actors = [Band(), Wash()]),
            "sheet is missing, and an actor reads a rectangle out of it");

        // A sheet that is itself wrong measures nothing, so nothing is measured against it.
        yield return Refused("a sheet with no width", Scene(Wash(actor => actor.Atlas!.W = 512), scene => scene.Sheet!.W = null),
            "sheet.w is missing");

        yield return Refused("a sheet of no width", Scene(Wash(), scene => scene.Sheet!.W = 0),
            "sheet.w is 0, and the range is 1 to 4096");

        yield return Refused("a sheet with no height", Scene(Wash(actor => actor.Atlas!.H = 512), scene => scene.Sheet!.H = null),
            "sheet.h is missing");

        yield return Refused("a sheet taller than a texture", Scene(Wash(), scene => scene.Sheet!.H = 4097),
            "sheet.h is 4097, and the range is 1 to 4096");

        yield return Refused("an actor of no known sort, which is checked no further",
            Scene(Wash(actor =>
            {
                actor.Type  = "cloud";
                actor.Depth = "sideways";
            })),
            "actors[0].type is 'wash', 'sprite', 'band', 'wrap' or 'emitter'");

        yield return Refused("an actor with no sort", Scene(Wash(actor => actor.Type = null)),
            "actors[0].type is 'wash', 'sprite', 'band', 'wrap' or 'emitter'");

        yield return Refused("a tuning reach of neither word", new ProfileSceneTuning { Reach = "far" }, "reach is either 'content' or 'card'");
    }

    private static IEnumerable<TestCaseData> SortRefusals()
    {
        // wash
        yield return Refused("a wash period too quick", Scene(Wash(actor => actor.PeriodMs = 239)),
            "actors[0].periodMs is 239, and the range is 240 to 600000");

        yield return Refused("a wash period past ten minutes", Scene(Wash(actor => actor.PeriodMs = 600_001)),
            "actors[0].periodMs is 600001, and the range is 240 to 600000");

        yield return Refused("a wash spill past a quarter", Scene(Wash(actor => actor.SpillPct = 26d)),
            "actors[0].spillPct is 26, and the range is 0 to 25");

        yield return Refused("a negative wash spill", Scene(Wash(actor =>
            {
                actor.SpillPct = -1d;
                actor.Fit      = "contain";
            })),
            "actors[0].spillPct is -1, and the range is 0 to 25");

        yield return Refused("a contained wash that spills", Scene(Wash(actor =>
            {
                actor.SpillPct = 4d;
                actor.Fit      = "contain";
            })),
            "actors[0].spillPct needs fit 'cover': a picture that hangs past the card has to cover it first");

        yield return Refused("a stretched wash that spills", Scene(Wash(actor =>
            {
                actor.SpillPct = 4d;
                actor.Fit      = "stretch";
            })),
            "actors[0].spillPct needs fit 'cover': a picture that hangs past the card has to cover it first");

        yield return Refused("a wash scale past eight", Scene(Wash(actor => actor.Scale = Envelope(1d, 9d))),
            "actors[0].scale[1].v is 9, and the range is 0.1 to 8");

        yield return Refused("a wash that grows with no duration", Scene(Wash(actor => actor.Grow = Changed(Grow(), grow => grow.DurationMs = null))),
            "actors[0].grow.durationMs is missing");

        // sprite
        yield return Refused("a sprite with no size", Scene(Sprite(actor => actor.SizePct = null)), "actors[0].sizePct is missing");

        yield return Refused("a sprite of no size", Scene(Sprite(actor => actor.SizePct = 0d)),
            "actors[0].sizePct is 0, and the range is 1 to 400");

        yield return Refused("a sprite four cards wide", Scene(Sprite(actor => actor.SizePct = 401d)),
            "actors[0].sizePct is 401, and the range is 1 to 400");

        yield return Refused("a sprite with no duration", Scene(Sprite(actor => actor.DurationMs = null)), "actors[0].durationMs is missing");

        yield return Refused("a sprite too quick", Scene(Sprite(actor => actor.DurationMs = 239)),
            "actors[0].durationMs is 239, and the range is 240 to 600000");

        yield return Refused("a sprite past ten minutes", Scene(Sprite(actor => actor.DurationMs = 600_001)),
            "actors[0].durationMs is 600001, and the range is 240 to 600000");

        yield return Refused("a sprite from nowhere", Scene(Sprite(actor => actor.From = null)), "actors[0].from is missing");

        yield return Refused("a sprite to nowhere", Scene(Sprite(actor => actor.To = null)), "actors[0].to is missing");

        yield return Refused("a point with no anchor, which is checked no further",
            Scene(Sprite(actor => actor.To = new CosmeticScenePoint { DxPct = 999d })),
            $"actors[0].to.anchor is one of {AnchorWords}");

        yield return Refused("a point nudged right past four cards",
            Scene(Sprite(actor => actor.From = new CosmeticScenePoint { Anchor = "top", DxPct = 401d })),
            "actors[0].from.dxPct is 401, and the range is -400 to 400");

        yield return Refused("a point nudged up past four cards",
            Scene(Sprite(actor => actor.From = new CosmeticScenePoint { Anchor = "top", DyPct = -401d })),
            "actors[0].from.dyPct is -401, and the range is -400 to 400");

        yield return Refused("a bow past twice the chord", Scene(Sprite(actor => actor.BowPct = 201d)),
            "actors[0].bowPct is 201, and the range is -200 to 200");

        yield return Refused("a bow past twice the chord the other way", Scene(Sprite(actor => actor.BowPct = -201d)),
            "actors[0].bowPct is -201, and the range is -200 to 200");

        yield return Refused("a bank past half a turn", Scene(Sprite(actor => actor.BankDeg = 181d)),
            "actors[0].bankDeg is 181, and the range is -180 to 180");

        yield return Refused("a sprite bob past a quarter", Scene(Sprite(actor => actor.BobPct = 26d)),
            "actors[0].bobPct is 26, and the range is 0 to 25");

        yield return Refused("a sprite spill past a quarter", Scene(Sprite(actor => actor.SpillPct = 26d)),
            "actors[0].spillPct is 26, and the range is 0 to 25");

        yield return Refused("a sprite scaled to nothing", Scene(Sprite(actor => actor.Scale = Envelope(0d, 1d))),
            "actors[0].scale[0].v is 0, and the range is 0.1 to 8");

        yield return Refused("a sprite spun past ten turns", Scene(Sprite(actor => actor.Rotate = Envelope(0d, 3601d))),
            "actors[0].rotate[1].v is 3601, and the range is -3600 to 3600");

        // band
        yield return Refused("a band on no edge", Scene(Band(actor => actor.Edge = null)),
            "actors[0].edge is 'top', 'right', 'bottom' or 'left'");

        yield return Refused("a band cut from the sheet", Scene(Band(actor =>
            {
                actor.Source = "atlas";
                actor.Atlas  = Cell();
            })),
            "actors[0] is a nine-slice band, so it needs a file of its own rather than a rectangle of the sheet");

        yield return Refused("a band with no slice", Scene(Band(actor => actor.Slice = null)),
            "actors[0].slice is missing; it is four numbers — top, right, bottom, left");

        yield return Refused("a band sliced three ways", Scene(Band(actor => actor.Slice = [8, 8, 8])),
            "actors[0].slice is four numbers — top, right, bottom, left — and has 3");

        yield return Refused("a band sliced outside its picture", Scene(Band(actor => actor.Slice = [8, -1, 8, 513])),
            "actors[0].slice[1] is -1, and the range is 0 to 512",
            "actors[0].slice[3] is 513, and the range is 0 to 512");

        yield return Refused("a band with no thickness", Scene(Band(actor => actor.ThicknessPct = null)),
            "actors[0].thicknessPct is missing");

        yield return Refused("a band of no thickness", Scene(Band(actor => actor.ThicknessPct = 0d)),
            "actors[0].thicknessPct is 0, and the range is 1 to 100");

        yield return Refused("a band thicker than the card", Scene(Band(actor => actor.ThicknessPct = 101d)),
            "actors[0].thicknessPct is 101, and the range is 1 to 100");

        yield return Refused("a band corner wider than the card", Scene(Band(actor => actor.CornerPct = 101d)),
            "actors[0].cornerPct is 101, and the range is 0 to 100");

        yield return Refused("a negative band corner", Scene(Band(actor => actor.CornerPct = -1d)),
            "actors[0].cornerPct is -1, and the range is 0 to 100");

        yield return Refused("a band outset past a quarter", Scene(Band(actor => actor.OutsetPct = 26d)),
            "actors[0].outsetPct is 26, and the range is 0 to 25");

        // wrap
        yield return Refused("a wrap cut from the sheet", Scene(Wrap(actor =>
            {
                actor.Source = null;
                actor.Atlas  = Cell();
            })),
            "actors[0] is a nine-slice wrap, so it needs a file of its own rather than a rectangle of the sheet");

        yield return Refused("a wrap with no slice", Scene(Wrap(actor => actor.Slice = null)),
            "actors[0].slice is missing; it is four numbers — top, right, bottom, left");

        yield return Refused("a wrap with no widths", Scene(Wrap(actor => actor.WidthPct = null)),
            "actors[0].widthPct is missing; it is four numbers — top, right, bottom, left");

        yield return Refused("a wrap of two widths", Scene(Wrap(actor => actor.WidthPct = [6d, 6d])),
            "actors[0].widthPct is four numbers — top, right, bottom, left — and has 2");

        yield return Refused("a wrap of widths outside the card", Scene(Wrap(actor => actor.WidthPct = [6d, 101d, -1d, 0d])),
            "actors[0].widthPct[1] is 101, and the range is 0 to 100",
            "actors[0].widthPct[2] is -1, and the range is 0 to 100");

        yield return Refused("a wrap drawn nowhere", Scene(Wrap(actor => actor.WidthPct = [0d, 0d, 0d, 0d])),
            "actors[0].widthPct is zero on every side, so the wrap is drawn nowhere");

        yield return Refused("a negative wrap outset", Scene(Wrap(actor => actor.OutsetPct = -1d)),
            "actors[0].outsetPct is -1, and the range is 0 to 25");

        // emitter
        yield return Refused("a scatter on no edge", Scene(Emitter(actor => actor.Edge = null)),
            "actors[0].edge is 'top', 'right', 'bottom' or 'left'");

        yield return Refused("a scatter of a file", Scene(Emitter(actor =>
            {
                actor.Source = "file";
                actor.Atlas  = null;
            })),
            "actors[0] is an emitter and reads the atlas; a self-animating file cannot be spread out");

        yield return Refused("a scatter of nothing counted", Scene(Emitter(actor => actor.Count = null)), "actors[0].count is missing");

        yield return Refused("a scatter of none", Scene(Emitter(actor => actor.Count = 0)), "actors[0].count is 0, and the range is 1 to 48");

        yield return Refused("a scatter of forty-nine", Scene(Emitter(actor => actor.Count = 49)), "actors[0].count is 49, and the range is 1 to 48");

        yield return Refused("a negative seed", Scene(Emitter(actor => actor.Seed = -1)), "actors[0].seed is -1, and the range is 0 to 65535");

        yield return Refused("a seed past two bytes", Scene(Emitter(actor => actor.Seed = 65_536)),
            "actors[0].seed is 65536, and the range is 0 to 65535");

        yield return Refused("a scatter with no size, which has no spread to compare", Scene(Emitter(actor =>
            {
                actor.SizePct    = null;
                actor.SizeVarPct = 5d;
            })),
            "actors[0].sizePct is missing");

        yield return Refused("a scatter four cards wide", Scene(Emitter(actor => actor.SizePct = 401d)),
            "actors[0].sizePct is 401, and the range is 1 to 400");

        yield return Refused("a scatter with no duration, which has no spread to compare", Scene(Emitter(actor =>
            {
                actor.DurationMs    = null;
                actor.DurationVarMs = 100;
            })),
            "actors[0].durationMs is missing");

        yield return Refused("a scatter too quick", Scene(Emitter(actor => actor.DurationMs = 239)),
            "actors[0].durationMs is 239, and the range is 240 to 600000");

        yield return Refused("a negative size spread", Scene(Emitter(actor => actor.SizeVarPct = -1d)),
            "actors[0].sizeVarPct is -1, and the range is 0 to 400");

        yield return Refused("a negative duration spread", Scene(Emitter(actor => actor.DurationVarMs = -1)),
            "actors[0].durationVarMs is -1, and the range is 0 to 600000");

        yield return Refused("a drift past two cards", Scene(Emitter(actor => actor.DriftPct = 201d)),
            "actors[0].driftPct is 201, and the range is 0 to 200");

        yield return Refused("a sway past the card", Scene(Emitter(actor => actor.SwayPct = 101d)),
            "actors[0].swayPct is 101, and the range is 0 to 100");

        yield return Refused("a sway too quick", Scene(Emitter(actor => actor.SwayPeriodMs = 239)),
            "actors[0].swayPeriodMs is 239, and the range is 240 to 120000");

        yield return Refused("a sway spread past two minutes", Scene(Emitter(actor => actor.SwayVarMs = 120_001)),
            "actors[0].swayVarMs is 120001, and the range is 0 to 120000");

        yield return Refused("a spin past ten turns", Scene(Emitter(actor => actor.SpinDeg = 3601d)),
            "actors[0].spinDeg is 3601, and the range is 0 to 3600");

        yield return Refused("a flutter past edge-on", Scene(Emitter(actor => actor.FlutterDeg = 91d)),
            "actors[0].flutterDeg is 91, and the range is 0 to 90");

        yield return Refused("a scatter bob past a quarter", Scene(Emitter(actor => actor.BobPct = 26d)),
            "actors[0].bobPct is 26, and the range is 0 to 25");

        yield return Refused("a depth spread past whole", Scene(Emitter(actor => actor.DepthSpreadPct = 101d)),
            "actors[0].depthSpreadPct is 101, and the range is 0 to 100");

        yield return Refused("a size spread wider than the size", Scene(Emitter(actor => actor.SizeVarPct = 5d)),
            "actors[0].sizeVarPct is wider than sizePct, so some copies come out at no size at all");

        yield return Refused("a duration spread wider than the duration", Scene(Emitter(actor => actor.DurationVarMs = 9001)),
            "actors[0].durationVarMs is wider than durationMs, so some copies never finish");

        yield return Refused("a sway spread wider than the sway", Scene(Emitter(actor =>
            {
                actor.SwayPeriodMs = 1000;
                actor.SwayVarMs    = 1001;
            })),
            "actors[0].swayVarMs is wider than swayPeriodMs, so some copies come out with no sway at all");
    }

    private static IEnumerable<TestCaseData> CommonRefusals()
    {
        yield return Refused("a source of neither kind, which reads no sheet", Scene(Wash(actor => actor.Source = "stream"), scene => scene.Sheet = null),
            "actors[0].source is 'atlas' or 'file'");

        yield return Refused("a sheet reader with no rectangle", Scene(Wash(actor => actor.Atlas = null)),
            "actors[0].atlas is missing, and this actor reads the sheet");

        yield return Refused("a file with a rectangle", Scene(Wash(actor => actor.Source = "file")),
            "actors[0] draws a whole file and has no atlas rectangle");

        yield return Refused("a rectangle told to replay", Scene(Wash(actor => actor.Replay = true)),
            "actors[0].replay is only read for a file; a rectangle of the sheet has no clock of its own");

        yield return Refused("a slot that does not exist", Scene(Wash(actor => actor.Slot = "Nowhere")),
            "actors[0].slot 'Nowhere' is not an asset slot");

        yield return Refused("a slot by a number no slot has", Scene(Wash(actor => actor.Slot = "9")),
            "actors[0].slot '9' is not an asset slot");

        yield return Refused("two parts of a card that hide nothing", Scene(Wash(actor => actor.Occlude = ["avatar", "glass", "name"])),
            "actors[0].occlude names 'glass', and the only part of a card that hides anything is 'avatar'",
            "actors[0].occlude names 'name', and the only part of a card that hides anything is 'avatar'");

        yield return Refused("a delay past two minutes", Scene(Wash(actor => actor.DelayMs = 120_001)),
            "actors[0].delayMs is 120001, and the range is -120000 to 120000");

        yield return Refused("a delay past two minutes ago", Scene(Wash(actor => actor.DelayMs = -120_001)),
            "actors[0].delayMs is -120001, and the range is -120000 to 120000");

        yield return Refused("an opacity past opaque", Scene(Wash(actor => actor.Opacity = Envelope(2d, 1d))),
            "actors[0].opacity[0].v is 2, and the range is 0 to 1");
    }

    private static IEnumerable<TestCaseData> PartRefusals()
    {
        CosmeticSceneStop Stop(double? at, double? value) => new() { At = at, V = value };

        ProfileScenePayload Opacity(params CosmeticSceneStop[] stops) => Scene(Wash(actor => actor.Opacity = stops));

        // envelope
        yield return Refused("an envelope of no stops", Opacity(),
            "actors[0].opacity needs at least two stops; one stop is a constant, and a constant is the field's own value");

        yield return Refused("an envelope of one stop", Opacity(Stop(0d, 1d)),
            "actors[0].opacity needs at least two stops; one stop is a constant, and a constant is the field's own value");

        yield return Refused("an envelope of nine stops",
            Opacity(Enumerable.Range(0, 9).Select(index => Stop(index / 8d, 1d)).ToArray()),
            "actors[0].opacity is capped at 8 stops");

        yield return Refused("a stop with no place, which ends the reading", Opacity(Stop(0d, 1d), Stop(null, 9d), Stop(1d, 1d)),
            "actors[0].opacity[1].at is missing");

        yield return Refused("a stop before the cycle", Opacity(Stop(-0.5d, 1d), Stop(1d, 1d)),
            "actors[0].opacity[0].at is -0.5, and the range is 0 to 1");

        yield return Refused("a stop after the cycle", Opacity(Stop(0d, 1d), Stop(1.5d, 1d)),
            "actors[0].opacity[1].at is 1.5, and the range is 0 to 1");

        yield return Refused("a stop that does not move on", Opacity(Stop(0d, 1d), Stop(0d, 1d), Stop(1d, 1d)),
            "actors[0].opacity[1].at is 0, which does not come after 0");

        yield return Refused("a stop with no value", Opacity(Stop(0d, null), Stop(1d, 1d)),
            "actors[0].opacity[0].v is missing");

        yield return Refused("two values out of range, both named", Opacity(Stop(0d, 2d), Stop(1d, -1d)),
            "actors[0].opacity[0].v is 2, and the range is 0 to 1",
            "actors[0].opacity[1].v is -1, and the range is 0 to 1");

        yield return Refused("an envelope that starts late", Opacity(Stop(0.2d, 1d), Stop(1d, 1d)),
            "actors[0].opacity starts at 0.2, and an envelope covers the whole cycle from 0");

        yield return Refused("an envelope that ends early", Opacity(Stop(0d, 1d), Stop(0.8d, 1d)),
            "actors[0].opacity ends at 0.8, and an envelope covers the whole cycle to 1");

        // grow
        ProfileScenePayload Growing(Action<CosmeticSceneGrow> change) => Scene(Band(actor => actor.Grow = Changed(Grow(), change)));

        yield return Refused("a grow from nowhere", Growing(grow => grow.FromPct = null), "actors[0].grow.fromPct is missing");

        yield return Refused("a grow from below nothing", Growing(grow => grow.FromPct = -1d),
            "actors[0].grow.fromPct is -1, and the range is 0 to 100");

        yield return Refused("a grow to nowhere", Growing(grow => grow.ToPct = null), "actors[0].grow.toPct is missing");

        yield return Refused("a grow past whole", Growing(grow => grow.ToPct = 101d),
            "actors[0].grow.toPct is 101, and the range is 0 to 100");

        yield return Refused("a grow too quick", Growing(grow => grow.DurationMs = 239),
            "actors[0].grow.durationMs is 239, and the range is 240 to 600000");

        yield return Refused("a negative hold", Growing(grow => grow.HoldMs = -1),
            "actors[0].grow.holdMs is -1, and the range is 0 to 600000");

        yield return Refused("a grow from an empty word", Growing(grow => grow.From = string.Empty),
            $"actors[0].grow.from is 'edge' or one of {AnchorWords}");

        yield return Refused("a grow from nothing at all", Growing(grow => grow.FromScale = 0d),
            "actors[0].grow.fromScale is 0, and the range is 0.01 to 1");

        yield return Refused("a grow that shrinks", Growing(grow => grow.FromScale = 1.5d),
            "actors[0].grow.fromScale is 1.5, and the range is 0.01 to 1");

        yield return Refused("a wobble past an eighth of a turn", Growing(grow => grow.WobbleDeg = 46d),
            "actors[0].grow.wobbleDeg is 46, and the range is 0 to 45");

        yield return Refused("an edge softer than soft", Growing(grow => grow.Softness = 1.5d),
            "actors[0].grow.softness is 1.5, and the range is 0 to 1");

        yield return Refused("a sway with no lean", Growing(grow => grow.Sway = new CosmeticSceneSway { Ms = 5000 }),
            "actors[0].grow.sway.deg is missing");

        yield return Refused("a sway past an eighth of a turn", Growing(grow => grow.Sway = new CosmeticSceneSway { Deg = 46d, Ms = 5000 }),
            "actors[0].grow.sway.deg is 46, and the range is 0 to 45");

        yield return Refused("a sway with no period", Growing(grow => grow.Sway = new CosmeticSceneSway { Deg = 3d }),
            "actors[0].grow.sway.ms is missing");

        yield return Refused("a sway too quick", Growing(grow => grow.Sway = new CosmeticSceneSway { Deg = 3d, Ms = 239 }),
            "actors[0].grow.sway.ms is 239, and the range is 240 to 600000");

        yield return Refused("a grow that runs inwards", Growing(grow =>
            {
                grow.FromPct = 60d;
                grow.ToPct   = 40d;
            }),
            "actors[0].grow.fromPct is 60 and toPct is 40; growth runs outwards");

        yield return Refused("a grow whose start is wrong is not also called backwards", Growing(grow =>
            {
                grow.FromPct = -1d;
                grow.ToPct   = 40d;
            }),
            "actors[0].grow.fromPct is -1, and the range is 0 to 100");

        yield return Refused("a grow whose end is wrong is not also called backwards", Growing(grow =>
            {
                grow.FromPct = 60d;
                grow.ToPct   = null;
            }),
            "actors[0].grow.toPct is missing");

        // retreat
        ProfileScenePayload Retreating(Action<CosmeticSceneRetreat> change)
            => Scene(Wash(actor =>
            {
                actor.Retreat = Retreat();
                change(actor.Retreat);
            }));

        yield return Refused("a retreat that never starts", Retreating(retreat => retreat.AtMs = null), "actors[0].retreat.atMs is missing");

        yield return Refused("a retreat before the card opens", Retreating(retreat => retreat.AtMs = -1),
            "actors[0].retreat.atMs is -1, and the range is 0 to 600000");

        yield return Refused("a retreat that takes no time", Retreating(retreat => retreat.DurationMs = null),
            "actors[0].retreat.durationMs is missing");

        yield return Refused("a retreat too quick", Retreating(retreat => retreat.DurationMs = 239),
            "actors[0].retreat.durationMs is 239, and the range is 240 to 600000");

        yield return Refused("an edge strip past half", Retreating(retreat => retreat.EdgePct = 51d),
            "actors[0].retreat.edgePct is 51, and the range is 0 to 50");

        yield return Refused("a foot strip past half", Retreating(retreat => retreat.FootPct = 51d),
            "actors[0].retreat.footPct is 51, and the range is 0 to 50");

        yield return Refused("a soft edge past a quarter", Retreating(retreat => retreat.SoftPct = 26d),
            "actors[0].retreat.softPct is 26, and the range is 0 to 25");

        yield return Refused("a retreat that leaves everything", Retreating(retreat => retreat.Remain = 1d),
            "actors[0].retreat.remain is 1; a retreat that leaves everything behind is not a retreat");

        yield return Refused("a retreat that leaves less than nothing", Retreating(retreat => retreat.Remain = -0.5d),
            "actors[0].retreat.remain is -0.5, and the range is 0 to 1");

        // shadow
        ProfileScenePayload Shadowed(Action<CosmeticSceneShadow> change)
            => Scene(Wash(actor =>
            {
                actor.Shadow = new CosmeticSceneShadow { DxPct = 0.5d, DyPct = 1d, BlurPct = 1d, Alpha = 0.3d };
                change(actor.Shadow);
            }));

        yield return Refused("a shadow thrown nowhere across", Shadowed(shadow => shadow.DxPct = null), "actors[0].shadow.dxPct is missing");

        yield return Refused("a shadow thrown past half a card", Shadowed(shadow => shadow.DxPct = 51d),
            "actors[0].shadow.dxPct is 51, and the range is -50 to 50");

        yield return Refused("a shadow thrown up past half a card", Shadowed(shadow => shadow.DyPct = -51d),
            "actors[0].shadow.dyPct is -51, and the range is -50 to 50");

        yield return Refused("a shadow with no blur", Shadowed(shadow => shadow.BlurPct = null), "actors[0].shadow.blurPct is missing");

        yield return Refused("a shadow blurred past half a card", Shadowed(shadow => shadow.BlurPct = 51d),
            "actors[0].shadow.blurPct is 51, and the range is 0 to 50");

        yield return Refused("a shadow with no alpha", Shadowed(shadow => shadow.Alpha = null), "actors[0].shadow.alpha is missing");

        yield return Refused("a shadow past opaque", Shadowed(shadow => shadow.Alpha = 1.5d),
            "actors[0].shadow.alpha is 1.5, and the range is 0 to 1");
    }

    private static IEnumerable<TestCaseData> AtlasRefusals()
    {
        ProfileScenePayload Cut(Action<CosmeticSceneAtlas> change) => Scene(Wash(actor => change(actor.Atlas!)));

        yield return Refused("a rectangle left of the sheet", Cut(atlas => atlas.X = -1), "actors[0].atlas.x is -1, and the range is 0 to 4096");

        yield return Refused("a rectangle below any texture", Cut(atlas => atlas.Y = 4097),
            "actors[0].atlas.y is 4097, and the range is 0 to 4096",
            "actors[0].atlas runs 3857px past the bottom of the sheet");

        yield return Refused("a rectangle with no width, which is not measured", Cut(atlas =>
            {
                atlas.W = null;
                atlas.X = 4000;
            }),
            "actors[0].atlas.w is missing");

        yield return Refused("a rectangle of no height, which is not measured", Cut(atlas =>
            {
                atlas.H = 0;
                atlas.Y = 4000;
            }),
            "actors[0].atlas.h is 0, and the range is 1 to 4096");

        yield return Refused("one picture with columns", Cut(atlas => atlas.Columns = 4),
            "actors[0].atlas names no frames, so it is one picture and has no columns, fps or still");

        yield return Refused("one picture with a frame rate", Cut(atlas => atlas.Fps = 8),
            "actors[0].atlas names no frames, so it is one picture and has no columns, fps or still");

        yield return Refused("one picture with a still frame", Cut(atlas => atlas.Still = 0),
            "actors[0].atlas names no frames, so it is one picture and has no columns, fps or still");

        yield return Refused("one picture past the right edge", Cut(atlas => atlas.X = 248),
            "actors[0].atlas runs 8px past the right edge of the sheet");

        yield return Refused("one picture past the bottom", Cut(atlas => atlas.Y = 248),
            "actors[0].atlas runs 8px past the bottom of the sheet");

        yield return Refused("one picture with no corner, measured from the sheet's", Cut(atlas =>
            {
                atlas.X = null;
                atlas.Y = null;
                atlas.W = 300;
                atlas.H = 260;
            }),
            "actors[0].atlas runs 44px past the right edge of the sheet",
            "actors[0].atlas runs 4px past the bottom of the sheet");

        ProfileScenePayload Strip(int? frames, int? columns, Action<CosmeticSceneAtlas>? change = null)
            => Cut(atlas =>
            {
                atlas.Frames  = frames;
                atlas.Columns = columns;
                change?.Invoke(atlas);
            });

        yield return Refused("a strip of no frames, which is checked no further", Strip(0, 4, atlas => atlas.Still = 9),
            "actors[0].atlas.frames is 0, and the range is 1 to 64");

        yield return Refused("a strip of sixty-five frames", Strip(65, 5), "actors[0].atlas.frames is 65, and the range is 1 to 64");

        yield return Refused("a strip with no columns, which is checked no further", Strip(4, null, atlas => atlas.Still = 9),
            "actors[0].atlas.columns is missing");

        yield return Refused("a strip of no columns", Strip(4, 0), "actors[0].atlas.columns is 0, and the range is 1 to 64");

        yield return Refused("a strip played too fast", Strip(4, 4, atlas => atlas.Fps = 61), "actors[0].atlas.fps is 61, and the range is 1 to 60");

        yield return Refused("a strip played not at all", Strip(4, 4, atlas => atlas.Fps = 0), "actors[0].atlas.fps is 0, and the range is 1 to 60");

        yield return Refused("more columns than frames", Strip(2, 4), "actors[0].atlas.frames must divide evenly into rows of 4");

        yield return Refused("a last row that runs out, whose still is still checked", Strip(6, 4, atlas => atlas.Still = 6),
            "actors[0].atlas.frames must divide evenly into rows of 4",
            "actors[0].atlas.still names frame 6, and the strip has 6");

        yield return Refused("a still frame past the strip", Strip(4, 4, atlas => atlas.Still = 4),
            "actors[0].atlas.still names frame 4, and the strip has 4");

        yield return Refused("a still frame before the strip", Strip(4, 4, atlas => atlas.Still = -1),
            "actors[0].atlas.still names frame -1, and the strip has 4");

        yield return Refused("a strip whose later frames run off the right", Strip(8, 4, atlas =>
            {
                atlas.W = 64;
                atlas.X = 16;
            }),
            "actors[0].atlas runs 16px past the right edge of the sheet");

        yield return Refused("a strip whose later rows run off the bottom", Strip(16, 4, atlas => atlas.H = 80),
            "actors[0].atlas runs 64px past the bottom of the sheet");
    }

    private static IEnumerable<TestCaseData> Acceptances()
    {
        yield return Accepted("a choice with a default of content", Scene(Wash(), scene =>
        {
            scene.Reach        = "choice";
            scene.DefaultReach = "content";
        }));

        yield return Accepted("a reach of content", Scene(Wash(), scene => scene.Reach = "content"));
        yield return Accepted("the smallest minimum width", Scene(Wash(), scene => scene.MinWidthPx = 0));
        yield return Accepted("the largest minimum width", Scene(Wash(), scene => scene.MinWidthPx = 512));
        yield return Accepted("eight actors", Scene(Wash(), scene => scene.Actors = Enumerable.Range(0, 8).Select(_ => Wash()).ToArray()));
        yield return Accepted("a file and then a sheet reader", Scene(Band(), scene =>
        {
            scene.Actors = [Band(), Wash()];
            scene.Sheet  = new CosmeticSceneSheet { W = 256, H = 256 };
        }));

        yield return Accepted("only files and no sheet", Scene(Band(), scene => scene.Actors = [Band(), Wrap()]));
        yield return Accepted("a covered wash that spills", Scene(Wash(actor =>
        {
            actor.SpillPct = 25d;
            actor.Fit      = "cover";
        })));

        yield return Accepted("a wash that spills with no fit named", Scene(Wash(actor => actor.SpillPct = 4d)));
        yield return Accepted("a wash that spills with an empty fit", Scene(Wash(actor =>
        {
            actor.SpillPct = 4d;
            actor.Fit      = string.Empty;
        })));

        yield return Accepted("a contained wash that does not spill", Scene(Wash(actor =>
        {
            actor.SpillPct = 0d;
            actor.Fit      = "contain";
        })));

        yield return Accepted("empty words where a word may be left out", Scene(Sprite(actor =>
        {
            actor.Ease    = string.Empty;
            actor.Turn    = string.Empty;
            actor.BowAxis = string.Empty;
            actor.Repeat  = string.Empty;
            actor.Depth   = string.Empty;
            actor.Slot    = string.Empty;
            actor.Occlude = [];
        })));

        yield return Accepted("a point nudged to the edge of its range", Scene(Sprite(actor =>
            actor.To = new CosmeticScenePoint { Anchor = "center", DxPct = -400d, DyPct = 400d })));

        yield return Accepted("a rectangle told not to replay", Scene(Wash(actor => actor.Replay = false)));
        yield return Accepted("a file told to replay", Scene(Wash(actor =>
        {
            actor.Source = "file";
            actor.Atlas  = null;
            actor.Replay = true;
            actor.Slot   = "Poster";
        })));

        foreach (var slot in Enum.GetNames<CosmeticAssetSlot>())
        {
            yield return Accepted($"the {slot} slot", Scene(Wash(actor => actor.Slot = slot)));
        }

        yield return Accepted("hidden by the avatar", Scene(Wash(actor => actor.Occlude = ["avatar"])));
        yield return Accepted("an envelope of eight stops",
            Scene(Wash(actor => actor.Opacity = Enumerable.Range(0, 8).Select(index => new CosmeticSceneStop { At = index / 7d, V = 1d }).ToArray())));

        yield return Accepted("a wrap of empty words", Scene(Wrap(actor => actor.Tile = string.Empty)));
        yield return Accepted("a scatter spread exactly as wide as itself", Scene(Emitter(actor =>
        {
            actor.SizeVarPct    = 4d;
            actor.DurationVarMs = 9000;
            actor.SwayPeriodMs  = 1000;
            actor.SwayVarMs     = 1000;
        })));

        yield return Accepted("a sway spread with no sway period to spread", Scene(Emitter(actor => actor.SwayVarMs = 1001)));
        yield return Accepted("a grow whose ends meet", Scene(Band(actor => actor.Grow = Changed(Grow(), grow =>
        {
            grow.FromPct   = 50d;
            grow.ToPct     = 50d;
            grow.LeadAxis  = string.Empty;
            grow.Repeat    = string.Empty;
            grow.HoldMs    = 0;
            grow.FromScale = 0.01d;
            grow.WobbleDeg = 45d;
            grow.Softness  = 1d;
            grow.Sway      = new CosmeticSceneSway { Deg = 45d, Ms = 240 };
        }))));

        yield return Accepted("a retreat that keeps half of itself", Scene(Wash(actor => actor.Retreat = new CosmeticSceneRetreat
        {
            AtMs       = 0,
            DurationMs = 240,
            EdgePct    = 50d,
            FootPct    = 50d,
            SoftPct    = 25d,
            Remain     = 0.5d
        })));

        yield return Accepted("a strip that fills the sheet exactly", Scene(Wash(actor => actor.Atlas = new CosmeticSceneAtlas
        {
            X       = 0,
            Y       = 0,
            W       = 64,
            H       = 128,
            Frames  = 8,
            Columns = 4,
            Fps     = 60,
            Still   = 7
        })));

        yield return Accepted("a tuning that says nothing", new ProfileSceneTuning());
        yield return Accepted("a tuning with an empty reach", new ProfileSceneTuning { Reach = string.Empty });
    }
}
