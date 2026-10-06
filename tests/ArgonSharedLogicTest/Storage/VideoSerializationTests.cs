namespace ArgonSharedLogicTest.Storage;

using System.Buffers;
using Argon.Entities;
using Argon.Features.Clustering;
using Argon.Features.NatsStreaming;
using Argon.Features.Storage;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Serialization;

/// <summary>
/// A video entity through every serializer that carries one — the message's jsonb column, the event bus
/// and a grain call — and the declaration through the ticket's jsonb column.
/// </summary>
[TestFixture]
public class VideoSerializationTests
{
    private ServiceProvider services = null!;

    [OneTimeSetUp]
    public void Build() => services = new ServiceCollection().AddArgonSerializer().BuildServiceProvider();

    [OneTimeTearDown]
    public void Dispose() => services.Dispose();

    private static MessageEntityVideo Video()
        => new(EntityType.Video, 0, 0, 1, Guid.NewGuid(), "clip.mp4", 123_456, "video/mp4", 1080, 1920, 61_000, true, "avc1.64001f",
            "1QcSHQRnh493V4dIh4eXh1h4kJUI", Guid.NewGuid(), Guid.NewGuid(), new VideoStoryboard(160, 90, 10, 61, 1000), 65_536,
            new IonArray<VideoVariant>([new VideoVariant(Guid.NewGuid(), 540, 960, 40_000, "avc1.4d401e", 900_000, null)]),
            null, null, null);

    private static void AssertSame(IMessageEntity? actual, MessageEntityVideo expected)
    {
        Assert.That(actual, Is.InstanceOf<MessageEntityVideo>());
        var video = (MessageEntityVideo)actual!;
        Assert.Multiple(() =>
        {
            // IonArray compares by reference, so the list is compared on its own.
            Assert.That(video with { variants = default }, Is.EqualTo(expected with { variants = default }));
            Assert.That(video.variants.Values, Is.EqualTo(expected.variants.Values));
        });
    }

    [Test]
    public void The_entities_column_keeps_a_video()
    {
        var converter = new PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>();
        var video     = Video();

        var json     = (string)converter.ConvertToProvider(new List<IMessageEntity> { video, VideoMedia.Placeholder(Guid.NewGuid()) })!;
        var restored = (List<IMessageEntity>)converter.ConvertFromProvider(json)!;

        AssertSame(restored[0], video);
        Assert.That(((MessageEntityVideo)restored[1]).variants.Values ?? [], Is.Empty);
    }

    [Test]
    public void The_event_bus_carries_a_message_with_a_video()
    {
        var serializer = new ArgonEventSerializer(NullLogger<ArgonEventSerializer>.Instance);
        var video      = Video();
        var message    = new ArgonMessage(1, null, Guid.NewGuid(), Guid.NewGuid(), "", new IonArray<IMessageEntity>([video]),
            DateTime.UtcNow, Guid.NewGuid(), IonArray<ReactionInfo>.Empty, null, null, null, null, null);
        var buffer = new ArrayBufferWriter<byte>();

        serializer.Serialize(buffer, new MessageSent(message.spaceId, message));
        var restored = (MessageSent)serializer.Deserialize(new ReadOnlySequence<byte>(buffer.WrittenMemory))!;

        AssertSame(restored.message.entities[0], video);
    }

    [Test]
    public void A_grain_call_carries_a_video_entity()
    {
        var serializer = services.GetRequiredService<Serializer>();
        var video      = Video();

        var restored = serializer.Deserialize<List<IMessageEntity>>(serializer.SerializeToArray(new List<IMessageEntity> { video }))!;

        AssertSame(restored[0], video);
    }

    [Test]
    public void The_ticket_keeps_the_declaration_without_its_hash()
    {
        var declared = new VideoUploadDeclaration("clip.mp4", "video/mp4", 123_456, new IonBytes(new byte[32]), 1920, 1080, 61_000, true,
            "avc1.64001f", null, Guid.NewGuid(), Guid.NewGuid(), new VideoStoryboard(160, 90, 10, 61, 1000), null);

        var restored = VideoMedia.Deserialize(VideoMedia.Serialize(declared));

        Assert.That(restored, Is.EqualTo(declared with { sha256 = null }));
    }

    [TestCase("avc1.64001f", "avc1")]
    [TestCase("hvc1.1.6.L93.B0", "hvc1")]
    [TestCase("vp09", "vp09")]
    public void The_codec_family_is_the_sample_entry(string codec, string family)
        => Assert.That(VideoMedia.CodecFamily(codec).ToString(), Is.EqualTo(family));
}
