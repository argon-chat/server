namespace ArgonComplexTest;

using System.Collections.Concurrent;
using Argon.Features.Moderation;
using Argon.Features.Storage;

/// <summary>
/// The image classifier, with verdicts a test hands it.
/// </summary>
/// <remarks>
/// The suite has no model, so the host would otherwise run <c>NoOpContentModerationService</c> and
/// allow everything; this answers exactly that for every object nobody flagged, and whatever
/// <see cref="Deny"/> was told for the rest — which is the only way the rejection branches of the
/// avatar uploads can run.
/// </remarks>
public sealed class FakeContentModeration : IContentModerationService
{
    private readonly ConcurrentDictionary<string, ContentModerationResult> verdicts = new();

    public bool IsAvailable => false;

    /// <summary>Makes the classifier reject the object at <paramref name="s3Key"/> with these scores.</summary>
    public void Deny(string s3Key, Dictionary<string, float> scores, Dictionary<string, float>? refined = null)
        => verdicts[s3Key] = new ContentModerationResult
        {
            Action        = ContentAction.Deny,
            StagesUsed    = refined is null ? 1 : 2,
            ElapsedMs     = 1,
            Scores        = scores,
            RefinedScores = refined
        };

    public Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default)
        => Task.FromResult(verdicts.TryGetValue(s3Key, out var verdict)
            ? verdict
            : new ContentModerationResult
            {
                Action     = ContentAction.Allow,
                StagesUsed = 0,
                ElapsedMs  = 0,
                Scores     = new Dictionary<string, float>()
            });
}
