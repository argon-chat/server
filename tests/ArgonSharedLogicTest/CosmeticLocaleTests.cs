namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;

/// <summary>
/// What counts as a language a cosmetic may be named in.
/// </summary>
/// <remarks>
/// Shape only, never a list. The client's five codes are its own business and change with its
/// releases; refusing anything else here would mean a sixth language could not be translated until
/// the server shipped too, which is the release-shaped delay this whole feature exists to remove.
/// </remarks>
[TestFixture]
public class CosmeticLocaleTests
{
    [TestCase("en")]
    [TestCase("ru")]
    [TestCase("jp")]
    [TestCase("am")]
    [TestCase("ru_pt")]
    [TestCase("en_tengwar")]
    [TestCase("en_abcdefgh")]
    public void The_codes_the_app_uses_are_accepted(string raw)
    {
        Assert.That(CosmeticLocale.TryNormalize(raw, out var locale, out _), Is.True);
        Assert.That(locale, Is.EqualTo(raw));
    }

    [Test]
    public void Case_and_dashes_are_normalized()
    {
        Assert.That(CosmeticLocale.TryNormalize("  RU-PT ", out var locale, out _), Is.True);
        Assert.That(locale, Is.EqualTo("ru_pt"));
    }

    /// <summary>
    /// The case where only the case is wrong: an underscore is already there, so nothing but
    /// lowercasing has to happen.
    /// </summary>
    [Test]
    public void Already_underscored_uppercase_input_is_lowercased()
    {
        Assert.That(CosmeticLocale.TryNormalize("RU_PT", out var locale, out _), Is.True);
        Assert.That(locale, Is.EqualTo("ru_pt"));
    }

    /// <summary>
    /// The constant the publication gate holds a row to, and the client's own fallback. Asserted
    /// here because changing it silently would let a nameless row publish.
    /// </summary>
    [Test]
    public void Fallback_is_english_and_is_itself_a_locale()
    {
        Assert.That(CosmeticLocale.Fallback, Is.EqualTo("en"));
        Assert.That(CosmeticLocale.TryNormalize(CosmeticLocale.Fallback, out var locale, out _), Is.True);
        Assert.That(locale, Is.EqualTo(CosmeticLocale.Fallback));
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("e")]
    [TestCase("english")]
    [TestCase("ru_")]
    [TestCase("_en")]
    [TestCase("en_abcdefghi")]
    [TestCase("ru pt")]
    [TestCase("../etc")]
    public void Anything_that_is_not_a_locale_is_refused(string raw)
    {
        Assert.That(CosmeticLocale.TryNormalize(raw, out _, out var error), Is.False);
        Assert.That(error, Is.Not.Null);
    }
}
