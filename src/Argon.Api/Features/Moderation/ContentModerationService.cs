namespace Argon.Features.Moderation;

using System.Diagnostics;
using Argon.Features.Storage;

public sealed class ContentModerationService(
    ContentModerator? moderator,
    IS3StorageService s3,
    ILogger<ContentModerationService> logger) : IContentModerationService
{
    public bool IsAvailable => moderator != null;

    public async Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default)
    {
        var purposeTag = new KeyValuePair<string, object?>("purpose", purpose.ToString());

        if (moderator == null)
        {
            ModerationInstruments.EvaluationsSkipped.Add(1, purposeTag);
            return new ContentModerationResult
            {
                Action = ContentAction.Allow,
                StagesUsed = 0,
                ElapsedMs = 0,
                Scores = new Dictionary<string, float>()
            };
        }

        using var activity = ModerationInstruments.ActivitySource.StartActivity("Moderation.Evaluate");
        activity?.SetTag("moderation.purpose", purpose.ToString());
        activity?.SetTag("moderation.s3_key", s3Key);

        var totalSw = Stopwatch.StartNew();

        // Download image from S3
        var downloadSw = Stopwatch.StartNew();
        using var imageStream = await s3.GetObjectStreamAsync(s3Key, ct);
        downloadSw.Stop();
        ModerationInstruments.S3DownloadDurationMs.Record(downloadSw.Elapsed.TotalMilliseconds, purposeTag);

        if (imageStream == null)
        {
            logger.LogWarning("Content moderation: failed to download {S3Key} from S3", s3Key);
            ModerationInstruments.EvaluationsSkipped.Add(1, purposeTag);
            return new ContentModerationResult
            {
                Action = ContentAction.Allow,
                StagesUsed = 0,
                ElapsedMs = totalSw.Elapsed.TotalMilliseconds,
                Scores = new Dictionary<string, float>()
            };
        }

        activity?.SetTag("moderation.image_size", imageStream.Length);

        // Run inference
        ModerationInstruments.ActiveInferences.Add(1);
        ContentModerationResult result;
        try
        {
            var inferenceSw = Stopwatch.StartNew();
            result = moderator.Evaluate(imageStream);
            inferenceSw.Stop();

            ModerationInstruments.InferenceDurationMs.Record(inferenceSw.Elapsed.TotalMilliseconds,
                purposeTag,
                new KeyValuePair<string, object?>("stage", result.StagesUsed == 2 ? "both" : "primary"));
        }
        finally
        {
            ModerationInstruments.ActiveInferences.Add(-1);
        }

        totalSw.Stop();

        ModerationInstruments.EvaluationDurationMs.Record(totalSw.Elapsed.TotalMilliseconds,
            purposeTag,
            new KeyValuePair<string, object?>("stages_used", result.StagesUsed));

        ModerationInstruments.EvaluationsTotal.Add(1,
            purposeTag,
            new KeyValuePair<string, object?>("action", result.Action.ToString()));

        if (result.Action == ContentAction.Deny)
            ModerationInstruments.RejectionsTotal.Add(1, purposeTag);

        activity?.SetTag("moderation.action", result.Action.ToString());
        activity?.SetTag("moderation.stages_used", result.StagesUsed);
        activity?.SetTag("moderation.elapsed_ms", result.ElapsedMs);

        return result;
    }
}

public sealed class NoOpContentModerationService : IContentModerationService
{
    public bool IsAvailable => false;

    public Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default)
        => Task.FromResult(new ContentModerationResult
        {
            Action = ContentAction.Allow,
            StagesUsed = 0,
            ElapsedMs = 0,
            Scores = new Dictionary<string, float>()
        });
}
