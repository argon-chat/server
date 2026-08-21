namespace Argon.Core.Entities.Data;

/// <summary>
/// Plain DTOs carried across the <c>IFeatureFlagGrain</c> boundary for flag/override management.
/// Orleans serializes everything via Newtonsoft JSON, so no [GenerateSerializer] is required.
/// </summary>
public sealed record FeatureFlagSummaryDto(
    string Id,
    string? Description,
    bool DefaultEnabled,
    int? RolloutPercentage,
    bool HasVariants,
    string? UssdActivationCode,
    DateTimeOffset? ExpiresAt,
    int OverrideCount,
    DateTimeOffset CreatedAt);

public sealed record FeatureFlagOverrideDto(
    Guid OverrideId,
    FeatureFlagScope Scope,
    string TargetId,
    bool? Enabled,
    int? RolloutPercentage,
    string? ForcedVariant,
    DateTimeOffset CreatedAt);

public sealed record FeatureFlagDetailsDto(
    string Id,
    string? Description,
    bool DefaultEnabled,
    int? RolloutPercentage,
    string? Variants,
    string? UssdActivationCode,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    List<FeatureFlagOverrideDto> Overrides);

public sealed record FeatureFlagInput(
    string FlagId,
    string? Description,
    bool DefaultEnabled,
    int? RolloutPercentage,
    string? Variants,
    string? UssdActivationCode,
    DateTimeOffset? ExpiresAt);

public sealed record FeatureFlagOverrideInput(
    string FlagId,
    FeatureFlagScope Scope,
    string TargetId,
    bool? Enabled,
    int? RolloutPercentage,
    string? ForcedVariant);

public sealed record FeatureFlagOpResult(bool Success, string? FlagId, string? Error)
{
    public static FeatureFlagOpResult Ok(string? flagId = null) => new(true, flagId, null);
    public static FeatureFlagOpResult Fail(string error)        => new(false, null, error);
}
