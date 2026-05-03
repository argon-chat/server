namespace Argon.Features.Moderation;

using Argon.Features.Storage;

public interface IContentModerationService
{
    bool IsAvailable { get; }
    Task<ContentModerationResult> EvaluateAsync(string s3Key, FilePurpose purpose, CancellationToken ct = default);
}
