namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using ArgonContracts;

/// <summary>
/// Every word and every number of a scene, as Ion carries it.
/// </summary>
/// <remarks>
/// <para>The stored document says what a scene is in words; the wire says it in cases. Each word of
/// every closed list has exactly one case, and a field the row left out arrives absent rather than
/// as a default — the defaults are the renderer's.</para>
///
/// <para>The mapping is only ever handed a document that passed validation. The last few tests hand
/// it one that did not, to pin what it does then: a word a scene cannot be drawn without stops the
/// mapping outright, and an optional word it has no case for is left out.</para>
/// </remarks>
[TestFixture]
public class CosmeticSceneWireTests
{
    private static readonly string[] Anchors =
        ["topLeft", "top", "topRight", "left", "center", "right", "bottomLeft", "bottom", "bottomRight"];

    private static CosmeticSceneActor Wash() => new() { Type = "wash", Source = "atlas", Atlas = new CosmeticSceneAtlas { W = 16, H = 16 } };

    private static CosmeticSceneActor Sprite() => new()
    {
        Type       = "sprite",
        SizePct    = 20d,
        DurationMs = 4000,
        From       = new CosmeticScenePoint { Anchor = "topRight" },
        To         = new CosmeticScenePoint { Anchor = "bottomLeft" }
    };

    private static CosmeticSceneActor Band() => new()
    {
        Type         = "band",
        Source       = "file",
        Edge         = "bottom",
        Slice        = [1, 2, 3, 4],
        ThicknessPct = 10d
    };

    private static CosmeticSceneActor Wrap() => new()
    {
        Type     = "wrap",
        Source   = "file",
        Slice    = [8, 8, 8, 8],
        WidthPct = [1d, 2d, 3d, 4d]
    };

    private static CosmeticSceneActor Emitter() => new()
    {
        Type       = "emitter",
        Edge       = "top",
        Count      = 12,
        SizePct    = 4d,
        DurationMs = 9000
    };

    private static CosmeticSceneGrow Grow() => new() { FromPct = 0d, ToPct = 100d, DurationMs = 2000 };

    private static PayloadProfileScene Wire(ProfileScenePayload scene) => (PayloadProfileScene)scene.ToWire();

    private static PayloadProfileScene Wire(params CosmeticSceneActor[] actors)
        => Wire(new ProfileScenePayload { Reach = "card", Actors = actors });

    private static T Only<T>(CosmeticSceneActor actor) where T : ISceneActor => (T)Wire(actor).actors.Single();

    private static CosmeticSceneActor With(CosmeticSceneActor actor, Action<CosmeticSceneActor> change)
    {
        change(actor);

        return actor;
    }

    /// <summary>The same near misses the validation is held to; see <c>CosmeticSceneRefusalTests</c>.</summary>
    private static IEnumerable<string> NearMisses(params string[] words)
    {
        var misses = new HashSet<string> { "~", string.Empty };

        foreach (var word in words)
        {
            for (var index = 0; index < word.Length; index++)
            {
                misses.Add(string.Concat(word.AsSpan(0, index), "~", word.AsSpan(index + 1)));
            }

            misses.Add(word[..^1]);
            misses.Add(word + "~");
        }

        misses.ExceptWith(words);

        return misses;
    }

    [TestCase("content", SceneReachRule.Content)]
    [TestCase("card", SceneReachRule.Card)]
    [TestCase("choice", SceneReachRule.Choice)]
    public void A_reach_rule_is_its_case(string reach, SceneReachRule expected)
        => Assert.That(Wire(new ProfileScenePayload { Reach = reach, Actors = [Wash()] }).reach, Is.EqualTo(expected));

    [TestCase("content", SceneReach.Content)]
    [TestCase("card", SceneReach.Card)]
    [TestCase(null, null)]
    public void A_default_reach_is_its_case_or_absent(string? reach, SceneReach? expected)
        => Assert.That(Wire(new ProfileScenePayload { Reach = "choice", DefaultReach = reach, Actors = [Wash()] }).defaultReach, Is.EqualTo(expected));

    [Test]
    public void The_scene_numbers_arrive_as_the_row_said_them()
    {
        var said   = Wire(new ProfileScenePayload { Reach = "card", MinWidthPx = 240, Sheet = new CosmeticSceneSheet { W = 512, H = 64 }, Actors = [Wash()] });
        var silent = Wire(new ProfileScenePayload { Reach = "card", Actors = [Band()] });

        Assert.Multiple(() =>
        {
            Assert.That(said.minWidthPx, Is.EqualTo((ushort)240));
            Assert.That(said.sheet, Is.EqualTo(new SceneSheet(512, 64)));
            Assert.That(silent.minWidthPx, Is.Null);
            Assert.That(silent.sheet, Is.Null);
        });
    }

    [TestCase(CosmeticAssetSlot.Primary, AssetSlot.Primary)]
    [TestCase(CosmeticAssetSlot.Secondary, AssetSlot.Secondary)]
    [TestCase(CosmeticAssetSlot.Tertiary, AssetSlot.Tertiary)]
    [TestCase(CosmeticAssetSlot.Quaternary, AssetSlot.Quaternary)]
    [TestCase(CosmeticAssetSlot.Poster, AssetSlot.Poster)]
    public void A_slot_is_named_as_the_asset_store_names_it(CosmeticAssetSlot slot, AssetSlot expected)
        => Assert.That(Only<SceneBand>(With(Band(), actor => actor.Slot = slot.ToString())).slot, Is.EqualTo(expected));

    [TestCase(null)]
    [TestCase("")]
    public void An_actor_that_names_no_slot_has_none(string? slot)
        => Assert.That(Only<SceneBand>(With(Band(), actor => actor.Slot = slot)).slot, Is.Null);

    [TestCase("atlas", SceneSource.Atlas)]
    [TestCase("file", SceneSource.File)]
    [TestCase(null, null)]
    public void A_source_is_its_case_or_absent(string? source, SceneSource? expected)
        => Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Source = source)).source, Is.EqualTo(expected));

    [TestCase("deep", SceneDepth.Deep)]
    [TestCase("over", SceneDepth.Over)]
    [TestCase("front", SceneDepth.Front)]
    [TestCase(null, null)]
    public void A_depth_is_its_case_or_absent(string? depth, SceneDepth? expected)
        => Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Depth = depth)).depth, Is.EqualTo(expected));

    [TestCase("cover", SceneFit.Cover)]
    [TestCase("contain", SceneFit.Contain)]
    [TestCase("stretch", SceneFit.Stretch)]
    [TestCase(null, null)]
    public void A_fit_is_its_case_or_absent(string? fit, SceneFit? expected)
        => Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Fit = fit)).fit, Is.EqualTo(expected));

    [TestCase("loop", SceneRepeat.Loop)]
    [TestCase("once", SceneRepeat.Once)]
    [TestCase("pingPong", SceneRepeat.PingPong)]
    [TestCase(null, null)]
    public void A_repeat_is_its_case_or_absent_on_every_sort_that_has_one(string? repeat, SceneRepeat? expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Repeat = repeat)).repeat, Is.EqualTo(expected));
            Assert.That(Only<SceneSprite>(With(Sprite(), actor => actor.Repeat = repeat)).repeat, Is.EqualTo(expected));
            Assert.That(Only<SceneEmitter>(With(Emitter(), actor => actor.Repeat = repeat)).repeat, Is.EqualTo(expected));

            var grow = Grow();

            grow.Repeat = repeat;

            Assert.That(Only<SceneBand>(With(Band(), actor => actor.Grow = grow)).grow!.repeat, Is.EqualTo(expected));
        });
    }

    [TestCase("linear", SceneEase.Linear)]
    [TestCase("in", SceneEase.In)]
    [TestCase("out", SceneEase.Out)]
    [TestCase("inOut", SceneEase.InOut)]
    [TestCase(null, null)]
    public void An_ease_is_its_case_or_absent(string? ease, SceneEase? expected)
        => Assert.That(Only<SceneSprite>(With(Sprite(), actor => actor.Ease = ease)).ease, Is.EqualTo(expected));

    [TestCase("none", SceneTurn.None)]
    [TestCase("mirror", SceneTurn.Mirror)]
    [TestCase(null, null)]
    public void A_turn_is_its_case_or_absent(string? turn, SceneTurn? expected)
        => Assert.That(Only<SceneSprite>(With(Sprite(), actor => actor.Turn = turn)).turn, Is.EqualTo(expected));

    [TestCase("auto", SceneBowAxis.Auto)]
    [TestCase("x", SceneBowAxis.X)]
    [TestCase("y", SceneBowAxis.Y)]
    [TestCase(null, null)]
    public void A_bow_axis_is_its_case_or_absent(string? axis, SceneBowAxis? expected)
        => Assert.That(Only<SceneSprite>(With(Sprite(), actor => actor.BowAxis = axis)).bowAxis, Is.EqualTo(expected));

    [TestCase("topLeft", SceneAnchor.TopLeft)]
    [TestCase("top", SceneAnchor.Top)]
    [TestCase("topRight", SceneAnchor.TopRight)]
    [TestCase("left", SceneAnchor.Left)]
    [TestCase("center", SceneAnchor.Center)]
    [TestCase("right", SceneAnchor.Right)]
    [TestCase("bottomLeft", SceneAnchor.BottomLeft)]
    [TestCase("bottom", SceneAnchor.Bottom)]
    [TestCase("bottomRight", SceneAnchor.BottomRight)]
    public void An_anchor_is_its_case_at_either_end_of_a_path(string anchor, SceneAnchor expected)
    {
        var sprite = Only<SceneSprite>(With(Sprite(), actor =>
        {
            actor.From = new CosmeticScenePoint { Anchor = anchor, DxPct = 1.5d };
            actor.To   = new CosmeticScenePoint { Anchor = anchor, DyPct = -2.5d };
        }));

        Assert.Multiple(() =>
        {
            Assert.That(sprite.from, Is.EqualTo(new ScenePoint(expected, 1.5d, null)));
            Assert.That(sprite.to, Is.EqualTo(new ScenePoint(expected, null, -2.5d)));
        });
    }

    [TestCase("top", SceneEdge.Top)]
    [TestCase("right", SceneEdge.Right)]
    [TestCase("bottom", SceneEdge.Bottom)]
    [TestCase("left", SceneEdge.Left)]
    public void An_edge_is_its_case_on_a_band_and_a_scatter(string edge, SceneEdge expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(Only<SceneBand>(With(Band(), actor => actor.Edge = edge)).edge, Is.EqualTo(expected));
            Assert.That(Only<SceneEmitter>(With(Emitter(), actor => actor.Edge = edge)).edge, Is.EqualTo(expected));
        });
    }

    [TestCase("stretch", SceneTile.Stretch)]
    [TestCase("repeat", SceneTile.Repeat)]
    [TestCase("round", SceneTile.Round)]
    [TestCase("space", SceneTile.Space)]
    [TestCase(null, null)]
    public void A_tile_is_its_case_or_absent_on_both_nine_slices(string? tile, SceneTile? expected)
    {
        Assert.Multiple(() =>
        {
            Assert.That(Only<SceneBand>(With(Band(), actor => actor.Tile = tile)).tile, Is.EqualTo(expected));
            Assert.That(Only<SceneWrap>(With(Wrap(), actor => actor.Tile = tile)).tile, Is.EqualTo(expected));
        });
    }

    [TestCase("y", SceneLeadAxis.Y)]
    [TestCase("x", SceneLeadAxis.X)]
    [TestCase("both", SceneLeadAxis.Both)]
    [TestCase(null, null)]
    public void A_lead_axis_is_its_case_or_absent(string? axis, SceneLeadAxis? expected)
    {
        var grow = Grow();

        grow.LeadAxis = axis;

        Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Grow = grow)).grow!.leadAxis, Is.EqualTo(expected));
    }

    [TestCase(null, null)]
    [TestCase("edge", SceneGrowOrigin.Edge)]
    [TestCase("topLeft", SceneGrowOrigin.TopLeft)]
    [TestCase("top", SceneGrowOrigin.Top)]
    [TestCase("topRight", SceneGrowOrigin.TopRight)]
    [TestCase("left", SceneGrowOrigin.Left)]
    [TestCase("center", SceneGrowOrigin.Center)]
    [TestCase("right", SceneGrowOrigin.Right)]
    [TestCase("bottomLeft", SceneGrowOrigin.BottomLeft)]
    [TestCase("bottom", SceneGrowOrigin.Bottom)]
    [TestCase("bottomRight", SceneGrowOrigin.BottomRight)]
    public void A_grow_origin_is_its_case_or_absent(string? origin, SceneGrowOrigin? expected)
    {
        var grow = Grow();

        grow.From = origin;

        Assert.That(Only<SceneWrap>(With(Wrap(), actor => actor.Grow = grow)).grow!.from, Is.EqualTo(expected));
    }

    [Test]
    public void What_every_sort_shares_arrives_as_the_row_said_it()
    {
        var wash = Only<SceneWash>(With(Wash(), actor =>
        {
            actor.Slot    = "Primary";
            actor.Replay  = false;
            actor.Occlude = ["avatar"];
            actor.DelayMs = -500;
            actor.Opacity = [new CosmeticSceneStop { At = 0d, V = 0.25d }, new CosmeticSceneStop { At = 1d, V = 1d }];
            actor.Shadow  = new CosmeticSceneShadow { DxPct = 0.5d, DyPct = -1d, BlurPct = 2d, Alpha = 0.3d };
        }));

        var bare = Only<SceneWash>(With(Wash(), actor => actor.Atlas = null));

        Assert.Multiple(() =>
        {
            Assert.That(wash.slot, Is.EqualTo(AssetSlot.Primary));
            Assert.That(wash.replay, Is.False);
            Assert.That(wash.occlude, Is.EqualTo(new[] { SceneOccluder.Avatar }));
            Assert.That(wash.delayMs, Is.EqualTo(-500));
            Assert.That(wash.opacity, Is.EqualTo(new[] { new SceneStop(0d, 0.25d), new SceneStop(1d, 1d) }));
            Assert.That(wash.shadow, Is.EqualTo(new SceneShadow(0.5d, -1d, 2d, 0.3d)));

            Assert.That(bare.atlas, Is.Null);
            Assert.That(bare.replay, Is.Null);
            Assert.That(bare.occlude, Is.Null);
            Assert.That(bare.delayMs, Is.Null);
            Assert.That(bare.opacity, Is.Null);
            Assert.That(bare.shadow, Is.Null);
        });
    }

    [Test]
    public void An_atlas_says_only_what_the_row_said()
    {
        var strip = Only<SceneWash>(With(Wash(), actor => actor.Atlas = new CosmeticSceneAtlas
        {
            X       = 16,
            Y       = 32,
            W       = 64,
            H       = 48,
            Frames  = 8,
            Columns = 4,
            Fps     = 12,
            Still   = 3
        }));

        var cell = Only<SceneWash>(Wash());

        Assert.Multiple(() =>
        {
            Assert.That(strip.atlas, Is.EqualTo(new SceneAtlas(16, 32, 64, 48, 8, 4, 12, 3)));
            Assert.That(cell.atlas, Is.EqualTo(new SceneAtlas(null, null, 16, 16, null, null, null, null)));
        });
    }

    [Test]
    public void A_wash_arrives_with_every_number_it_carries()
    {
        var said = Only<SceneWash>(With(Wash(), actor =>
        {
            actor.PeriodMs = 6000;
            actor.Scale    = [new CosmeticSceneStop { At = 0d, V = 1d }, new CosmeticSceneStop { At = 1d, V = 1.5d }];
            actor.SpillPct = 4d;
            actor.Grow     = new CosmeticSceneGrow
            {
                FromPct    = 10d,
                ToPct      = 90d,
                DurationMs = 2000,
                HoldMs     = 500,
                FromScale  = 0.2d,
                WobbleDeg  = 6d,
                Softness   = 0.4d,
                Sway       = new CosmeticSceneSway { Deg = 3d, Ms = 5000 }
            };

            actor.Retreat = new CosmeticSceneRetreat { AtMs = 3000, DurationMs = 800, EdgePct = 10d, FootPct = 20d, SoftPct = 5d, Remain = 0.2d };
        }));

        var silent = Only<SceneWash>(Wash());

        Assert.Multiple(() =>
        {
            Assert.That(said.periodMs, Is.EqualTo(6000));
            Assert.That(said.scale, Is.EqualTo(new[] { new SceneStop(0d, 1d), new SceneStop(1d, 1.5d) }));
            Assert.That(said.spillPct, Is.EqualTo(4d));
            Assert.That(said.grow, Is.EqualTo(new SceneGrow(10d, 90d, 2000, 500, null, 0.2d, null, 6d, 0.4d, new SceneSway(3d, 5000), null)));
            Assert.That(said.retreat, Is.EqualTo(new SceneRetreat(3000, 800, 10d, 20d, 5d, 0.2d)));

            Assert.That(silent.periodMs, Is.Null);
            Assert.That(silent.scale, Is.Null);
            Assert.That(silent.spillPct, Is.Null);
            Assert.That(silent.grow, Is.Null);
            Assert.That(silent.retreat, Is.Null);
        });
    }

    [Test]
    public void A_sprite_arrives_with_every_number_it_carries()
    {
        var said = Only<SceneSprite>(With(Sprite(), actor =>
        {
            actor.BobPct   = 2d;
            actor.Rotate   = [new CosmeticSceneStop { At = 0d, V = -10d }, new CosmeticSceneStop { At = 1d, V = 10d }];
            actor.BowPct   = 30d;
            actor.BankDeg  = 12d;
            actor.Scale    = [new CosmeticSceneStop { At = 0d, V = 1d }, new CosmeticSceneStop { At = 1d, V = 2d }];
            actor.SpillPct = 5d;
            actor.Retreat  = new CosmeticSceneRetreat { AtMs = 1000, DurationMs = 400 };
        }));

        var silent = Only<SceneSprite>(Sprite());

        Assert.Multiple(() =>
        {
            Assert.That(said.sizePct, Is.EqualTo(20d));
            Assert.That(said.durationMs, Is.EqualTo(4000));
            Assert.That(said.bobPct, Is.EqualTo(2d));
            Assert.That(said.rotate, Is.EqualTo(new[] { new SceneStop(0d, -10d), new SceneStop(1d, 10d) }));
            Assert.That(said.bowPct, Is.EqualTo(30d));
            Assert.That(said.bankDeg, Is.EqualTo(12d));
            Assert.That(said.scale, Is.EqualTo(new[] { new SceneStop(0d, 1d), new SceneStop(1d, 2d) }));
            Assert.That(said.spillPct, Is.EqualTo(5d));
            Assert.That(said.retreat, Is.EqualTo(new SceneRetreat(1000, 400, null, null, null, null)));

            Assert.That(silent.source, Is.Null);
            Assert.That(silent.atlas, Is.Null);
            Assert.That(silent.bobPct, Is.Null);
            Assert.That(silent.rotate, Is.Null);
            Assert.That(silent.bowPct, Is.Null);
            Assert.That(silent.bankDeg, Is.Null);
            Assert.That(silent.scale, Is.Null);
            Assert.That(silent.spillPct, Is.Null);
            Assert.That(silent.retreat, Is.Null);
        });
    }

    [Test]
    public void A_band_arrives_with_every_number_it_carries()
    {
        var said = Only<SceneBand>(With(Band(), actor =>
        {
            actor.CornerPct = 12d;
            actor.OutsetPct = 3d;
            actor.Retreat   = new CosmeticSceneRetreat { AtMs = 0, DurationMs = 240 };
        }));

        var silent = Only<SceneBand>(Band());

        Assert.Multiple(() =>
        {
            Assert.That(said.slice, Is.EqualTo(new SceneSlice(1, 2, 3, 4)));
            Assert.That(said.thicknessPct, Is.EqualTo(10d));
            Assert.That(said.cornerPct, Is.EqualTo(12d));
            Assert.That(said.outsetPct, Is.EqualTo(3d));
            Assert.That(said.retreat, Is.EqualTo(new SceneRetreat(0, 240, null, null, null, null)));

            Assert.That(silent.cornerPct, Is.Null);
            Assert.That(silent.outsetPct, Is.Null);
            Assert.That(silent.grow, Is.Null);
            Assert.That(silent.retreat, Is.Null);
        });
    }

    [Test]
    public void A_wrap_arrives_with_its_four_sides()
    {
        var said = Only<SceneWrap>(With(Wrap(), actor =>
        {
            actor.OutsetPct = 2d;
            actor.Retreat   = new CosmeticSceneRetreat { AtMs = 0, DurationMs = 240 };
        }));

        var silent = Only<SceneWrap>(Wrap());

        Assert.Multiple(() =>
        {
            Assert.That(said.slice, Is.EqualTo(new SceneSlice(8, 8, 8, 8)));
            Assert.That(said.widthPct, Is.EqualTo(new SceneWidths(1d, 2d, 3d, 4d)));
            Assert.That(said.outsetPct, Is.EqualTo(2d));
            Assert.That(said.retreat, Is.EqualTo(new SceneRetreat(0, 240, null, null, null, null)));

            Assert.That(silent.outsetPct, Is.Null);
            Assert.That(silent.grow, Is.Null);
            Assert.That(silent.retreat, Is.Null);
        });
    }

    [Test]
    public void A_scatter_arrives_with_every_number_it_carries()
    {
        var said = Only<SceneEmitter>(With(Emitter(), actor =>
        {
            actor.Seed           = 7;
            actor.BobPct         = 1d;
            actor.SizeVarPct     = 2d;
            actor.DurationVarMs  = 1500;
            actor.DriftPct       = 30d;
            actor.SwayPct        = 8d;
            actor.SwayPeriodMs   = 2400;
            actor.SwayVarMs      = 600;
            actor.SpinDeg        = 180d;
            actor.FlutterDeg     = 40d;
            actor.DepthSpreadPct = 60d;
        }));

        var silent = Only<SceneEmitter>(Emitter());

        Assert.Multiple(() =>
        {
            Assert.That(said.count, Is.EqualTo((ushort)12));
            Assert.That(said.seed, Is.EqualTo((ushort)7));
            Assert.That(said.sizePct, Is.EqualTo(4d));
            Assert.That(said.durationMs, Is.EqualTo(9000));
            Assert.That(said.bobPct, Is.EqualTo(1d));
            Assert.That(said.sizeVarPct, Is.EqualTo(2d));
            Assert.That(said.durationVarMs, Is.EqualTo(1500));
            Assert.That(said.driftPct, Is.EqualTo(30d));
            Assert.That(said.swayPct, Is.EqualTo(8d));
            Assert.That(said.swayPeriodMs, Is.EqualTo(2400));
            Assert.That(said.swayVarMs, Is.EqualTo(600));
            Assert.That(said.spinDeg, Is.EqualTo(180d));
            Assert.That(said.flutterDeg, Is.EqualTo(40d));
            Assert.That(said.depthSpreadPct, Is.EqualTo(60d));

            Assert.That(silent.seed, Is.Null);
            Assert.That(silent.bobPct, Is.Null);
            Assert.That(silent.sizeVarPct, Is.Null);
            Assert.That(silent.durationVarMs, Is.Null);
            Assert.That(silent.driftPct, Is.Null);
            Assert.That(silent.swayPct, Is.Null);
            Assert.That(silent.swayPeriodMs, Is.Null);
            Assert.That(silent.swayVarMs, Is.Null);
            Assert.That(silent.spinDeg, Is.Null);
            Assert.That(silent.flutterDeg, Is.Null);
            Assert.That(silent.depthSpreadPct, Is.Null);
        });
    }

    [Test]
    public void An_unvalidated_actor_of_no_known_sort_stops_the_mapping()
        => Assert.That(() => Wire(With(Wash(), actor => actor.Type = "cloud")),
            Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains("a scene actor this build has no case for"));

    [Test]
    public void An_unvalidated_word_a_scene_cannot_be_drawn_without_stops_the_mapping()
    {
        Assert.Multiple(() =>
        {
            foreach (var miss in NearMisses("content", "card", "choice"))
            {
                Assert.That(() => Wire(new ProfileScenePayload { Reach = miss, Actors = [Wash()] }),
                    Throws.TypeOf<ArgumentOutOfRangeException>(), $"reach '{miss}'");
            }

            foreach (var miss in NearMisses("top", "right", "bottom", "left"))
            {
                Assert.That(() => Wire(With(Band(), actor => actor.Edge = miss)), Throws.TypeOf<ArgumentOutOfRangeException>(), $"edge '{miss}'");
            }

            foreach (var miss in NearMisses(Anchors))
            {
                Assert.That(() => Wire(With(Sprite(), actor => actor.From = new CosmeticScenePoint { Anchor = miss })),
                    Throws.TypeOf<ArgumentOutOfRangeException>(), $"anchor '{miss}'");
            }

            foreach (var miss in NearMisses(["edge", .. Anchors]))
            {
                var grow = Grow();

                grow.From = miss;

                Assert.That(() => Wire(With(Band(), actor => actor.Grow = grow)), Throws.TypeOf<ArgumentOutOfRangeException>(), $"grow from '{miss}'");
            }
        });
    }

    [Test]
    public void An_unvalidated_optional_word_with_no_case_is_left_out()
    {
        Assert.Multiple(() =>
        {
            foreach (var miss in NearMisses("content", "card"))
            {
                Assert.That(Wire(new ProfileScenePayload { Reach = "choice", DefaultReach = miss, Actors = [Wash()] }).defaultReach,
                    Is.Null, $"defaultReach '{miss}'");
            }

            foreach (var miss in NearMisses("atlas", "file"))
            {
                Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Source = miss)).source, Is.Null, $"source '{miss}'");
            }

            foreach (var miss in NearMisses("deep", "over", "front"))
            {
                Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Depth = miss)).depth, Is.Null, $"depth '{miss}'");
            }

            foreach (var miss in NearMisses("cover", "contain", "stretch"))
            {
                Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Fit = miss)).fit, Is.Null, $"fit '{miss}'");
            }

            foreach (var miss in NearMisses("loop", "once", "pingPong"))
            {
                Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Repeat = miss)).repeat, Is.Null, $"repeat '{miss}'");
            }

            foreach (var miss in NearMisses("linear", "in", "out", "inOut"))
            {
                Assert.That(Only<SceneSprite>(With(Sprite(), actor => actor.Ease = miss)).ease, Is.Null, $"ease '{miss}'");
            }

            foreach (var miss in NearMisses("none", "mirror"))
            {
                Assert.That(Only<SceneSprite>(With(Sprite(), actor => actor.Turn = miss)).turn, Is.Null, $"turn '{miss}'");
            }

            foreach (var miss in NearMisses("auto", "x", "y"))
            {
                Assert.That(Only<SceneSprite>(With(Sprite(), actor => actor.BowAxis = miss)).bowAxis, Is.Null, $"bowAxis '{miss}'");
            }

            foreach (var miss in NearMisses("stretch", "repeat", "round", "space"))
            {
                Assert.That(Only<SceneBand>(With(Band(), actor => actor.Tile = miss)).tile, Is.Null, $"tile '{miss}'");
            }

            foreach (var miss in NearMisses("y", "x", "both"))
            {
                var grow = Grow();

                grow.LeadAxis = miss;

                Assert.That(Only<SceneBand>(With(Band(), actor => actor.Grow = grow)).grow!.leadAxis, Is.Null, $"leadAxis '{miss}'");
            }

            // A part of the card that hides nothing is not carried, and the parts that do still are.
            Assert.That(Only<SceneWash>(With(Wash(), actor => actor.Occlude = ["glass", "avatar"])).occlude,
                Is.EqualTo(new[] { SceneOccluder.Avatar }));
        });
    }
}
