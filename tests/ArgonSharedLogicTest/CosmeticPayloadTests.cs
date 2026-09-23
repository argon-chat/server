namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;

/// <summary>
/// What a stored document is allowed to say, and that both serializers say the same about it.
/// </summary>
/// <remarks>
/// The two codecs are the point of most of this fixture. A payload written through the console's
/// Newtonsoft and read through System.Text.Json has to mean one thing, and the way that stops being
/// true is quietly: one of them accepts a member the other refuses, or reads an enum the other does
/// not, and the difference surfaces as a cosmetic that renders wrong for the people who have it.
/// </remarks>
[TestFixture]
public class CosmeticPayloadTests
{
    private static ICosmeticJsonCodec[] Codecs => [CosmeticJson.SystemText, CosmeticJson.Newtonsoft];

    private static CosmeticPayloadSchema Font    => CosmeticPayloadSchema.For<FontOptionPayload>();
    private static CosmeticPayloadSchema Avatar  => CosmeticPayloadSchema.For<AvatarDecorationPayload>();
    private static CosmeticPayloadSchema Nick    => CosmeticPayloadSchema.For<NicknameStyleTuning>();

    [Test]
    public void An_empty_payload_is_not_a_payload([Values(null, "", "   ")] string? json)
        => Assert.That(Avatar.Validate(json).IsValid, Is.False);

    [Test]
    public void A_member_the_type_does_not_declare_is_an_error()
    {
        foreach (var codec in Codecs)
        {
            var outcome = Avatar.Validate("""{"insetPct":10,"beneath":false,"opacity":0.5}""", codec);

            Assert.That(outcome.IsValid, Is.False, $"{codec.Name} accepted an undeclared member");
        }
    }

    [Test]
    public void A_payload_both_codecs_read_the_same_way()
    {
        foreach (var codec in Codecs)
        {
            Assert.That(Avatar.Validate("""{"insetPct":10,"beneath":true}""", codec).IsValid, Is.True, codec.Name);
        }
    }

    [Test]
    public void A_family_name_is_letters_digits_spaces_and_hyphens()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Font.Validate("""{"cssFamily":"Noto Sans JP"}""").IsValid, Is.True);
            Assert.That(Font.Validate("""{"cssFamily":"Inter-Tight"}""").IsValid, Is.True);
            Assert.That(Font.Validate("""{"cssFamily":"Inter; } body {"}""").IsValid, Is.False);
            Assert.That(Font.Validate("""{"cssFamily":""}""").IsValid, Is.False);
        });
    }

    [Test]
    public void An_avatar_is_inset_by_whole_percent_within_reason()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Avatar.Validate("""{"insetPct":12,"beneath":false}""").IsValid, Is.True);
            Assert.That(Avatar.Validate("""{"insetPct":41,"beneath":false}""").IsValid, Is.False);
        });
    }

    [Test]
    public void A_name_takes_six_colours_at_the_outside()
    {
        var five = Gradient(1, 2, 3, 4, 5);
        var six  = Gradient(1, 2, 3, 4, 5, 6);

        Assert.Multiple(() =>
        {
            Assert.That(Nick.Validate("{\"colors\":" + five + "}").IsValid, Is.True);
            Assert.That(Nick.Validate("{\"colors\":" + six + "}").IsValid, Is.True);
            Assert.That(() => CosmeticGradient.Of(1, 2, 3, 4, 5, 6, 7), Throws.TypeOf<ArgumentOutOfRangeException>());
        });
    }

    /// <summary>
    /// A seventh colour is refused rather than dropped, by both codecs.
    /// </summary>
    /// <remarks>
    /// Truncating would publish a gradient the operator did not author and say nothing about it,
    /// which is the same reasoning that makes an unmapped member an error on the way in.
    /// </remarks>
    [Test]
    public void A_seventh_colour_is_refused_rather_than_dropped()
    {
        foreach (var codec in Codecs)
        {
            Assert.That(Nick.Validate("""{"colors":[1,2,3,4,5,6,7]}""", codec).IsValid, Is.False, codec.Name);
        }
    }

    /// <summary>
    /// The packing is the type's own business and stops at its edge: a JSON number is a double
    /// everywhere the client is, and two colours in one long do not fit in one exactly.
    /// </summary>
    [Test]
    public void A_gradient_crosses_the_wire_as_a_list_of_integers()
    {
        var first  = unchecked((int)0xFF112233);
        var second = 0x00445566;

        var written = CosmeticJson.SystemText.Write(CosmeticGradient.Of(first, second));

        Assert.That(written, Is.EqualTo($"[{first},{second}]"));
    }

    [Test]
    public void Both_codecs_read_the_same_gradient()
    {
        foreach (var codec in Codecs)
        {
            Assert.That(Nick.Validate("""{"colors":[-15729101,4478310],"shape":"linear"}""", codec).IsValid,
                Is.True, codec.Name);
        }
    }

    [Test]
    public void A_gradient_gives_back_the_colours_it_was_given()
    {
        var packed = CosmeticGradient.Of(unchecked((int)0xFF112233), 0x00445566, unchecked((int)0xFF778899));

        Assert.Multiple(() =>
        {
            Assert.That(packed.Count, Is.EqualTo(3));
            Assert.That(packed[0], Is.EqualTo(unchecked((int)0xFF112233)));
            Assert.That(packed[1], Is.EqualTo(0x00445566));
            Assert.That(packed[2], Is.EqualTo(unchecked((int)0xFF778899)));
            Assert.That(packed[3], Is.EqualTo(0));
        });
    }

    [Test]
    public void A_shape_is_one_of_the_three_both_codecs_know()
    {
        foreach (var codec in Codecs)
        {
            Assert.Multiple(() =>
            {
                Assert.That(Nick.Validate("""{"shape":"conic"}""", codec).IsValid, Is.True, codec.Name);
                Assert.That(Nick.Validate("""{"shape":"spiral"}""", codec).IsValid, Is.False, codec.Name);
            });
        }
    }

    [Test]
    public void A_name_is_set_in_a_weight_and_a_spacing_that_exist()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Nick.Validate("""{"weight":700,"letterSpacingCentiEm":8}""").IsValid, Is.True);
            Assert.That(Nick.Validate("""{"weight":1000}""").IsValid, Is.False);
            Assert.That(Nick.Validate("""{"letterSpacingCentiEm":80}""").IsValid, Is.False);
        });
    }

    /// <summary>
    /// One spelling for a slot, in the payload and in the map that holds its file.
    /// </summary>
    /// <remarks>
    /// The two drifting apart is invisible: a payload asking for a slot the map does not have draws
    /// nothing, which looks exactly like a row whose upload was never finished.
    /// </remarks>
    [Test]
    public void A_slot_is_spelled_the_same_in_a_payload_and_in_the_asset_map()
    {
        foreach (var slot in Enum.GetValues<CosmeticAssetSlot>())
        {
            var key = CosmeticAssetSlots.KeyOf(slot);

            // What the codec writes when a payload names the slot, which has to be the same word.
            Assert.That(CosmeticJson.SystemText.Write(new SlotHolder { Slot = slot }),
                Is.EqualTo($$"""{"slot":"{{key}}"}"""), key);
        }
    }

    private sealed class SlotHolder
    {
        public CosmeticAssetSlot Slot { get; set; }
    }

    /// <summary>Written by the codec itself, so the fixture cannot spell the packing differently.</summary>
    private static string Gradient(params int[] colors)
        => CosmeticJson.SystemText.Write(CosmeticGradient.Of(colors));
}
