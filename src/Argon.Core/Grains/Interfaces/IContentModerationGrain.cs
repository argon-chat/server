namespace Argon.Api.Grains.Interfaces;

using Argon.Features.Moderation;
using Argon.Features.Storage;

[Alias(nameof(IContentModerationGrain))]
public interface IContentModerationGrain : IGrainWithGuidKey
{
    [Alias(nameof(EvaluateAsync)), ResponseTimeout("00:01:00")]
    Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default);
}
