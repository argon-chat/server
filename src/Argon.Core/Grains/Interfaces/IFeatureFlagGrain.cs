namespace Argon.Grains.Interfaces;

using Argon.Core.Entities.Data;

/// <summary>
/// Singleton grain for feature flag evaluation.
/// Caches all flags and evaluates them for given context.
/// Use Guid.Empty as key.
/// </summary>
[Alias("Argon.Grains.Interfaces.IFeatureFlagGrain")]
public interface IFeatureFlagGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Evaluates a single feature flag for the given context.
    /// </summary>
    [Alias(nameof(EvaluateAsync))]
    ValueTask<FeatureFlagResult> EvaluateAsync(string flagId, FeatureFlagEvaluationContext context);

    /// <summary>
    /// Evaluates multiple flags at once for the given context.
    /// </summary>
    [Alias(nameof(EvaluateManyAsync))]
    ValueTask<Dictionary<string, FeatureFlagResult>> EvaluateManyAsync(List<string> flagIds, FeatureFlagEvaluationContext context);

    /// <summary>
    /// Evaluates ALL flags for the given context.
    /// Returns only enabled flags by default.
    /// </summary>
    [Alias(nameof(EvaluateAllAsync))]
    ValueTask<Dictionary<string, FeatureFlagResult>> EvaluateAllAsync(FeatureFlagEvaluationContext context, bool includeDisabled = false);

    /// <summary>
    /// Invalidates cached flag data, forcing reload on next evaluation.
    /// </summary>
    [Alias(nameof(InvalidateCacheAsync))]
    ValueTask InvalidateCacheAsync();

    /// <summary>
    /// Resolves the flag id whose USSD activation code matches the given dialed code (exact, trimmed).
    /// Returns null when no flag claims the code.
    /// </summary>
    [Alias(nameof(FindFlagIdByUssdCodeAsync))]
    ValueTask<string?> FindFlagIdByUssdCodeAsync(string code);

    /// <summary>
    /// Activates a flag for a single user by upserting a User-scope override (Enabled = true).
    /// Idempotent. Returns the evaluated result for that user (disabled result when the flag is missing/expired).
    /// Does not emit notifications — the caller fires the user event.
    /// </summary>
    [Alias(nameof(ActivateForUserAsync))]
    ValueTask<FeatureFlagResult> ActivateForUserAsync(Guid userId, string flagId);

    /// <summary>
    /// Lists all non-deleted flags with override counts for admin management.
    /// </summary>
    [Alias(nameof(ListFlagsAsync))]
    ValueTask<List<FeatureFlagSummaryDto>> ListFlagsAsync();

    /// <summary>
    /// Returns a single flag with its overrides, or null when not found.
    /// </summary>
    [Alias(nameof(GetFlagAsync))]
    ValueTask<FeatureFlagDetailsDto?> GetFlagAsync(string flagId);

    [Alias(nameof(CreateFlagAsync))]
    ValueTask<FeatureFlagOpResult> CreateFlagAsync(FeatureFlagInput input);

    [Alias(nameof(UpdateFlagAsync))]
    ValueTask<FeatureFlagOpResult> UpdateFlagAsync(FeatureFlagInput input);

    [Alias(nameof(DeleteFlagAsync))]
    ValueTask<FeatureFlagOpResult> DeleteFlagAsync(string flagId);

    [Alias(nameof(SetOverrideAsync))]
    ValueTask<FeatureFlagOpResult> SetOverrideAsync(FeatureFlagOverrideInput input);

    [Alias(nameof(DeleteOverrideAsync))]
    ValueTask<FeatureFlagOpResult> DeleteOverrideAsync(Guid overrideId);
}
