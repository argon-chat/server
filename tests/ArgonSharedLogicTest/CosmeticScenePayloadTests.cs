namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;

/// <summary>
/// What a scene's payload has to satisfy, and the refusals that are the whole point of it.
/// </summary>
/// <remarks>
/// A scene carries more numbers than anything else in the catalogue and every one of them arrives
/// from a form somebody typed into. The cases below are the ones where a wrong value is not a wrong
/// picture but a broken card: an envelope that does not cover its cycle, a field belonging to
/// another sort of actor, and a spread wider than the thing it is spreading.
/// </remarks>
[TestFixture]
public class CosmeticScenePayloadTests
{
    private const string OneSprite =
        """
        {"reach":"card","sheet":{"w":256,"h":128},"actors":[
          {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
           "sizePct":20,"durationMs":4000,
           "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"}}
        ]}
        """;

    private static CosmeticPayloadValidation Check(string json)
        => CosmeticPayloadValidator.Validate(typeof(ProfileScenePayload), json);

    [Test]
    public void A_scene_of_one_sprite_is_valid()
    {
        var result = Check(OneSprite);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_scene_with_no_actors_is_refused()
    {
        Assert.That(Check("""{"reach":"card","sheet":{"w":256,"h":128},"actors":[]}""").IsValid, Is.False);
    }

    [Test]
    public void Reach_outside_the_three_words_is_refused()
    {
        Assert.That(Check("""{"reach":"everything","actors":[]}""").IsValid, Is.False);
    }

    [Test]
    public void A_default_reach_without_a_choice_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","defaultReach":"content","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_choice_without_a_default_reach_is_refused()
    {
        var result = Check(
            """
            {"reach":"choice","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_field_belonging_to_another_sort_of_actor_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "durationMs":4000}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("durationMs"));
    }

    [Test]
    public void An_envelope_that_does_not_start_at_zero_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "opacity":[{"at":0.2,"v":0},{"at":1,"v":1}]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void An_envelope_whose_stops_go_backwards_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "opacity":[{"at":0,"v":0},{"at":0.7,"v":1},{"at":0.3,"v":0.5},{"at":1,"v":0}]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void An_envelope_of_one_stop_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "opacity":[{"at":0,"v":1}]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_non_monotonic_envelope_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "opacity":[{"at":0,"v":0},{"at":0.15,"v":1},{"at":0.8,"v":1},{"at":1,"v":0}]}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// The one scene here that carries no sheet, because nothing in it reads one — a sheet would be
    /// a second refusal and this test would stop saying what it is named for.
    /// </summary>
    [Test]
    public void An_emitter_reading_a_whole_file_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"emitter","source":"file","slot":"Secondary","edge":"top",
               "count":10,"sizePct":4,"durationMs":6000}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("cannot be spread out"));
    }

    [Test]
    public void An_emitter_spread_wider_than_its_middle_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"sizeVarPct":6,"durationMs":6000}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_band_with_a_file_of_its_own_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,"cornerPct":12}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// The refusal that keeps a band a nine-slice: <c>border-image-source</c> is a whole picture,
    /// and a rectangle of the sheet is not one.
    /// </summary>
    [Test]
    public void A_band_reading_the_sheet_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"band","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("a file of its own"));
    }

    [Test]
    public void A_band_that_grows_inwards_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":100,"toPct":0,"durationMs":2000}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_wrap_with_a_file_and_four_widths_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wrap","source":"file","slot":"Secondary",
               "slice":[24,24,24,24],"widthPct":[8,8,8,8],"tile":"round"}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// A wrap is a nine-slice, so it takes the band's refusal along with the band's cut.
    /// </summary>
    [Test]
    public void A_wrap_reading_the_sheet_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wrap","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "slice":[24,24,24,24],"widthPct":[8,8,8,8]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("a file of its own"));
    }

    [Test]
    public void A_wrap_with_no_widths_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wrap","source":"file","slot":"Secondary","slice":[24,24,24,24]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("widthPct is missing"));
    }

    [Test]
    public void A_wrap_whose_widths_are_not_four_numbers_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wrap","source":"file","slot":"Secondary",
               "slice":[24,24,24,24],"widthPct":[8,8]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("and has 2"));
    }

    /// <summary>
    /// Four sides of nothing passes every range there is and draws no pixels, which on a card is
    /// indistinguishable from a file that failed to load.
    /// </summary>
    [Test]
    public void A_wrap_of_four_zero_widths_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wrap","source":"file","slot":"Secondary",
               "slice":[24,24,24,24],"widthPct":[0,0,0,0]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("drawn nowhere"));
    }

    /// <summary>
    /// The pair that keeps the two nine-slices apart: a wrap has no one side to lie on, and a band
    /// has no four to be thick on.
    /// </summary>
    [Test]
    public void A_wrap_given_an_edge_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wrap","source":"file","slot":"Secondary","edge":"bottom",
               "slice":[24,24,24,24],"widthPct":[8,8,8,8]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("edge"));
    }

    [Test]
    public void A_band_given_the_widths_of_a_wrap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,"widthPct":[8,8,8,8]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("widthPct"));
    }

    /// <summary>
    /// A reveal out of a point rather than in from a side — the difference between a blind coming
    /// down and a vine taking the card.
    /// </summary>
    [Test]
    public void A_reveal_rooted_at_an_anchor_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wrap","source":"file","slot":"Secondary",
               "slice":[24,24,24,24],"widthPct":[6,10,10,6],
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"from":"bottomLeft"}}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_reveal_rooted_at_neither_an_edge_nor_an_anchor_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"from":"root"}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("grow.from"));
    }

    [Test]
    public void Occluding_something_that_is_not_part_of_a_card_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "occlude":["board"]}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_field_the_type_does_not_declare_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},"fitt":"cover"}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_scene_reading_the_sheet_without_one_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("sheet is missing"));
    }

    [Test]
    public void A_sheet_nothing_reads_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"file","slot":"Secondary"}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("sheet is only read"));
    }

    [Test]
    public void A_rectangle_running_off_the_sheet_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":200,"y":0,"w":64,"h":64}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("8px past the right edge"));
    }

    /// <summary>
    /// The pair that proves the fit is measured over the whole strip: the same rectangle is refused
    /// as four frames and accepted as one picture.
    /// </summary>
    [Test]
    public void A_strip_whose_last_frame_runs_off_the_sheet_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":72,"h":64,"frames":4,"columns":4},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("32px past the right edge"));
    }

    [Test]
    public void The_same_rectangle_as_one_picture_fits()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":72,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"}}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// A scene that ends: every actor but a band may say its own cycle plays once.
    /// </summary>
    /// <remarks>
    /// <c>repeat</c> was a sprite's alone until 2026-09-18, which made a whole sort of cosmetic
    /// inexpressible — the one that plays through when somebody opens a card and then simply leaves
    /// it decorated. A wash and a scatter that could only loop went round forever underneath the
    /// parts that had finished.
    /// </remarks>
    [Test]
    public void A_wash_and_a_scatter_may_play_once()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},"repeat":"once",
               "opacity":[{"at":0,"v":0},{"at":1,"v":1}]},
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,"repeat":"once"}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_band_still_takes_no_repeat_of_its_own()
    {
        // Its cycle is inside `grow`, next to the hold it shares one with. Two places to say the
        // same thing is two places for them to disagree.
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,"repeat":"once"}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("repeat"));
    }

    [Test]
    public void A_repeat_outside_the_three_words_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},"repeat":"forever"}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Tuning_outside_the_two_words_is_refused()
    {
        var result = CosmeticPayloadValidator.Validate(typeof(ProfileSceneTuning), """{"reach":"both"}""");

        Assert.That(result.IsValid, Is.False);
    }

    // Volume, physics and growth — the fields added 2026-09-18.
    //
    // Every one of them is optional, and the proof of that is every test above this line: not one
    // of them names a field below, and all of them still pass. That is what "an existing row keeps
    // parsing" means, and it is worth more than a test that asserts it in one payload.

    /// <summary>
    /// A shadow belongs to no one sort of actor, so the thing worth checking is that it is refused
    /// by none of them.
    /// </summary>
    [Test]
    public void A_shadow_on_every_sort_of_actor_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "shadow":{"dxPct":2,"dyPct":4,"blurPct":3,"alpha":0.35}},
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},
               "shadow":{"dxPct":-50,"dyPct":50,"blurPct":50,"alpha":0}},
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "shadow":{"dxPct":0,"dyPct":-50,"blurPct":0,"alpha":1}},
              {"type":"wrap","source":"file","slot":"Tertiary",
               "slice":[24,24,24,24],"widthPct":[8,8,8,8],
               "shadow":{"dxPct":-4,"dyPct":-4,"blurPct":6,"alpha":0.2}},
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,
               "shadow":{"dxPct":1,"dyPct":1,"blurPct":2,"alpha":0.4}}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_shadow_thrown_further_than_half_the_card_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "shadow":{"dxPct":51,"dyPct":4,"blurPct":3,"alpha":0.35}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("shadow.dxPct"));
    }

    [Test]
    public void A_shadow_blurred_past_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "shadow":{"dxPct":2,"dyPct":4,"blurPct":51,"alpha":0.35}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("shadow.blurPct"));
    }

    [Test]
    public void A_shadow_more_opaque_than_opaque_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "shadow":{"dxPct":2,"dyPct":4,"blurPct":3,"alpha":1.5}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("shadow.alpha"));
    }

    /// <summary>
    /// Four numbers or nothing: a blur with no alpha is a shadow at whatever opacity the renderer
    /// picked, which is the renderer deciding where the sun is.
    /// </summary>
    [Test]
    public void A_shadow_missing_a_member_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "shadow":{"dxPct":2,"dyPct":4,"blurPct":3}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("shadow.alpha is missing"));
    }

    [Test]
    public void A_fluttering_scatter_with_depth_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,
               "flutterDeg":72,"bobPct":3,
               "swayPct":4,"swayPeriodMs":2400,"swayVarMs":900,
               "depthSpreadPct":60}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_flutter_past_edge_on_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,"flutterDeg":91}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("flutterDeg"));
    }

    [Test]
    public void A_scatter_bobbing_deeper_than_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,"bobPct":26}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("bobPct"));
    }

    [Test]
    public void A_depth_spread_past_a_whole_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,"depthSpreadPct":101}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("depthSpreadPct"));
    }

    /// <summary>
    /// The same trap as a size spread wider than its middle, one period in: the copies at the far
    /// end of the spread come out with no swing at all.
    /// </summary>
    [Test]
    public void A_sway_spread_wider_than_its_period_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,
               "swayPct":4,"swayPeriodMs":2400,"swayVarMs":3000}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("swayVarMs is wider than swayPeriodMs"));
    }

    [Test]
    public void A_bowed_and_banked_sprite_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},
               "bowPct":-140,"bowAxis":"y","bankDeg":-22,"bobPct":5}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_bow_past_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},"bowPct":201}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("bowPct"));
    }

    [Test]
    public void A_bank_past_half_a_turn_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},"bankDeg":-181}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("bankDeg"));
    }

    [Test]
    public void A_sprite_bobbing_deeper_than_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},"bobPct":26}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("bobPct"));
    }

    [Test]
    public void A_bow_axis_outside_the_three_words_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},
               "bowPct":40,"bowAxis":"sideways"}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("bowAxis"));
    }

    /// <summary>
    /// <c>bobPct</c> is the one field a sprite and a scatter share and mean different things by, so
    /// it belongs to neither's own list — and what could go wrong is that it is put in both, and
    /// each of them refuses its own field.
    /// </summary>
    [Test]
    public void A_bob_belongs_to_the_two_travelling_sorts_and_to_nothing_else()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},"bobPct":4}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("bobPct"));
    }

    /// <summary>
    /// The pair that proves each new name is registered in both halves of the refusal.
    /// </summary>
    /// <remarks>
    /// <b>An accepting test cannot see a half-registered field.</b> A name listed as foreign but
    /// missing from the name-to-property switch reads as never filled, so it is silently allowed
    /// everywhere and the actor it belongs to still parses. Only the wrong sort of actor carrying
    /// it tells the two halves apart.
    /// </remarks>
    [Test]
    public void The_scatter_only_fields_are_refused_on_a_sprite()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},
               "flutterDeg":30,"swayVarMs":400,"depthSpreadPct":50}
            ]}
            """);

        var errors = string.Join("; ", result.Errors);

        Assert.That(result.IsValid, Is.False);
        Assert.That(errors, Does.Contain("flutterDeg"));
        Assert.That(errors, Does.Contain("swayVarMs"));
        Assert.That(errors, Does.Contain("depthSpreadPct"));
    }

    [Test]
    public void The_sprite_only_fields_are_refused_on_a_scatter()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,
               "bowPct":40,"bowAxis":"x","bankDeg":12}
            ]}
            """);

        var errors = string.Join("; ", result.Errors);

        Assert.That(result.IsValid, Is.False);
        Assert.That(errors, Does.Contain("bowPct"));
        Assert.That(errors, Does.Contain("bowAxis"));
        Assert.That(errors, Does.Contain("bankDeg"));
    }

    [Test]
    public void A_growth_that_extends_wobbles_and_breathes_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"from":"bottom",
                       "fromScale":0.08,"leadAxis":"both","wobbleDeg":4,"softness":0.7,
                       "sway":{"deg":1.5,"ms":5200}}}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_growth_starting_at_no_size_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"fromScale":0}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("fromScale"));
    }

    [Test]
    public void A_growth_starting_larger_than_it_finishes_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"fromScale":1.5}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("fromScale"));
    }

    [Test]
    public void A_lead_axis_outside_the_three_words_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"leadAxis":"z"}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("leadAxis"));
    }

    [Test]
    public void A_wobble_past_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"wobbleDeg":46}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("wobbleDeg"));
    }

    [Test]
    public void A_reveal_softer_than_a_whole_edge_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"softness":1.5}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("softness"));
    }

    [Test]
    public void A_breathing_sway_leaning_past_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"sway":{"deg":46,"ms":5200}}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("sway.deg"));
    }

    [Test]
    public void A_breathing_sway_faster_than_the_shortest_period_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"sway":{"deg":2,"ms":100}}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("sway.ms"));
    }

    [Test]
    public void A_breathing_sway_missing_a_member_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":0,"toPct":100,"durationMs":3000,"sway":{"deg":2}}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("sway.ms is missing"));
    }

    // Spill, replay and retreat — the fields added 2026-09-18 for a picture that hangs past the card
    // and then gets out of the way of the text.
    //
    // The same proof of optionality as above: nothing above this line names a field below.

    [Test]
    public void A_spilling_wash_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},"spillPct":12}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_spilling_sprite_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},"spillPct":5}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// The spill box is the card plus the hang, and only <c>cover</c> fills it: a contained picture
    /// would hang past the card as empty box. An absent <c>fit</c> is <c>cover</c>, and passes.
    /// </summary>
    [Test]
    public void A_spilling_wash_that_does_not_cover_the_card_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "spillPct":12,"fit":"contain"}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("spillPct needs fit 'cover'"));
    }

    [Test]
    public void A_spill_on_a_band_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,"spillPct":4}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("has no 'spillPct'"));
    }

    [Test]
    public void A_spill_on_a_wrap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wrap","source":"file","slot":"Secondary",
               "slice":[24,24,24,24],"widthPct":[8,8,8,8],"spillPct":4}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("has no 'spillPct'"));
    }

    [Test]
    public void A_spill_on_a_scatter_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,"spillPct":4}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("has no 'spillPct'"));
    }

    [Test]
    public void A_spill_past_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},"spillPct":26}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("spillPct is 26"));
    }

    [Test]
    public void A_file_that_replays_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"wash","source":"file","slot":"Secondary","replay":true}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// Only a file has a clock of its own to restart. A rectangle of the sheet is moved by this
    /// code's animations, which start afresh with the card whatever anybody says.
    /// </summary>
    [Test]
    public void A_rectangle_of_the_sheet_that_replays_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},"replay":true}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("no clock of its own"));
    }

    /// <summary>The console's toggle writes <c>false</c>, so <c>false</c> has to pass everywhere.</summary>
    [Test]
    public void A_replay_switched_off_is_accepted_on_a_rectangle_of_the_sheet()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"sprite","source":"atlas","atlas":{"x":0,"y":0,"w":64,"h":64},
               "sizePct":20,"durationMs":4000,
               "from":{"anchor":"topRight"},"to":{"anchor":"bottomLeft"},"replay":false}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_retreating_wash_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "retreat":{"atMs":9200,"durationMs":2800,"edgePct":9,"footPct":7,"softPct":6,"remain":0}}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    /// <summary>
    /// The strips default to nothing and the softness to a little, so when and how long are the
    /// whole of a retreat that keeps nothing.
    /// </summary>
    [Test]
    public void A_retreat_of_its_two_times_alone_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "retreat":{"atMs":9200,"durationMs":2800}}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_retreat_with_no_start_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "retreat":{"durationMs":2800}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("retreat.atMs is missing"));
    }

    [Test]
    public void A_retreat_keeping_more_than_half_the_card_a_side_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "retreat":{"atMs":9200,"durationMs":2800,"edgePct":51}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("retreat.edgePct"));
    }

    [Test]
    public void A_retreat_softer_than_its_cap_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "retreat":{"atMs":9200,"durationMs":2800,"softPct":26}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("retreat.softPct"));
    }

    /// <summary>
    /// A whole passes the range and changes nothing on the card — the same trap as a wrap of four
    /// zero widths, refused for the same reason.
    /// </summary>
    [Test]
    public void A_retreat_that_leaves_everything_behind_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"wash","source":"atlas","atlas":{"x":0,"y":0,"w":8,"h":8},
               "retreat":{"atMs":9200,"durationMs":2800,"remain":1}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("not a retreat"));
    }

    [Test]
    public void A_scatter_given_a_retreat_is_refused()
    {
        var result = Check(
            """
            {"reach":"card","sheet":{"w":256,"h":128},"actors":[
              {"type":"emitter","source":"atlas","atlas":{"x":0,"y":0,"w":16,"h":16},
               "edge":"top","count":10,"sizePct":4,"durationMs":6000,
               "retreat":{"atMs":9200,"durationMs":2800}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("has no 'retreat'"));
    }

    /// <summary>
    /// Equal ends install no reveal, and what is left of the grow — extension, wobble, duration and
    /// breathing — is exactly what a tree that is drawn whole and only has to stand up wants.
    /// </summary>
    [Test]
    public void A_growth_with_nothing_to_uncover_is_accepted()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":100,"toPct":100,"durationMs":7600,"sway":{"deg":1.1,"ms":5200}}}
            ]}
            """);

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_growth_that_runs_inwards_is_still_refused()
    {
        var result = Check(
            """
            {"reach":"card","actors":[
              {"type":"band","source":"file","slot":"Secondary",
               "edge":"bottom","slice":[8,8,8,8],"thicknessPct":10,
               "grow":{"fromPct":100,"toPct":50,"durationMs":3000}}
            ]}
            """);

        Assert.That(result.IsValid, Is.False);
        Assert.That(string.Join("; ", result.Errors), Does.Contain("growth runs outwards"));
    }
}
