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
/// What a publish carries across grain calls — the copy with its entities and source, the publish
/// result and the follow results — through the serializer the cluster runs.
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

    private static CrosspostDraft Draft()
        => new(Guid.NewGuid(), "big news", [
                new MessageEntityBold(EntityType.Bold, 0, 3, 1),
                new MessageEntityAttachment(EntityType.Attachment, 0, 0, 1, Guid.NewGuid(), "a.png", 10, "image/png", 1, 1, null, null)
            ],
            new MessageCrosspost
            {
                SourceSpaceId     = Guid.NewGuid(),
                SourceChannelId   = Guid.NewGuid(),
                SourceMessageId   = 42,
                SourceSpaceName   = "Space",
                SourceChannelName = "news",
                HideAuthor        = true
            });

    [Test]
    public void A_copy_arrives_with_its_entities_and_its_source()
    {
        var draft   = Draft();
        var carried = RoundTrip(draft);

        Assert.Multiple(() =>
        {
            Assert.That(carried.AuthorId, Is.EqualTo(draft.AuthorId));
            Assert.That(carried.Entities.Select(e => e.GetType()),
                Is.EqualTo(new[] { typeof(MessageEntityBold), typeof(MessageEntityAttachment) }));
            Assert.That(carried.Source, Is.EqualTo(draft.Source));
        });
    }

    [Test]
    public void A_publish_arrives_with_its_time_and_target_count()
    {
        var published = new PublishedCrosspost(DateTimeOffset.UtcNow, 3);

        foreach (var carried in new[] { RoundTrip(published), RoundTrip(Either<PublishedCrosspost, PublishMessageError>.Success(published)).Value })
            Assert.That(carried, Is.EqualTo(published));
    }

    [Test]
    public void A_refused_publish_arrives_as_its_error()
        => Assert.That(RoundTrip(Either<PublishedCrosspost, PublishMessageError>.Failure(PublishMessageError.ALREADY_PUBLISHED)).Error,
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
