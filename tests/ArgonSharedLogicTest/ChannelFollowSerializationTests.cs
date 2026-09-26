namespace ArgonSharedLogicTest;

using Argon;
using Argon.Entities;
using Argon.Features.Clustering;
using Argon.Grains.Interfaces;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;

/// <summary>
/// What a publish carries across grain calls — the copy with its entities and source, and the follow
/// results — through the serializer the cluster runs.
/// </summary>
[TestFixture]
public class ChannelFollowSerializationTests
{
    private ServiceProvider services = null!;

    [OneTimeSetUp]
    public void Build() => services = new ServiceCollection().AddArgonSerializer().BuildServiceProvider();

    [OneTimeTearDown]
    public void Dispose() => services.Dispose();

    private T RoundTrip<T>(T value)
    {
        var serializer = services.GetRequiredService<Serializer>();
        return serializer.Deserialize<T>(serializer.SerializeToArray(value));
    }

    private static CrosspostBatch Batch()
        => new(
            new CrosspostDraft(Guid.NewGuid(), "big news", [
                    new MessageEntityBold(EntityType.Bold, 0, 3, 1),
                    new MessageEntityAttachment(EntityType.Attachment, 0, 0, 1, Guid.NewGuid(), "a.png", 10, "image/png", 1, 1, null, null)
                ],
                new MessageCrosspost
                {
                    SourceSpaceId     = Guid.NewGuid(),
                    SourceChannelId   = Guid.NewGuid(),
                    SourceMessageId   = 42,
                    SourceSpaceName   = "Space",
                    SourceChannelName = "news"
                }),
            [Guid.NewGuid(), Guid.NewGuid()],
            DateTimeOffset.UtcNow);

    [Test]
    public void A_publish_arrives_with_its_entities_and_its_source()
    {
        var batch = Batch();

        foreach (var carried in new[] { RoundTrip(batch), RoundTrip(Either<CrosspostBatch, PublishMessageError>.Success(batch)).Value })
        {
            Assert.Multiple(() =>
            {
                Assert.That(carried.Targets, Is.EqualTo(batch.Targets));
                Assert.That(carried.Draft.Entities.Select(e => e.GetType()),
                    Is.EqualTo(new[] { typeof(MessageEntityBold), typeof(MessageEntityAttachment) }));
                Assert.That(carried.Draft.Source, Is.EqualTo(batch.Draft.Source));
                Assert.That(carried.PublishedAt, Is.EqualTo(batch.PublishedAt));
            });
        }
    }

    [Test]
    public void A_refused_publish_arrives_as_its_error()
        => Assert.That(RoundTrip(Either<CrosspostBatch, PublishMessageError>.Failure(PublishMessageError.ALREADY_PUBLISHED)).Error,
            Is.EqualTo(PublishMessageError.ALREADY_PUBLISHED));

    [Test]
    public void A_follow_result_arrives_with_its_link()
    {
        var link = new ChannelFollowLink(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Source", "news", Guid.NewGuid(), Guid.NewGuid(),
            "Target", "general", DateTime.UtcNow, Guid.NewGuid(), "avatar", null);

        var carried = RoundTrip<IFollowChannelResult>(new SuccessFollowChannel(link));

        Assert.That(carried, Is.InstanceOf<SuccessFollowChannel>());
        Assert.That(((SuccessFollowChannel)carried).link, Is.EqualTo(link));
    }
}
