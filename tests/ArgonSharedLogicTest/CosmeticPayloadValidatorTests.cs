namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;

/// <summary>
/// What a catalogue row's payload has to satisfy before the row can be published.
/// </summary>
/// <remarks>
/// <para>The payload is stored as JSON and validated against the kind's payload type, so the type's
/// <c>[Required]</c>, its ranges and its own <c>Validate</c> are the schema. That division is
/// deliberate and matches how feature options work: the rules belong to the model, not to whichever
/// admin method happens to be saving it.</para>
///
/// <para>The case worth pinning hardest is the unknown member. Newtonsoft's default is to drop a
/// field the type does not declare, which would mean a kind edited without its rows being migrated
/// publishes cosmetics that render as something other than what the operator filled in — silently,
/// and only on the surfaces that read the dropped field.</para>
/// </remarks>
[TestFixture]
public class CosmeticPayloadValidatorTests
{
    [Test]
    public void A_payload_matching_its_type_is_valid()
    {
        var result = CosmeticPayloadValidator.Validate(typeof(ProfileBackgroundPayload),
            """{"loop":true,"tintOpacity":0.4}""");

        Assert.That(result.IsValid, Is.True, string.Join("; ", result.Errors));
    }

    [Test]
    public void A_field_the_type_does_not_declare_is_refused()
    {
        var result = CosmeticPayloadValidator.Validate(typeof(ProfileBackgroundPayload),
            """{"loop":true,"tintOpacityy":0.4}""");

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_value_outside_its_range_is_refused()
    {
        var result = CosmeticPayloadValidator.Validate(typeof(ProfileBackgroundPayload),
            """{"tintOpacity":4}""");

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void A_required_field_left_out_is_refused()
    {
        var result = CosmeticPayloadValidator.Validate(typeof(BadgePayload), """{"tint":255}""");

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void Malformed_json_is_reported_rather_than_thrown()
    {
        var result = CosmeticPayloadValidator.Validate(typeof(BadgePayload), "{not json");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Errors, Is.Not.Empty);
        });
    }

    [Test]
    public void An_empty_payload_is_refused()
        => Assert.That(CosmeticPayloadValidator.Validate(typeof(BadgePayload), null).IsValid, Is.False);

    /// <summary>
    /// A rule that no annotation can express — a pair that has to agree — runs too.
    /// </summary>
    [Test]
    public void The_payloads_own_rules_run_after_the_annotations()
    {
        var oneStop = CosmeticPayloadValidator.Validate(typeof(NicknameStylePayload),
            """{"gradientStops":[255]}""");

        var twoStops = CosmeticPayloadValidator.Validate(typeof(NicknameStylePayload),
            """{"gradientStops":[255,128]}""");

        Assert.Multiple(() =>
        {
            Assert.That(oneStop.IsValid, Is.False);
            Assert.That(twoStops.IsValid, Is.True, string.Join("; ", twoStops.Errors));
        });
    }
}
