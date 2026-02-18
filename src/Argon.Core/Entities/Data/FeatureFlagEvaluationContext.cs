namespace Argon.Core.Entities.Data;

/// <summary>
/// Context for evaluating feature flags against override layers.
/// </summary>
public sealed record FeatureFlagEvaluationContext
{
    /// <summary>
    /// User identifier for user-level overrides.
    /// </summary>
    public Guid? UserId { get; init; }

    /// <summary>
    /// ISO 3166-1 alpha-2 country code (e.g., "US", "RU") for country-level overrides.
    /// </summary>
    public string? CountryCode { get; init; }

    /// <summary>
    /// Client application identifier for client-level overrides.
    /// </summary>
    public string? ClientId { get; init; }

    public static FeatureFlagEvaluationContext Empty => new();

    public static FeatureFlagEvaluationContext ForUser(Guid userId)
        => new() { UserId = userId };

    public static FeatureFlagEvaluationContext ForUser(Guid userId, string? countryCode, string? clientId)
        => new() { UserId = userId, CountryCode = countryCode, ClientId = clientId };
}

/// <summary>
/// Result of feature flag evaluation.
/// </summary>
public sealed record FeatureFlagResult : IMapper<FeatureFlagResult, FeatureFlagData>
{
    public required string FlagId { get; init; }
    public required bool IsEnabled { get; init; }
    public string? Variant { get; init; }
    public FeatureFlagScope ResolvedAt { get; init; }

    public static FeatureFlagResult Disabled(string flagId, FeatureFlagScope resolvedAt = FeatureFlagScope.Global)
        => new() { FlagId = flagId, IsEnabled = false, ResolvedAt = resolvedAt };

    public static FeatureFlagResult Enabled(string flagId, FeatureFlagScope scope, string? variant = null)
        => new() { FlagId = flagId, IsEnabled = true, ResolvedAt = scope, Variant = variant };

    public static FeatureFlagData Map(scoped in FeatureFlagResult self)
        => new(self.FlagId, self.IsEnabled, self.Variant, (int)self.ResolvedAt);
}
