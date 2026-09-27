namespace ArgonSharedLogicTest;

using System.Buffers;
using Argon.Entities;
using Argon.Features.Clustering;
using Argon.Features.NatsStreaming;
using Argon.Services.L1L2;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Serialization;

/// <summary>
/// Stickers and custom emoji through every serializer that carries them: the message's jsonb column,
/// the event bus and a grain call. The outline is the one <c>bytes</c> field any of them holds.
/// </summary>
[TestFixture]
public class ExpressionSerializationTests
{
    private static readonly byte[] OutlineBytes = [0x01, 0x02, 0x80, 0xFF, 0x00, 0x7F];

    private ServiceProvider services = null!;

    [OneTimeSetUp]
    public void Build() => services = new ServiceCollection().AddArgonSerializer().BuildServiceProvider();

    [OneTimeTearDown]
    public void Dispose() => services.Dispose();

    private static MessageEntitySticker Sticker()
        => new(EntityType.Sticker, 0, 0, 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ExpressionFormat.Lottie,
            Guid.NewGuid(), Guid.NewGuid(), 512, 512, new IonBytes(OutlineBytes), null, null);

    private static MessageEntityCustomEmoji Emoji()
        => new(EntityType.CustomEmoji, 3, 7, 1, Guid.NewGuid(), Guid.NewGuid(), ExpressionFormat.Static, Guid.NewGuid(),
            "party", true, null);

    private static ExpressionItem Item()
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ExpressionKind.Sticker, ExpressionFormat.Video, "wave",
            Guid.NewGuid(), Guid.NewGuid(), 512, 512, 40_000, new IonArray<string>(["👋"]), new IonArray<string>(["hello"]),
            new IonBytes(OutlineBytes), false, 3, null, null);

    private static void AssertSameSticker(IMessageEntity? actual, MessageEntitySticker expected)
    {
        Assert.That(actual, Is.InstanceOf<MessageEntitySticker>());
        var sticker = (MessageEntitySticker)actual!;
        Assert.Multiple(() =>
        {
            Assert.That(sticker.itemId, Is.EqualTo(expected.itemId));
            Assert.That(sticker.format, Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That(sticker.thumbFileId, Is.EqualTo(expected.thumbFileId));
            Assert.That(sticker.outline?.Span.ToArray(), Is.EqualTo(OutlineBytes));
        });
    }

    private static void AssertSameItem(ExpressionItem actual, ExpressionItem expected)
        => Assert.Multiple(() =>
        {
            Assert.That(actual.itemId, Is.EqualTo(expected.itemId));
            Assert.That(actual.format, Is.EqualTo(ExpressionFormat.Video));
            Assert.That(actual.emoji.Values, Is.EqualTo(expected.emoji.Values));
            Assert.That(actual.outline?.Span.ToArray(), Is.EqualTo(OutlineBytes));
        });

    [Test]
    public void The_entities_column_keeps_a_sticker_and_a_custom_emoji()
    {
        var converter = new PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>();
        var sticker   = Sticker();
        var emoji     = Emoji();

        var json     = (string)converter.ConvertToProvider(new List<IMessageEntity> { sticker, emoji })!;
        var restored = (List<IMessageEntity>)converter.ConvertFromProvider(json)!;

        AssertSameSticker(restored[0], sticker);
        Assert.That(restored[1], Is.EqualTo(emoji));
    }

    [Test]
    public void A_sticker_without_an_outline_stays_without_one()
    {
        var converter = new PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>();
        var sticker   = Sticker() with { outline = null };

        var json     = (string)converter.ConvertToProvider(new List<IMessageEntity> { sticker })!;
        var restored = (List<IMessageEntity>)converter.ConvertFromProvider(json)!;

        Assert.That(((MessageEntitySticker)restored[0]).outline, Is.Null);
    }

    [Test]
    public void The_event_bus_carries_an_expression_delta()
    {
        var serializer = new ArgonEventSerializer(NullLogger<ArgonEventSerializer>.Instance);
        var item       = Item();
        var buffer     = new ArrayBufferWriter<byte>();

        serializer.Serialize(buffer, new SpaceExpressionsChanged(item.spaceId, "v2", "v1", new ItemUpserted(item)));
        var restored = serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));

        Assert.That(restored, Is.InstanceOf<SpaceExpressionsChanged>());
        var changed = (SpaceExpressionsChanged)restored!;
        Assert.That(changed.baseVersion, Is.EqualTo("v1"));
        Assert.That(changed.delta, Is.InstanceOf<ItemUpserted>());
        AssertSameItem(((ItemUpserted)changed.delta!).item, item);
    }

    [Test]
    public void The_event_bus_carries_a_message_with_a_sticker()
    {
        var serializer = new ArgonEventSerializer(NullLogger<ArgonEventSerializer>.Instance);
        var sticker    = Sticker();
        var message    = new ArgonMessage(1, null, Guid.NewGuid(), Guid.NewGuid(), "", new IonArray<IMessageEntity>([sticker]),
            DateTime.UtcNow, Guid.NewGuid(), IonArray<ReactionInfo>.Empty, null, null, null, null, null);
        var buffer = new ArrayBufferWriter<byte>();

        serializer.Serialize(buffer, new MessageSent(message.spaceId, message));
        var restored = (MessageSent)serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory))!;

        AssertSameSticker(restored.message.entities[0], sticker);
    }

    [Test]
    public void A_grain_call_carries_a_sticker_entity()
    {
        var serializer = services.GetRequiredService<Serializer>();
        var sticker    = Sticker();

        var restored = serializer.Deserialize<List<IMessageEntity>>(serializer.SerializeToArray(new List<IMessageEntity> { sticker }));

        AssertSameSticker(restored[0], sticker);
    }

    [Test]
    public void The_event_bus_carries_every_kind_of_expression_delta()
    {
        var serializer = new ArgonEventSerializer(NullLogger<ArgonEventSerializer>.Instance);
        var packId     = Guid.NewGuid();
        var ordered    = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var pack       = new ExpressionPack(packId, Guid.NewGuid(), ExpressionKind.Emoji, "Party", "party", null, 0, 4, IonArray<ExpressionItem>.Empty);

        IExpressionDelta[] deltas =
        [
            new PackUpserted(pack),
            new PackDeleted(packId),
            new ItemDeleted(packId, ordered[0]),
            new PacksReordered(ExpressionKind.Sticker, new IonArray<Guid>(ordered)),
            new ItemsReordered(packId, new IonArray<Guid>(ordered))
        ];

        foreach (var delta in deltas)
        {
            var buffer = new ArrayBufferWriter<byte>();
            serializer.Serialize(buffer, new SpaceExpressionsChanged(Guid.NewGuid(), "v2", null, delta));

            var restored = ((SpaceExpressionsChanged)serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory))!).delta;

            Assert.That(restored, Is.InstanceOf(delta.GetType()), delta.GetType().Name);
            switch (restored)
            {
                case PackUpserted p:
                    Assert.That(p.pack.slug, Is.EqualTo("party"));
                    break;
                case PacksReordered r:
                    Assert.That(r.ordered.Values, Is.EqualTo(ordered));
                    break;
                case ItemsReordered r:
                    Assert.That(r.ordered.Values, Is.EqualTo(ordered));
                    break;
            }
        }
    }

    [Test]
    public void The_event_bus_carries_the_custom_emoji_of_a_removed_reaction()
    {
        var serializer = new ArgonEventSerializer(NullLogger<ArgonEventSerializer>.Instance);
        var itemId     = Guid.NewGuid();
        var buffer     = new ArrayBufferWriter<byte>();

        serializer.Serialize(buffer, new ReactionRemoved(Guid.NewGuid(), Guid.NewGuid(), 7, Guid.NewGuid(), ":party:", itemId));
        var restored = (ReactionRemoved)serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory))!;

        Assert.That(restored.customEmojiId, Is.EqualTo(itemId));
    }

    [Test]
    public void The_shared_cache_keeps_a_snapshot_with_its_outlines()
    {
        var item     = Item();
        var bare     = item with { outline = null, itemId = Guid.NewGuid() };
        var pack     = new ExpressionPack(item.packId, item.spaceId, ExpressionKind.Sticker, "Pack", "pack", item.itemId, 1, 2,
            new IonArray<ExpressionItem>([item, bare]));
        var snapshot = new Versioned<IonArray<ExpressionPack>>("0123456789ABCDEF", new IonArray<ExpressionPack>([pack]));

        Assert.That(new IonHybridCacheSerializerFactory().TryCreateSerializer<Versioned<IonArray<ExpressionPack>>>(out var serializer), Is.True);

        var buffer = new ArrayBufferWriter<byte>();
        serializer!.Serialize(snapshot, buffer);
        var restored = serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory));

        var items = restored.Value[0].items;
        Assert.Multiple(() =>
        {
            Assert.That(restored.Version, Is.EqualTo(snapshot.Version));
            Assert.That(restored.Value[0].coverItemId, Is.EqualTo(item.itemId));
            AssertSameItem(items[0], item);
            Assert.That(items[1].outline, Is.Null, "a missing outline came back as an empty one");
        });
    }

    [Test]
    public void A_grain_call_carries_resolved_items()
    {
        var serializer = services.GetRequiredService<Serializer>();
        var item       = Item();
        IReadOnlyDictionary<Guid, ExpressionItem> found = new Dictionary<Guid, ExpressionItem> { [item.itemId] = item };

        var restored = serializer.Deserialize<IReadOnlyDictionary<Guid, ExpressionItem>>(serializer.SerializeToArray(found));

        AssertSameItem(restored[item.itemId], item);
    }

    [Test]
    public void A_grain_call_keeps_a_cleared_field_of_a_patch_apart_from_an_untouched_one()
    {
        var serializer = services.GetRequiredService<Serializer>();
        var patch      = new IonPartial<ExpressionPack>().Modify(x => x.title, "Renamed").Remove(x => x.coverItemId);

        var restored = serializer.Deserialize<IonPartial<ExpressionPack>>(serializer.SerializeToArray(patch));

        Assert.Multiple(() =>
        {
            Assert.That(restored.GetField(x => x.title).Value, Is.EqualTo("Renamed"));
            Assert.That(restored.GetField(x => x.coverItemId).IsRemoved, Is.True);
            Assert.That(restored.GetField(x => x.slug).State, Is.EqualTo(PartialState.None));
        });
    }

    [Test]
    public void A_grain_call_carries_an_item_result()
    {
        var serializer = services.GetRequiredService<Serializer>();
        var item       = Item();

        var restored = serializer.Deserialize<IItemResult>(serializer.SerializeToArray<IItemResult>(new SuccessItem(item)));

        Assert.That(restored, Is.InstanceOf<SuccessItem>());
        AssertSameItem(((SuccessItem)restored).item, item);
    }
}
