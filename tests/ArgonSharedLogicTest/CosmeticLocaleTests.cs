namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;

/// <summary>
/// Which language codes a cosmetic may be named in.
/// </summary>
/// <remarks>
/// <para>Shape and not membership, deliberately. A language the client gains next release should be
/// translatable today, and a row nobody reads costs nothing — whereas a list here would have to be
/// edited, deployed and remembered every time the app learns a word.</para>
///
/// <para>The app also spells two of its languages the way a standard would not, <c>am</c> for
/// Armenian and <c>jp</c> for Japanese, so the shape is the app's and not BCP-47's.</para>
/// </remarks>
[TestFixture]
public class CosmeticLocaleTests
{
    [TestCase("en")]
    [TestCase("ru")]
    [TestCase("jp")]
    [TestCase("am")]
    [TestCase("ru_pt")]
    public void The_languages_the_app_speaks_are_locales(string raw)
        => Assert.That(CosmeticLocale.TryNormalize(raw, out _, out _), Is.True);

    [TestCase("EN", "en")]
    [TestCase("  ru  ", "ru")]
    [TestCase("ru-pt", "ru_pt")]
    public void A_locale_is_trimmed_lowercased_and_underscored(string raw, string expected)
    {
        Assert.That(CosmeticLocale.TryNormalize(raw, out var locale, out _), Is.True);
        Assert.That(locale, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("e")]
    [TestCase("eng")]
    [TestCase("en_")]
    [TestCase("en_portugal_south")]
    [TestCase("../../etc")]
    public void Anything_that_is_not_a_language_code_is_refused(string? raw)
    {
        Assert.That(CosmeticLocale.TryNormalize(raw, out _, out var error), Is.False);
        Assert.That(error, Is.Not.Null);
    }

    /// <summary>
    /// The language a published row must be named in, which is also the client's fallback. Changing
    /// one without the other leaves somebody reading a slug.
    /// </summary>
    [Test]
    public void The_fallback_is_a_locale_like_any_other()
        => Assert.That(CosmeticLocale.TryNormalize(CosmeticLocale.Fallback, out var locale, out _)
                    && locale == CosmeticLocale.Fallback, Is.True);
}
