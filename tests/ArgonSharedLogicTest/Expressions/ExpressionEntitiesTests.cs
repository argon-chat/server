namespace ArgonSharedLogicTest.Expressions;

using Argon.Features.Expressions;
using ArgonContracts;
using ion.runtime;

/// <summary>What the message path keeps of a sticker or custom emoji a client sent, and what it rewrites.</summary>
[TestFixture]
public class ExpressionEntitiesTests
{
    private static readonly Guid Space = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static readonly byte[] OutlineBytes = [0xC0, 0x01, 0x42];

    private static ExpressionItem Item(ExpressionKind kind, string name, Guid? spaceId = null, bool textColor = false)
        => new(Guid.NewGuid(), Guid.NewGuid(), spaceId ?? Space, kind, ExpressionFormat.Lottie, name, Guid.NewGuid(),
            kind == ExpressionKind.Sticker ? Guid.NewGuid() : null, 512, 512, 1000, new IonArray<string>(["🙂"]), IonArray<string>.Empty,
            kind == ExpressionKind.Sticker ? new IonBytes(OutlineBytes) : null, textColor, 0, "https://cdn/x", "https://cdn/t", Guid.NewGuid());

    // What a client might claim: everything but the item id is a guess, and wrong on purpose.
    private static MessageEntitySticker StickerFor(ExpressionItem item, Guid? claimedSpace = null)
        => new(EntityType.Sticker, 3, 9, 1, item.itemId, Guid.NewGuid(), claimedSpace ?? item.spaceId, ExpressionFormat.Static,
            Guid.NewGuid(), null, 1, 1, null, "https://evil/x", "https://evil/t");

    private static MessageEntityCustomEmoji EmojiFor(ExpressionItem item, int offset, int length, Guid? claimedSpace = null)
        => new(EntityType.CustomEmoji, offset, length, 1, item.itemId, claimedSpace ?? item.spaceId, ExpressionFormat.Video,
            Guid.NewGuid(), "spoofed", !item.textColor, "https://evil/x");

    private static Dictionary<Guid, ExpressionItem> Live(params ExpressionItem[] items) => items.ToDictionary(i => i.itemId);

    [Test]
    public void A_lone_sticker_on_empty_text_is_rewritten_from_its_item()
    {
        var item   = Item(ExpressionKind.Sticker, "wave");
        var result = ExpressionEntities.Resolve("", [StickerFor(item)], Live(item), Space, 100);

        Assert.That(result, Has.Count.EqualTo(1));
        var sticker = (MessageEntitySticker)result[0];

        Assert.Multiple(() =>
        {
            Assert.That(sticker.packId, Is.EqualTo(item.packId));
            Assert.That(sticker.spaceId, Is.EqualTo(Space));
            Assert.That(sticker.format, Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That(sticker.fileId, Is.EqualTo(item.fileId));
            Assert.That(sticker.thumbFileId, Is.EqualTo(item.thumbFileId));
            Assert.That((sticker.width, sticker.height), Is.EqualTo((512, 512)));
            Assert.That(sticker.outline?.ToArray(), Is.EqualTo(OutlineBytes));
            Assert.That((sticker.offset, sticker.length), Is.EqualTo((0, 0)));
            Assert.That(sticker.downloadUrl, Is.Null, "URLs are filled on read, never kept from a client");
            Assert.That(sticker.thumbUrl, Is.Null);
        });
    }

    [Test]
    public void A_sticker_with_text_or_beside_another_entity_is_dropped()
    {
        var item  = Item(ExpressionKind.Sticker, "wave");
        var bold  = new MessageEntityBold(EntityType.Bold, 0, 2, 1);
        var live  = Live(item);

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionEntities.Resolve("hi", [StickerFor(item)], live, Space, 100), Is.Empty);
            Assert.That(ExpressionEntities.Resolve("", [StickerFor(item), bold], live, Space, 100), Is.EqualTo(new IMessageEntity[] { bold }));
        });
    }

    [Test]
    public void A_sticker_that_is_unknown_foreign_or_an_emoji_is_dropped()
    {
        var foreign = Item(ExpressionKind.Sticker, "wave", Other);
        var emoji   = Item(ExpressionKind.Emoji, "party");
        var gone    = Item(ExpressionKind.Sticker, "gone");

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionEntities.Resolve("", [StickerFor(foreign)], Live(foreign), Space, 100), Is.Empty);
            Assert.That(ExpressionEntities.Resolve("", [StickerFor(emoji)], Live(emoji), Space, 100), Is.Empty);
            Assert.That(ExpressionEntities.Resolve("", [StickerFor(gone)], Live(), Space, 100), Is.Empty);
        });
    }

    [Test]
    public void A_custom_emoji_over_its_name_is_kept_and_rewritten()
    {
        var item   = Item(ExpressionKind.Emoji, "party", textColor: true);
        var result = ExpressionEntities.Resolve("so :party: now", [EmojiFor(item, 3, 7)], Live(item), Space, 100);

        Assert.That(result, Has.Count.EqualTo(1));
        var emoji = (MessageEntityCustomEmoji)result[0];

        Assert.Multiple(() =>
        {
            Assert.That(emoji.name, Is.EqualTo("party"));
            Assert.That(emoji.fileId, Is.EqualTo(item.fileId));
            Assert.That(emoji.format, Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That(emoji.textColor, Is.True);
            Assert.That((emoji.offset, emoji.length), Is.EqualTo((3, 7)));
            Assert.That(emoji.downloadUrl, Is.Null);
        });
    }

    [TestCase(2, 7)]
    [TestCase(3, 6)]
    [TestCase(3, 8)]
    [TestCase(10, 7)]
    [TestCase(-1, 7)]
    public void A_custom_emoji_that_does_not_cover_exactly_its_name_is_dropped(int offset, int length)
    {
        var item = Item(ExpressionKind.Emoji, "party");
        var text = "so :party: now";

        Assert.That(ExpressionEntities.Resolve(text, [EmojiFor(item, offset, length)], Live(item), Space, 100), Is.Empty);
    }

    [Test]
    public void A_custom_emoji_of_another_name_another_space_or_a_sticker_is_dropped()
    {
        var renamed = Item(ExpressionKind.Emoji, "other");
        var foreign = Item(ExpressionKind.Emoji, "party", Other);
        var sticker = Item(ExpressionKind.Sticker, "party");

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionEntities.Resolve(":party:", [EmojiFor(renamed, 0, 7)], Live(renamed), Space, 100), Is.Empty);
            Assert.That(ExpressionEntities.Resolve(":party:", [EmojiFor(foreign, 0, 7)], Live(foreign), Space, 100), Is.Empty);
            Assert.That(ExpressionEntities.Resolve(":party:", [EmojiFor(sticker, 0, 7)], Live(sticker), Space, 100), Is.Empty);
        });
    }

    [Test]
    public void Custom_emoji_past_the_cap_are_dropped_and_the_text_stays()
    {
        var item     = Item(ExpressionKind.Emoji, "ok");
        var text     = string.Concat(Enumerable.Repeat(":ok:", 5));
        var entities = Enumerable.Range(0, 5).Select(i => (IMessageEntity)EmojiFor(item, i * 4, 4)).ToList();

        var result = ExpressionEntities.Resolve(text, entities, Live(item), Space, 3);

        Assert.That(result.Cast<MessageEntityCustomEmoji>().Select(e => e.offset), Is.EqualTo(new[] { 0, 4, 8 }));
    }

    [Test]
    public void Two_emoji_over_the_same_name_count_once()
    {
        var item = Item(ExpressionKind.Emoji, "ok");

        Assert.That(ExpressionEntities.Resolve(":ok:", [EmojiFor(item, 0, 4), EmojiFor(item, 0, 4)], Live(item), Space, 100), Has.Count.EqualTo(1));
    }

    [Test]
    public void Without_a_space_each_item_is_held_to_the_space_its_entity_names()
    {
        var item = Item(ExpressionKind.Emoji, "ok", Other);

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionEntities.Resolve(":ok:", [EmojiFor(item, 0, 4)], Live(item), null, 100), Has.Count.EqualTo(1));
            Assert.That(ExpressionEntities.Resolve(":ok:", [EmojiFor(item, 0, 4, claimedSpace: Space)], Live(item), null, 100), Is.Empty);
        });
    }

    [Test]
    public void Other_entities_pass_through_untouched()
    {
        var bold    = new MessageEntityBold(EntityType.Bold, 0, 2, 1);
        var mention = new MessageEntityMention(EntityType.Mention, 3, 4, 1, Guid.NewGuid());

        Assert.That(ExpressionEntities.Resolve("hi you", [bold, mention], Live(), Space, 100), Is.EqualTo(new IMessageEntity[] { bold, mention }));
    }

    [Test]
    public void Claimed_items_are_grouped_by_the_space_each_entity_names()
    {
        var a = Item(ExpressionKind.Emoji, "a");
        var b = Item(ExpressionKind.Sticker, "b", Other);

        var claimed = ExpressionEntities.ClaimedItems([EmojiFor(a, 0, 3), EmojiFor(a, 4, 3), StickerFor(b)]);

        Assert.Multiple(() =>
        {
            Assert.That(claimed[Space], Is.EqualTo(new[] { a.itemId }));
            Assert.That(claimed[Other], Is.EqualTo(new[] { b.itemId }));
        });
    }
}
