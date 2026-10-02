namespace ArgonSharedLogicTest;

using AccountContracts;
using Argon.Core.Entities.Data;
using Argon.Features.Apps;

[TestFixture]
public class AppTextRulesTests
{
    [TestCase("plain", "plain")]
    [TestCase("  padded  ", "padded")]
    [TestCase("two\r\nlines", "two lines")]
    [TestCase("tab\tand   spaces", "tab and spaces")]
    [TestCase("bell\u0007inside", "bell inside")]
    [TestCase("\n\n", "")]
    public void A_one_line_value_is_flattened(string raw, string expected)
        => Assert.That(AppTextRules.Flatten(raw), Is.EqualTo(expected));

    [TestCase("  two\r\nlines  ", "two\nlines")]
    [TestCase("bell\u0007gone", "bellgone")]
    public void A_multi_line_value_keeps_its_line_breaks(string raw, string expected)
        => Assert.That(AppTextRules.Clean(raw), Is.EqualTo(expected));

    [Test]
    public void Keys_belong_to_the_kinds_of_application_that_have_them()
        => Assert.Multiple(() =>
        {
            Assert.That(AppTextKeys.Find("motd", DevAppType.Bot), Is.SameAs(AppTextKeys.Motd));
            Assert.That(AppTextKeys.Find("motd", DevAppType.Application), Is.Null);
            Assert.That(AppTextKeys.Find("description", DevAppType.Bot), Is.Null, "a reserved key is open");
            Assert.That(AppTextKeys.Find("MOTD", DevAppType.Bot), Is.Null);
        });

    [Test]
    public void An_emoji_sequence_counts_as_one_character()
    {
        var family = string.Concat(Enumerable.Repeat("👨‍👩‍👧", AppTextKeys.Motd.MaxLength));

        Assert.Multiple(() =>
        {
            Assert.That(Normalize(("en", family)).Error, Is.EqualTo(AppTextError.NONE));
            Assert.That(Normalize(("en", family + "!")).Error, Is.EqualTo(AppTextError.TOO_LONG));
        });
    }

    [Test]
    public void Nothing_at_all_needs_no_english()
        => Assert.That(Normalize(("ru", "  ")).Error, Is.EqualTo(AppTextError.NONE));

    [Test]
    public void Too_many_locales_are_refused_before_any_is_read()
    {
        var values = Enumerable.Range(0, AppTextRules.MaxLocales + 1).Select(i => ("bad locale", $"{i}")).ToArray();

        Assert.That(Normalize(values).Error, Is.EqualTo(AppTextError.TOO_MANY_LOCALES));
    }

    [Test]
    public void A_megabyte_is_refused_by_its_raw_length()
        => Assert.That(Normalize(("en", new string(' ', 1 << 20))), Is.EqualTo((AppTextError.TOO_LONG, "en")));

    private static (AppTextError Error, string? Locale) Normalize(params (string Locale, string Value)[] values)
    {
        var (error, locale, _) = AppTextRules.Normalize(AppTextKeys.Motd, values.Select(e => new LocaleValue(e.Locale, e.Value)).ToList());
        return (error, locale);
    }
}
