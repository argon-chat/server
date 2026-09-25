namespace ArgonSharedLogicTest.Moderation;

using Argon.Api.Grains;
using Argon.Features.Moderation;
using Argon.Features.Storage;
using Microsoft.Extensions.Logging.Abstractions;

/// <summary>
/// The grain in front of image moderation, and the one decision it makes itself: a moderator that
/// fails lets the upload through.
/// </summary>
/// <remarks>
/// The integration host has no models on disk, so it runs the no-op service, which never fails — the
/// fail-open branch cannot be reached there. It is reached here with a service that throws, the way
/// a missing object or a crashed inference session does in production.
/// </remarks>
[TestFixture]
public class ContentModerationGrainTests
{
    private sealed class Throwing : IContentModerationService
    {
        public bool IsAvailable => true;

        public Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default)
            => throw new InvalidOperationException("inference session crashed");
    }

    private sealed class Denying : IContentModerationService
    {
        public bool IsAvailable => true;

        public Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default)
            => Task.FromResult(new ContentModerationResult
            {
                Action     = ContentAction.Deny,
                StagesUsed = 2,
                ElapsedMs  = 12.5,
                Scores     = new Dictionary<string, float> { ["nsfw"] = 0.97f }
            });
    }

    [Test]
    public async Task A_moderator_that_fails_lets_the_upload_through_with_nothing_scored()
    {
        var grain  = new ContentModerationGrain(new Throwing(), NullLogger<ContentModerationGrain>.Instance);
        var result = await grain.EvaluateAsync("avatars/some-user/some-file", FilePurpose.Avatar);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(ContentAction.Allow),
                "a broken moderator must not turn every upload into a rejection");
            Assert.That(result.StagesUsed, Is.Zero, "the fallback claims to have run a stage it never ran");
            Assert.That(result.Scores, Is.Empty);
        });
    }

    [Test]
    public async Task A_verdict_from_the_moderator_is_passed_through_as_it_is()
    {
        var grain  = new ContentModerationGrain(new Denying(), NullLogger<ContentModerationGrain>.Instance);
        var result = await grain.EvaluateAsync("avatars/some-user/some-file", FilePurpose.Avatar);

        Assert.Multiple(() =>
        {
            Assert.That(result.Action, Is.EqualTo(ContentAction.Deny));
            Assert.That(result.StagesUsed, Is.EqualTo(2));
            Assert.That(result.Scores["nsfw"], Is.EqualTo(0.97f));
        });
    }
}
