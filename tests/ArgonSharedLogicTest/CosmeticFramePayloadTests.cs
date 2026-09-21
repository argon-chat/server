namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;

/// <summary>
/// A frame is the one cosmetic allowed outside the card it belongs to, so it is the one whose
/// geometry has to be held to a ceiling.
/// </summary>
/// <remarks>
/// <para>The parts used to be one flat type with a string discriminator and every field on every
/// part, refused by name against a hand-written list. They are three types now, so most of what
/// this fixture used to have to assert — that a ring handed a slice is refused — is no longer
/// expressible, and what is left is the arithmetic.</para>
///
/// <para>Both codecs are exercised, because a frame is the payload with a discriminator in it and
/// the two serializers reach it by different routes: an attribute on one side, a converter on the
/// other.</para>
/// </remarks>
[TestFixture]
public class CosmeticFramePayloadTests
{
    private static ICosmeticJsonCodec[] Codecs => [CosmeticJson.SystemText, CosmeticJson.Newtonsoft];

    private static CosmeticPayloadSchema Frame => CosmeticPayloadSchema.For<ProfileFramePayload>();

    private static CosmeticPayloadValidation Check(string parts, ICosmeticJsonCodec codec)
        => Frame.Validate("{\"parts\":" + parts + "}", codec);

    [Test]
    public void A_frame_with_no_parts_is_not_a_frame()
    {
        foreach (var codec in Codecs)
        {
            Assert.That(Check("[]", codec).IsValid, Is.False, codec.Name);
        }
    }

    [Test]
    public void A_part_without_a_type_is_refused()
    {
        foreach (var codec in Codecs)
        {
            Assert.That(Check("""[{"thickness":2}]""", codec).IsValid, Is.False, codec.Name);
        }
    }

    [Test]
    public void A_type_outside_the_three_is_refused()
    {
        foreach (var codec in Codecs)
        {
            Assert.That(Check("""[{"type":"scene"}]""", codec).IsValid, Is.False, codec.Name);
        }
    }

    [Test]
    public void A_ring_is_a_thickness_and_some_colours()
    {
        var ring = "[{\"type\":\"ring\",\"thickness\":2,\"colors\":" + Gradient(unchecked((int)0xFFFF0000)) + "}]";

        foreach (var codec in Codecs)
        {
            Assert.That(Check(ring, codec).IsValid, Is.True, $"{codec.Name}: {string.Join("; ", Check(ring, codec).Errors)}");
        }
    }

    [Test]
    public void A_ring_takes_four_colours_at_the_outside()
    {
        var five = "[{\"type\":\"ring\",\"thickness\":2,\"colors\":" + Gradient(1, 2, 3, 4, 5) + "}]";

        Assert.That(Check(five, CosmeticJson.SystemText).IsValid, Is.False);
    }

    [Test]
    public void A_surround_is_a_slice_and_a_width_and_a_file()
    {
        var band = """
            [{"type":"surround","slot":"primary","slice":[8,8,8,8],"width":[4,4,4,4],"repeat":"round"}]
            """;

        foreach (var codec in Codecs)
        {
            Assert.That(Check(band, codec).IsValid, Is.True, $"{codec.Name}: {string.Join("; ", Check(band, codec).Errors)}");
        }
    }

    [Test]
    public void A_band_drawn_nowhere_is_refused()
    {
        var nothing = """
            [{"type":"surround","slot":"primary","slice":[8,8,8,8],"width":[0,0,0,0]}]
            """;

        Assert.That(Check(nothing, CosmeticJson.SystemText).IsValid, Is.False);
    }

    [Test]
    public void A_repeat_outside_the_closed_set_is_refused()
    {
        var wrong = """
            [{"type":"surround","slot":"primary","slice":[8,8,8,8],"width":[4,4,4,4],"repeat":"tile"}]
            """;

        foreach (var codec in Codecs)
        {
            Assert.That(Check(wrong, codec).IsValid, Is.False, codec.Name);
        }
    }

    /// <summary>
    /// The hole the old rule left: only the axis the anchor named was measured, so a part pinned to
    /// the top could be made as wide as the limit allowed and then nudged sideways, and nothing
    /// looked at either number.
    /// </summary>
    [Test]
    public void A_part_pinned_to_the_top_cannot_escape_sideways()
    {
        var overhanging = """
            [{"type":"prop","slot":"primary","anchor":"top","w":512,"h":16,"dx":256}]
            """;

        var outcome = Check(overhanging, CosmeticJson.SystemText);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.IsValid, Is.False);
            Assert.That(outcome.Errors, Has.Some.Contains("reaches right"));
        });
    }

    [Test]
    public void A_part_pinned_to_the_top_may_still_be_as_wide_as_the_card()
    {
        var sitting = """
            [{"type":"prop","slot":"primary","anchor":"top","w":320,"h":64,"dy":8}]
            """;

        var outcome = Check(sitting, CosmeticJson.SystemText);

        Assert.That(outcome.IsValid, Is.True, string.Join("; ", outcome.Errors));
    }

    [Test]
    public void A_part_hanging_too_far_above_the_card_is_refused()
    {
        var hanging = """
            [{"type":"prop","slot":"primary","anchor":"top","w":64,"h":200}]
            """;

        var outcome = Check(hanging, CosmeticJson.SystemText);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.IsValid, Is.False);
            Assert.That(outcome.Errors, Has.Some.Contains("reaches above"));
        });
    }

    [Test]
    public void An_anchor_outside_the_nine_is_refused()
    {
        var wrong = """
            [{"type":"prop","slot":"primary","anchor":"middle","w":32,"h":32}]
            """;

        foreach (var codec in Codecs)
        {
            Assert.That(Check(wrong, codec).IsValid, Is.False, codec.Name);
        }
    }

    [Test]
    public void A_strip_that_does_not_divide_into_rows_is_refused()
    {
        var ragged = """
            [{"type":"prop","slot":"primary","anchor":"topRight","w":32,"h":32,
              "sprite":{"frames":7,"columns":4,"fps":12}}]
            """;

        Assert.That(Check(ragged, CosmeticJson.SystemText).IsValid, Is.False);
    }

    [Test]
    public void A_still_frame_outside_the_strip_is_refused()
    {
        var beyond = """
            [{"type":"prop","slot":"primary","anchor":"topRight","w":32,"h":32,
              "sprite":{"frames":8,"columns":4,"still":9}}]
            """;

        Assert.That(Check(beyond, CosmeticJson.SystemText).IsValid, Is.False);
    }

    [Test]
    public void Opacity_is_whole_percent_and_never_invisible()
    {
        var invisible = Ring("\"opacityPct\":1");
        var solid     = Ring("\"opacityPct\":80");

        Assert.Multiple(() =>
        {
            Assert.That(Check(invisible, CosmeticJson.SystemText).IsValid, Is.False);
            Assert.That(Check(solid, CosmeticJson.SystemText).IsValid, Is.True);
        });
    }

    [Test]
    public void A_frame_is_capped_before_it_becomes_a_scene()
    {
        var ring = "{\"type\":\"ring\",\"thickness\":1,\"colors\":" + Gradient(unchecked((int)0xFF00FF00)) + "}";
        var nine = string.Join(",", Enumerable.Repeat(ring, CosmeticFrameLimits.MaxParts + 1));

        Assert.That(Check($"[{nine}]", CosmeticJson.SystemText).IsValid, Is.False);
    }

    [Test]
    public void A_movement_is_named_and_bounded()
    {
        var wild = Ring("\"motion\":{\"kind\":\"sway\",\"periodMs\":10}");

        Assert.That(Check(wild, CosmeticJson.SystemText).IsValid, Is.False);
    }

    /// <summary>A one-ring frame, plus whatever member the case is about.</summary>
    private static string Ring(string andAlso)
        => "[{\"type\":\"ring\",\"thickness\":1,\"colors\":"
         + Gradient(unchecked((int)0xFF00FF00)) + "," + andAlso + "}]";

    /// <summary>Written by the codec itself, so the fixture cannot spell the packing differently.</summary>
    private static string Gradient(params int[] colors)
        => CosmeticJson.SystemText.Write(CosmeticGradient.Of(colors));
}
