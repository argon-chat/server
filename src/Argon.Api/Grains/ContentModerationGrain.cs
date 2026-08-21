namespace Argon.Api.Grains;

using Argon.Features.Moderation;
using Argon.Features.Storage;
using Interfaces;
using Orleans;
using Orleans.Concurrency;

[StatelessWorker]
public class ContentModerationGrain(
    IContentModerationService moderationService,
    ILogger<ContentModerationGrain> logger) : Grain, IContentModerationGrain
{
    public async Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default)
    {
        try
        {
            return await moderationService.EvaluateAsync(s3Key, purpose, ct);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Content moderation failed for {S3Key}, defaulting to Allow", s3Key);
            return new ContentModerationResult
            {
                Action = ContentAction.Allow,
                StagesUsed = 0,
                ElapsedMs = 0,
                Scores = new Dictionary<string, float>()
            };
        }
    }
}
