namespace Argon.Shared.SharedGrains;

[Alias("Argon.Shared.SharedGrains.IAccessTokenGrain")]
public interface IAccessTokenGrain : IGrainWithGuidKey
{
    [Alias(nameof(GenerateAccessHashAsync))]
    Task<int> GenerateAccessHashAsync(Guid userId, DateTime timestampUtc);
    [Alias(nameof(GenerateBatchAccessHashAsync))]
    Task<Dictionary<Guid, int>> GenerateBatchAccessHashAsync(List<Guid> userIds, DateTime timestampUtc);
    [Alias(nameof(ValidateAccessHash))]
    Task<ValidateAccessHashError> ValidateAccessHash(Guid userId, int accessGuid, int maxAgeDays = 3);
}

public enum ValidateAccessHashError
{
    OK,
    EXPIRED,
    ATTEMPT_FALSIFICATION
}