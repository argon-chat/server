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
}
