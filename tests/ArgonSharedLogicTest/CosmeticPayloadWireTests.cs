namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using ArgonContracts;

/// <summary>
/// What a catalogue row's stored document goes over Ion as.
/// </summary>
/// <remarks>
/// <para>Ion carries no JSON. A payload is stored as a document, checked against its kind's schema by
/// this build, and sent as the typed case of <see cref="ICosmeticPayload"/> — so what a client
/// receives is exactly what the schema allowed, with every optional number already the value the
/// client would have drawn it with.</para>
///
/// <para>A document that does not fit its kind is not sent at all: the row is left out of the
/// catalogue rather than sent half-understood.</para>
/// </remarks>
[TestFixture]
public class CosmeticPayloadWireTests
{
    private static readonly CosmeticKindRegistry Registry =
        CosmeticKindRegistry.FromAssembly(typeof(ICosmeticKind).Assembly);

    private static ICosmeticPayload? Wire(string kindKey, string json) => Registry.Find(kindKey)!.PayloadToWire(json);

    private const string Orbit =
        """
        {"minSizePx":34,"satellites":[
          {"type":"satellite","slot":"Primary","radiusPct":62,"widthPct":30,"heightPct":24,"periodMs":6000,
           "tiltDeg":70,"turn":"spin","nearScale":1.2,"sprite":{"frames":8,"columns":4}}
        ]}
        """;

    /// <summary>One of every sort of actor, in the order a scene draws them.</summary>
    private const string Scene =
        """
        {"reach":"choice","defaultReach":"content","sheet":{"w":256,"h":128},"actors":[
          {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},"fit":"cover","spillPct":4,
           "retreat":{"atMs":3000,"durationMs":800,"remain":0.2}},
          {"type":"sprite","source":"atlas","atlas":{"x":64,"y":0,"w":32,"h":32,"frames":4,"columns":4,"fps":8},
           "sizePct":20,"durationMs":4000,"from":{"anchor":"topRight","dxPct":-2.5},"to":{"anchor":"bottomLeft"},
           "ease":"inOut","turn":"mirror","bowAxis":"y","depth":"front","occlude":["avatar"],
           "rotate":[{"at":0,"v":-10},{"at":1,"v":10}]},
          {"type":"band","source":"file","slot":"Secondary","edge":"bottom","slice":[8,8,8,8],"thicknessPct":12,
           "tile":"round","grow":{"fromPct":0,"toPct":100,"durationMs":2000,"from":"bottomLeft","leadAxis":"y",
           "sway":{"deg":3,"ms":5000}}},
          {"type":"wrap","source":"file","slot":"Tertiary","replay":true,"slice":[24,24,24,24],"widthPct":[6,0,6,0]},
          {"type":"emitter","source":"atlas","atlas":{"x":0,"y":64,"w":16,"h":16},"edge":"top","count":24,
           "seed":7,"sizePct":4,"sizeVarPct":2,"durationMs":9000,"repeat":"pingPong","flutterDeg":40,
           "shadow":{"dxPct":0.5,"dyPct":1,"blurPct":1,"alpha":0.3}}
        ]}
        """;

    /// <summary>A kind with rows and no case would have every row of it quietly missing from the catalogue.</summary>
    [Test]
    public void Every_kind_with_rows_has_a_case_on_the_wire()
    {
        var samples = new Dictionary<string, string>
        {
            ["avatar.decoration"]  = """{"insetPct":10,"beneath":false}""",
            ["profile.frame"]      = """{"parts":[{"type":"ring","thickness":2,"colors":[-1]}]}""",
            ["option.font"]        = """{"cssFamily":"Inter"}""",
            ["option.text-effect"] = "{}",
            ["profile.background"] = "{}",
            ["profile.badge"]      = """{"tooltipKey":"badge_staff"}""",
            ["avatar.orbit"]       = Orbit,
            ["profile.scene"]      = Scene
        };

        var withRows = Registry.All.Where(kind => !kind.IsBare).Select(kind => kind.Key).ToList();

        Assert.That(samples.Keys, Is.EquivalentTo(withRows), "a kind with rows has no sample here — add one, and a case to the contract");

        foreach (var (kind, json) in samples)
        {
            Assert.That(Wire(kind, json), Is.Not.Null, kind);
        }
    }

    [Test]
    public void An_avatar_decoration_is_its_inset_and_its_side()
        => Assert.That(Wire("avatar.decoration", """{"insetPct":12,"beneath":true}"""),
            Is.EqualTo(new PayloadAvatarDecoration(12, true)));

    [Test]
    public void A_face_is_its_family()
        => Assert.That(Wire("option.font", """{"cssFamily":"Noto Sans JP"}"""), Is.EqualTo(new PayloadFont("Noto Sans JP")));

    [Test]
    public void A_treatment_carries_nothing()
        => Assert.That(Wire("option.text-effect", "{}"), Is.InstanceOf<PayloadTextEffect>());

    [Test]
    public void A_document_that_does_not_fit_its_kind_is_not_sent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Wire("avatar.decoration", """{"insetPct":90,"beneath":false}"""), Is.Null);
            Assert.That(Wire("option.font", """{"cssFamily":"Inter; } body {"}"""), Is.Null);
            Assert.That(Wire("profile.frame", """{"parts":[]}"""), Is.Null);
            Assert.That(Wire("profile.frame", "not a document"), Is.Null);
        });
    }

    [Test]
    public void A_frame_arrives_as_typed_parts_in_the_order_they_are_drawn()
    {
        var frame = Wire("profile.frame", """
            {"parts":[
              {"type":"surround","slot":"primary","slice":[8,8,8,8],"width":[4,0,4,0],"repeat":"round"},
              {"type":"prop","slot":"secondary","anchor":"topRight","w":40,"h":24,"dx":-6,
               "sprite":{"frames":8,"columns":4},"motion":{"kind":"sway","amount":2}},
              {"type":"ring","thickness":2,"colors":[-65536,-16776961],"glowPct":40,"opacityPct":80,"over":false}
            ]}
            """);

        Assert.That(frame, Is.InstanceOf<PayloadProfileFrame>());

        var parts = ((PayloadProfileFrame)frame!).parts;

        Assert.That(parts.Select(part => part.GetType()),
            Is.EqualTo(new[] { typeof(FrameSurround), typeof(FrameProp), typeof(FrameRing) }));

        var band = (FrameSurround)parts[0];
        var prop = (FrameProp)parts[1];
        var ring = (FrameRing)parts[2];

        Assert.Multiple(() =>
        {
            Assert.That(band.slot, Is.EqualTo(AssetSlot.Primary));
            Assert.That(band.width, Is.EqualTo(new FrameSides(4, 0, 4, 0)));
            Assert.That(band.repeat, Is.EqualTo(FrameRepeat.Round));
            Assert.That(band.outset, Is.Null);

            Assert.That(prop.slot, Is.EqualTo(AssetSlot.Secondary));
            Assert.That(prop.anchor, Is.EqualTo(FrameAnchor.TopRight));
            Assert.That(prop.dx, Is.EqualTo(-6));
            Assert.That(prop.sprite, Is.EqualTo(new FrameSprite(8, 4, 12, 0)));
            Assert.That(prop.motion, Is.EqualTo(new FrameMotion("sway", 2, 4000, 0)));

            Assert.That(ring.colors, Is.EqualTo(new[] { -65536, -16776961 }));
            Assert.That(ring.glowPct, Is.EqualTo((ushort)40));
            Assert.That(ring.opacityPct, Is.EqualTo((ushort)80));
            Assert.That(ring.over, Is.False);
        });
    }

    /// <summary>Nothing arrives as "absent" when the answer is known: the client's own defaults, filled in.</summary>
    [Test]
    public void What_a_part_leaves_out_arrives_as_what_the_client_would_draw()
    {
        var ring = (FrameRing)((PayloadProfileFrame)Wire("profile.frame",
            """{"parts":[{"type":"ring","thickness":3,"colors":[-1]}]}""")!).parts[0];

        Assert.Multiple(() =>
        {
            Assert.That(ring.over, Is.True);
            Assert.That(ring.opacityPct, Is.EqualTo((ushort)100));
            Assert.That(ring.angle, Is.EqualTo((ushort)135));
            Assert.That(ring.glowPct, Is.EqualTo((ushort)0));
            Assert.That(ring.inset, Is.Null);
            Assert.That(ring.motion, Is.Null);
        });
    }

    [Test]
    public void A_background_is_its_loop_and_its_tint()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Wire("profile.background", """{"loop":false,"tintOpacity":0.5}"""),
                Is.EqualTo(new PayloadProfileBackground(false, 0.5d)));

            // The payload type's own defaults: a clip loops, under a third of the card's colour.
            Assert.That(Wire("profile.background", "{}"), Is.EqualTo(new PayloadProfileBackground(true, 0.35d)));
            Assert.That(Wire("profile.background", """{"tintOpacity":4}"""), Is.Null);
        });
    }

    [Test]
    public void A_badge_is_its_tooltip_and_its_tint()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Wire("profile.badge", """{"tooltipKey":"badge_staff","tint":-65536}"""),
                Is.EqualTo(new PayloadBadge("badge_staff", -65536)));
            Assert.That(Wire("profile.badge", """{"tooltipKey":"badge_staff"}"""), Is.EqualTo(new PayloadBadge("badge_staff", null)));
            Assert.That(Wire("profile.badge", """{"tint":255}"""), Is.Null);
        });
    }

    [Test]
    public void An_orbit_arrives_as_typed_figures_saying_only_what_the_row_said()
    {
        var orbit = (PayloadAvatarOrbit)Wire("avatar.orbit", Orbit)!;
        var cat   = (OrbitSatellite)orbit.satellites.Single();

        Assert.Multiple(() =>
        {
            Assert.That(orbit.minSizePx, Is.EqualTo((ushort)34));
            Assert.That(cat.slot, Is.EqualTo(AssetSlot.Primary));
            Assert.That(cat.radiusPct, Is.EqualTo(62d));
            Assert.That(cat.periodMs, Is.EqualTo(6000));
            Assert.That(cat.tiltDeg, Is.EqualTo(70d));
            Assert.That(cat.turn, Is.EqualTo(OrbitTurn.Spin));
            Assert.That(cat.nearScale, Is.EqualTo(1.2d));
            Assert.That(cat.sprite, Is.EqualTo(new OrbitSprite(8, 4, null, null)));

            // Absent stays absent: the renderer holds the defaults.
            Assert.That(cat.farScale, Is.Null);
            Assert.That(cat.occlude, Is.Null);
            Assert.That(cat.stillDeg, Is.Null);
        });
    }

    [Test]
    public void An_orbit_that_does_not_fit_its_kind_is_not_sent()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Wire("avatar.orbit", """{"satellites":[]}"""), Is.Null);
            Assert.That(Wire("avatar.orbit",
                """{"satellites":[{"type":"satellite","slot":"7","radiusPct":62,"widthPct":30,"heightPct":24,"periodMs":6000}]}"""),
                Is.Null);
            Assert.That(Wire("avatar.orbit",
                """{"satellites":[{"type":"satellite","slot":"Primary","radiusPct":62,"widthPct":30,"heightPct":24,"periodMs":6000,"turn":"flip"}]}"""),
                Is.Null);
        });
    }

    [Test]
    public void A_scene_arrives_as_one_case_per_sort_of_actor_in_drawing_order()
    {
        var scene  = (PayloadProfileScene)Wire("profile.scene", Scene)!;
        var actors = scene.actors;

        Assert.That(actors.Select(actor => actor.GetType()), Is.EqualTo(new[]
        {
            typeof(SceneWash), typeof(SceneSprite), typeof(SceneBand), typeof(SceneWrap), typeof(SceneEmitter)
        }));

        var wash    = (SceneWash)actors[0];
        var sprite  = (SceneSprite)actors[1];
        var band    = (SceneBand)actors[2];
        var wrap    = (SceneWrap)actors[3];
        var emitter = (SceneEmitter)actors[4];

        Assert.Multiple(() =>
        {
            Assert.That(scene.reach, Is.EqualTo(SceneReachRule.Choice));
            Assert.That(scene.defaultReach, Is.EqualTo(SceneReach.Content));
            Assert.That(scene.sheet, Is.EqualTo(new SceneSheet(256, 128)));

            Assert.That(wash.source, Is.EqualTo(SceneSource.Atlas));
            Assert.That(wash.atlas, Is.EqualTo(new SceneAtlas(0, 0, 64, 64, null, null, null, null)));
            Assert.That(wash.fit, Is.EqualTo(SceneFit.Cover));
            Assert.That(wash.spillPct, Is.EqualTo(4d));
            Assert.That(wash.retreat, Is.EqualTo(new SceneRetreat(3000, 800, null, null, null, 0.2d)));

            Assert.That(sprite.atlas, Is.EqualTo(new SceneAtlas(64, 0, 32, 32, 4, 4, 8, null)));
            Assert.That(sprite.from, Is.EqualTo(new ScenePoint(SceneAnchor.TopRight, -2.5d, null)));
            Assert.That(sprite.to, Is.EqualTo(new ScenePoint(SceneAnchor.BottomLeft, null, null)));
            Assert.That(sprite.ease, Is.EqualTo(SceneEase.InOut));
            Assert.That(sprite.turn, Is.EqualTo(SceneTurn.Mirror));
            Assert.That(sprite.bowAxis, Is.EqualTo(SceneBowAxis.Y));
            Assert.That(sprite.depth, Is.EqualTo(SceneDepth.Front));
            Assert.That(sprite.occlude, Is.EqualTo(new[] { SceneOccluder.Avatar }));
            Assert.That(sprite.rotate, Is.EqualTo(new[] { new SceneStop(0d, -10d), new SceneStop(1d, 10d) }));

            Assert.That(band.slot, Is.EqualTo(AssetSlot.Secondary));
            Assert.That(band.edge, Is.EqualTo(SceneEdge.Bottom));
            Assert.That(band.slice, Is.EqualTo(new SceneSlice(8, 8, 8, 8)));
            Assert.That(band.tile, Is.EqualTo(SceneTile.Round));
            Assert.That(band.cornerPct, Is.Null);
            Assert.That(band.grow, Is.EqualTo(new SceneGrow(0d, 100d, 2000, null, null, null, SceneLeadAxis.Y, null, null,
                new SceneSway(3d, 5000), SceneGrowOrigin.BottomLeft)));

            Assert.That(wrap.slot, Is.EqualTo(AssetSlot.Tertiary));
            Assert.That(wrap.replay, Is.True);
            Assert.That(wrap.widthPct, Is.EqualTo(new SceneWidths(6d, 0d, 6d, 0d)));

            Assert.That(emitter.edge, Is.EqualTo(SceneEdge.Top));
            Assert.That(emitter.count, Is.EqualTo((ushort)24));
            Assert.That(emitter.seed, Is.EqualTo((ushort)7));
            Assert.That(emitter.sizeVarPct, Is.EqualTo(2d));
            Assert.That(emitter.repeat, Is.EqualTo(SceneRepeat.PingPong));
            Assert.That(emitter.flutterDeg, Is.EqualTo(40d));
            Assert.That(emitter.shadow, Is.EqualTo(new SceneShadow(0.5d, 1d, 1d, 0.3d)));
        });
    }

    /// <summary>A field another sort of actor owns keeps the whole row out, rather than being dropped.</summary>
    [Test]
    public void A_scene_that_does_not_fit_its_kind_is_not_sent()
        => Assert.That(Wire("profile.scene", """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary","edge":"top","slice":[8,8,8,8],"thicknessPct":10,"count":3}
            ]}
            """), Is.Null);
}
