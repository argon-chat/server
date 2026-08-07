namespace ArgonSharedLogicTest;

using Argon.Core.Features.WebHooks;
using System.Text.Json;

/// <summary>
/// Xsolla sends <c>custom_parameters</c> as free-form JSON and is inconsistent about whether numeric
/// fields arrive as numbers or as strings. Everything the payment webhook does — which user, which
/// plan, how many boosts — is read through these two helpers, so a mis-read here silently drops a
/// paid purchase.
/// </summary>
[TestFixture]
public class XsollaWebhookParsingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    // ── GetString ───────────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetString_ReadsAPresentKey()
        => Assert.That(
            XsollaCustomParametersHelper.GetString(Json("""{"type":"gift"}"""), "type"),
            Is.EqualTo("gift"));

    [Test]
    public void GetString_MissingKeyIsNull()
        => Assert.That(XsollaCustomParametersHelper.GetString(Json("""{"type":"gift"}"""), "plan"), Is.Null);

    [Test]
    public void GetString_NullElementIsNull()
        => Assert.That(XsollaCustomParametersHelper.GetString(null, "type"), Is.Null);

    [Test]
    public void GetString_NonObjectElementIsNull()
        => Assert.That(XsollaCustomParametersHelper.GetString(Json("""[1,2,3]"""), "type"), Is.Null);

    /// <summary>
    /// GetString does <em>not</em> tolerate a numeric value: JsonElement.GetString throws on a
    /// number and the helper does not guard it. Recorded as behaviour, not as an endorsement —
    /// FlexibleStringConverter exists precisely because Xsolla is inconsistent about number-versus-
    /// string, so a payload that sends a normally-string field as a number will fault the webhook.
    /// </summary>
    [Test]
    public void GetString_OnANumericValue_Throws()
        => Assert.Throws<InvalidOperationException>(
            () => XsollaCustomParametersHelper.GetString(Json("""{"boost_count":3}"""), "boost_count"));

    // ── GetInt ──────────────────────────────────────────────────────────────────────────────────

    [Test]
    public void GetInt_ReadsANumber()
        => Assert.That(XsollaCustomParametersHelper.GetInt(Json("""{"boost_count":3}"""), "boost_count"), Is.EqualTo(3));

    [Test]
    public void GetInt_ReadsANumberSentAsAString()
        // Xsolla's sandbox and production do not agree on this; both have to work.
        => Assert.That(XsollaCustomParametersHelper.GetInt(Json("""{"boost_count":"3"}"""), "boost_count"), Is.EqualTo(3));

    [Test]
    public void GetInt_UnparsableStringIsNull()
        => Assert.That(XsollaCustomParametersHelper.GetInt(Json("""{"boost_count":"three"}"""), "boost_count"), Is.Null);

    [Test]
    public void GetInt_MissingKeyIsNull()
        => Assert.That(XsollaCustomParametersHelper.GetInt(Json("""{"other":1}"""), "boost_count"), Is.Null);

    [Test]
    public void GetInt_NullElementIsNull()
        => Assert.That(XsollaCustomParametersHelper.GetInt(null, "boost_count"), Is.Null);

    [Test]
    public void GetInt_BooleanIsNull()
        => Assert.That(XsollaCustomParametersHelper.GetInt(Json("""{"boost_count":true}"""), "boost_count"), Is.Null);

    // ── FlexibleStringConverter ─────────────────────────────────────────────────────────────────

    private sealed record Wrapper(string? Value);

    private static readonly JsonSerializerOptions FlexibleOptions = new()
    {
        Converters          = { new FlexibleStringConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Test]
    public void FlexibleStringConverter_ReadsAString()
        => Assert.That(JsonSerializer.Deserialize<Wrapper>("""{"value":"abc"}""", FlexibleOptions)!.Value, Is.EqualTo("abc"));

    [Test]
    public void FlexibleStringConverter_ReadsAnIntegerAsAString()
        => Assert.That(JsonSerializer.Deserialize<Wrapper>("""{"value":12345}""", FlexibleOptions)!.Value, Is.EqualTo("12345"));

    [Test]
    public void FlexibleStringConverter_ReadsNullAsNull()
        => Assert.That(JsonSerializer.Deserialize<Wrapper>("""{"value":null}""", FlexibleOptions)!.Value, Is.Null);

    [Test]
    public void FlexibleStringConverter_RoundTripsAValue()
    {
        var json = JsonSerializer.Serialize(new Wrapper("42"), FlexibleOptions);
        Assert.That(JsonSerializer.Deserialize<Wrapper>(json, FlexibleOptions)!.Value, Is.EqualTo("42"));
    }

    [Test]
    public void FlexibleStringConverter_WritesNullAsNull()
        => Assert.That(JsonSerializer.Serialize(new Wrapper(null), FlexibleOptions), Does.Contain("null"));
}
