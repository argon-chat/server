namespace ArgonSharedLogicTest.Expressions;

using Argon.Features.Clustering;
using Argon.Features.Expressions;
using ArgonContracts;
using Microsoft.Extensions.Configuration;

[TestFixture]
public class ExpressionLimitsTests
{
    [TestCase(ExpressionKind.Sticker, ExpressionFormat.Static, 512 * 1024)]
    [TestCase(ExpressionKind.Sticker, ExpressionFormat.Lottie, 64 * 1024)]
    [TestCase(ExpressionKind.Sticker, ExpressionFormat.Video, 256 * 1024)]
    [TestCase(ExpressionKind.Emoji, ExpressionFormat.Static, 128 * 1024)]
    [TestCase(ExpressionKind.Emoji, ExpressionFormat.Lottie, 64 * 1024)]
    [TestCase(ExpressionKind.Emoji, ExpressionFormat.Video, 256 * 1024)]
    public void Byte_caps_are_telegrams(ExpressionKind kind, ExpressionFormat format, int bytes)
        => Assert.That(ExpressionLimits.MaxBytes(kind, format), Is.EqualTo(bytes));

    [TestCase("ab", true)]
    [TestCase("party_parrot_2", true)]
    [TestCase("a", false)]
    [TestCase("Party", false)]
    [TestCase("has-dash", false)]
    [TestCase("abcdefghijklmnopqrstuvwxyz012345", true)]
    [TestCase("abcdefghijklmnopqrstuvwxyz0123456", false)]
    public void Emoji_names_are_2_to_32_of_a_to_z_digits_and_underscore(string name, bool valid)
        => Assert.That(ExpressionLimits.IsValidName(ExpressionKind.Emoji, name), Is.EqualTo(valid));

    [TestCase("Hi", true)]
    [TestCase("Good morning!", true)]
    [TestCase("x", false)]
    [TestCase(" padded ", false)]
    [TestCase("   ", false)]
    [TestCase("line\nbreak", false)]
    public void Sticker_names_are_2_to_30_characters_of_text(string name, bool valid)
        => Assert.That(ExpressionLimits.IsValidName(ExpressionKind.Sticker, name), Is.EqualTo(valid));

    [Test]
    public void Keywords_and_associated_emoji_have_their_counts()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpressionLimits.AreValidKeywords([]), Is.True);
            Assert.That(ExpressionLimits.AreValidKeywords(Enumerable.Repeat("ab", 20).ToArray()), Is.True);
            Assert.That(ExpressionLimits.AreValidKeywords(Enumerable.Repeat("a", 21).ToArray()), Is.False);
            Assert.That(ExpressionLimits.AreValidKeywords([new string('k', 65)]), Is.False);
            Assert.That(ExpressionLimits.AreValidAssociatedEmoji([]), Is.True, "associated emoji are optional");
            Assert.That(ExpressionLimits.AreValidAssociatedEmoji(["👋"]), Is.True);
            Assert.That(ExpressionLimits.AreValidAssociatedEmoji(Enumerable.Repeat("👋", 20).ToArray()), Is.True);
            Assert.That(ExpressionLimits.AreValidAssociatedEmoji(Enumerable.Repeat("👋", 21).ToArray()), Is.False);
            Assert.That(ExpressionLimits.AreValidAssociatedEmoji([" "]), Is.False);
            Assert.That(ExpressionLimits.IsValidPackSlug("my_pack_1"), Is.True);
            Assert.That(ExpressionLimits.IsValidPackSlug(""), Is.False);
            Assert.That(ExpressionLimits.IsValidPackTitle(new string('t', 64)), Is.True);
            Assert.That(ExpressionLimits.IsValidPackTitle(new string('t', 65)), Is.False);
        });
    }

    [Test]
    public void Slots_follow_the_boost_level_and_stop_at_the_top()
    {
        var options = new ExpressionsOptions();

        Assert.Multiple(() =>
        {
            Assert.That(options.SlotsFor(ExpressionKind.Emoji, 0), Is.EqualTo(60));
            Assert.That(options.SlotsFor(ExpressionKind.Emoji, 3), Is.EqualTo(300));
            Assert.That(options.SlotsFor(ExpressionKind.Emoji, 9), Is.EqualTo(300));
            Assert.That(options.SlotsFor(ExpressionKind.Sticker, 1), Is.EqualTo(18));
            Assert.That(options.SlotsFor(ExpressionKind.Sticker, -1), Is.EqualTo(6));
            Assert.That(options.ItemsPerPack(ExpressionKind.Emoji), Is.EqualTo(200));
            Assert.That(options.ItemsPerPack(ExpressionKind.Sticker), Is.EqualTo(120));
        });
    }

    [Test]
    public void Configured_slots_replace_the_defaults_rather_than_append_to_them()
    {
        var configuration = new ConfigurationBuilder()
           .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Expressions:EmojiSlotsByBoostLevel:0"] = "10",
                ["Expressions:EmojiSlotsByBoostLevel:1"] = "20"
            })
           .Build();

        var options = new ExpressionsOptions();
        configuration.GetSection(ExpressionsOptions.SectionName).Bind(options);

        Assert.That(options.EffectiveEmojiSlots, Is.EqualTo(new[] { 10, 20 }));
        Assert.That(options.EffectiveStickerSlots, Is.EqualTo(new[] { 6, 18, 36, 72 }));
    }

    [Test]
    public void Defaults_validate_and_a_decreasing_table_does_not()
    {
        var clean = new Report();
        new ExpressionsOptions().Validate(clean);

        var broken = new Report();
        new ExpressionsOptions { StickerSlotsByBoostLevel = [10, 5], PacksPerSpace = 0 }.Validate(broken);

        Assert.That(clean.Errors, Is.Empty);
        Assert.That(broken.Errors, Has.Count.EqualTo(2));
    }

    private sealed class Report : IFeatureConfigurationReport
    {
        public List<string> Errors { get; } = [];

        public string Section       => ExpressionsOptions.SectionName;
        public bool   SectionExists => true;

        public TOther Read<TOther>(string section) where TOther : class => throw new NotSupportedException();

        public void Require(bool condition, string setting, string message)
        {
            if (!condition)
                Errors.Add($"{setting}: {message}");
        }

        public void Invalid(string message) => Errors.Add(message);

        public void Prefer(bool condition, string setting, string message) { }

        public void Required(string? value, string setting) => Require(!string.IsNullOrWhiteSpace(value), setting, "required");

        public void RequireUri(string? value, string setting, params string[] schemes) { }

        public void RequireFile(string? path, string setting) { }

        public void RequireRange(int value, int min, int max, string setting)
            => Require(value >= min && value <= max, setting, $"must be in [{min}, {max}]");

        public void RequireRange(TimeSpan value, TimeSpan min, TimeSpan max, string setting)
            => Require(value >= min && value <= max, setting, $"must be in [{min}, {max}]");
    }
}
