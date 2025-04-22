namespace Argon.Shared.SharedGrains;

[Alias("Argon.Shared.SharedGrains.IAccessTokenGrain")]
public interface IAccessTokenGrain : IGrainWithGuidKey
{
    [Alias(nameof(GenerateAccessGuidAsync))]
    Task<Guid> GenerateAccessGuidAsync(Guid userId, DateTime timestampUtc);
    [Alias(nameof(GenerateBatchAccessGuidAsync))]
    Task<Dictionary<Guid, Guid>> GenerateBatchAccessGuidAsync(List<Guid> userIds, DateTime timestampUtc);
    [Alias(nameof(ValidateAccessGuid))]
    Task<bool> ValidateAccessGuid(Guid userId, Guid accessGuid, int maxAgeSeconds = 300);
}