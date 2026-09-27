namespace ArgonSharedLogicTest.Expressions;

using Argon.Features.Expressions;

[TestFixture]
public class StatusIconTests
{
    [TestCase(null, StatusIconKind.None)]
    [TestCase("", StatusIconKind.None)]
    [TestCase("🔥", StatusIconKind.Unicode)]
    [TestCase("👩‍👩‍👧‍👦", StatusIconKind.Unicode)]
    [TestCase("icon-7", StatusIconKind.Unicode)]
    [TestCase("0123456789abcdef", StatusIconKind.Unicode)]
    [TestCase("0123456789abcdefg", StatusIconKind.Invalid)]
    [TestCase("a:b", StatusIconKind.Invalid)]
    [TestCase("ce:", StatusIconKind.Invalid)]
    [TestCase("ce:not-a-guid", StatusIconKind.Invalid)]
    [TestCase("ce:00000000-0000-0000-0000-000000000000", StatusIconKind.Invalid)]
    public void An_icon_id_is_nothing_a_unicode_emoji_or_refused(string? iconId, StatusIconKind kind)
        => Assert.That(StatusIcon.Parse(iconId).Kind, Is.EqualTo(kind));

    [Test]
    public void A_ce_prefix_names_the_custom_emoji_item()
    {
        var itemId = Guid.NewGuid();

        Assert.Multiple(() =>
        {
            Assert.That(StatusIcon.Parse($"ce:{itemId}"), Is.EqualTo((StatusIconKind.CustomEmoji, itemId)));
            Assert.That(StatusIcon.Parse($"ce:{itemId.ToString().ToUpperInvariant()}").ItemId, Is.EqualTo(itemId));
            Assert.That(StatusIcon.CustomEmoji(itemId), Is.EqualTo($"ce:{itemId}"));
        });
    }
}
