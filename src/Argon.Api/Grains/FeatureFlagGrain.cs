namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Singleton grain for feature flag evaluation.
/// Caches all flags and overrides, evaluates by context.
/// </summary>
[StatelessWorker]
public sealed class FeatureFlagGrain(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    ILogger<FeatureFlagGrain> logger) : Grain, IFeatureFlagGrain
{
    private FrozenDictionary<string, FeatureFlagEntity> _flags = FrozenDictionary<string, FeatureFlagEntity>.Empty;
    private FrozenDictionary<string, List<FeatureFlagOverrideEntity>> _overridesByFlag = FrozenDictionary<string, List<FeatureFlagOverrideEntity>>.Empty;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public async ValueTask<FeatureFlagResult> EvaluateAsync(string flagId, FeatureFlagEvaluationContext context)
    {
        await EnsureCacheLoadedAsync();
        return EvaluateFlag(flagId, context);
    }

    public async ValueTask<Dictionary<string, FeatureFlagResult>> EvaluateManyAsync(
        List<string> flagIds,
        FeatureFlagEvaluationContext context)
    {
        await EnsureCacheLoadedAsync();

        var results = new Dictionary<string, FeatureFlagResult>(flagIds.Count);
        foreach (var flagId in flagIds)
            results[flagId] = EvaluateFlag(flagId, context);

        return results;
    }

    public async ValueTask<Dictionary<string, FeatureFlagResult>> EvaluateAllAsync(
        FeatureFlagEvaluationContext context,
        bool includeDisabled = false)
    {
        await EnsureCacheLoadedAsync();

        var results = new Dictionary<string, FeatureFlagResult>(_flags.Count);
        foreach (var flagId in _flags.Keys)
        {
            var result = EvaluateFlag(flagId, context);
            if (includeDisabled || result.IsEnabled)
                results[flagId] = result;
        }

        return results;
    }

    public ValueTask InvalidateCacheAsync()
    {
        _cacheExpiry = DateTime.MinValue;
        return ValueTask.CompletedTask;
    }

    private FeatureFlagResult EvaluateFlag(string flagId, FeatureFlagEvaluationContext context)
    {
        if (!_flags.TryGetValue(flagId, out var flag))
        {
            logger.LogDebug("Feature flag {FlagId} not found", flagId);
            return FeatureFlagResult.Disabled(flagId);
        }

        if (flag.ExpiresAt.HasValue && flag.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            logger.LogDebug("Feature flag {FlagId} has expired", flagId);
            return FeatureFlagResult.Disabled(flagId);
        }

        var overrides = _overridesByFlag.GetValueOrDefault(flagId) ?? [];
        return EvaluateWithOverrides(flag, overrides, context);
    }

    private FeatureFlagResult EvaluateWithOverrides(
        FeatureFlagEntity flag,
        List<FeatureFlagOverrideEntity> overrides,
        FeatureFlagEvaluationContext context)
    {
        // Find applicable overrides by priority
        var userOverride = context.UserId.HasValue
            ? overrides.FirstOrDefault(o => o.Scope == FeatureFlagScope.User && o.TargetId == context.UserId.Value.ToString())
            : null;

        var countryOverride = !string.IsNullOrEmpty(context.CountryCode)
            ? overrides.FirstOrDefault(o => o.Scope == FeatureFlagScope.Country && o.TargetId.Equals(context.CountryCode, StringComparison.OrdinalIgnoreCase))
            : null;

        var clientOverride = !string.IsNullOrEmpty(context.ClientId)
            ? overrides.FirstOrDefault(o => o.Scope == FeatureFlagScope.Client && o.TargetId.Equals(context.ClientId, StringComparison.OrdinalIgnoreCase))
            : null;

        // Resolve enabled state (highest priority wins)
        var (enabled, resolvedScope) = ResolveEnabled(flag, userOverride, countryOverride, clientOverride, context);

        if (!enabled)
            return FeatureFlagResult.Disabled(flag.Id, resolvedScope);

        // Resolve variant
        var variant = ResolveVariant(flag, context, userOverride, countryOverride, clientOverride);

        return FeatureFlagResult.Enabled(flag.Id, resolvedScope, variant);
    }

    private (bool Enabled, FeatureFlagScope Scope) ResolveEnabled(
        FeatureFlagEntity flag,
        FeatureFlagOverrideEntity? userOverride,
        FeatureFlagOverrideEntity? countryOverride,
        FeatureFlagOverrideEntity? clientOverride,
        FeatureFlagEvaluationContext context)
    {
        // User-level override (highest priority)
        if (userOverride?.Enabled.HasValue == true)
            return (userOverride.Enabled.Value, FeatureFlagScope.User);

        // Country-level override
        if (countryOverride?.Enabled.HasValue == true)
            return (countryOverride.Enabled.Value, FeatureFlagScope.Country);

        // Client-level override
        if (clientOverride?.Enabled.HasValue == true)
            return (clientOverride.Enabled.Value, FeatureFlagScope.Client);

        // Global default with optional percentage rollout
        return (EvaluateGlobalDefault(flag, context), FeatureFlagScope.Global);
    }

    private static bool EvaluateGlobalDefault(FeatureFlagEntity flag, FeatureFlagEvaluationContext context)
    {
        if (!flag.RolloutPercentage.HasValue)
            return flag.DefaultEnabled;

        // Use user ID for consistent rollout, or flag ID for anonymous
        var hashInput = context.UserId.HasValue
            ? $"{flag.Id}:{context.UserId.Value}"
            : flag.Id;

        var bucket = GetStableHash(hashInput) % 100;
        return bucket < flag.RolloutPercentage.Value;
    }

    private string? ResolveVariant(
        FeatureFlagEntity flag,
        FeatureFlagEvaluationContext context,
        FeatureFlagOverrideEntity? userOverride,
        FeatureFlagOverrideEntity? countryOverride,
        FeatureFlagOverrideEntity? clientOverride)
    {
        // Check for forced variants in priority order
        if (!string.IsNullOrEmpty(userOverride?.ForcedVariant))
            return userOverride.ForcedVariant;

        if (!string.IsNullOrEmpty(countryOverride?.ForcedVariant))
            return countryOverride.ForcedVariant;

        if (!string.IsNullOrEmpty(clientOverride?.ForcedVariant))
            return clientOverride.ForcedVariant;

        // No variants configured
        if (string.IsNullOrEmpty(flag.Variants))
            return null;

        return AssignVariant(flag, context.UserId);
    }

    private string? AssignVariant(FeatureFlagEntity flag, Guid? userId)
    {
        try
        {
            var variants = JsonSerializer.Deserialize<Dictionary<string, int>>(flag.Variants!);
            if (variants is null || variants.Count == 0)
                return null;

            var totalWeight = variants.Values.Sum();
            if (totalWeight <= 0)
                return variants.Keys.First();

            var hashInput = userId.HasValue
                ? $"{flag.Id}:{userId.Value}"
                : $"{flag.Id}:{Guid.NewGuid()}";

            var bucket = GetStableHash(hashInput) % totalWeight;

            var cumulative = 0;
            foreach (var (variant, weight) in variants)
            {
                cumulative += weight;
                if (bucket < cumulative)
                    return variant;
            }

            return variants.Keys.Last();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse variants JSON for flag {FlagId}", flag.Id);
            return null;
        }
    }

    private static int GetStableHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Math.Abs(BitConverter.ToInt32(bytes, 0));
    }

    private async Task EnsureCacheLoadedAsync()
    {
        if (DateTime.UtcNow < _cacheExpiry)
            return;

        await using var ctx = await contextFactory.CreateDbContextAsync();

        var flags = await ctx.FeatureFlags
            .AsNoTracking()
            .Where(f => !f.IsDeleted)
            .ToListAsync();

        var overrides = await ctx.FeatureFlagOverrides
            .AsNoTracking()
            .Where(o => !o.IsDeleted)
            .ToListAsync();

        _flags = flags.ToFrozenDictionary(f => f.Id);
        _overridesByFlag = overrides
            .GroupBy(o => o.FeatureFlagId)
            .ToFrozenDictionary(
                g => g.Key,
                g => g.OrderByDescending(o => o.Scope).ToList());

        _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);

        logger.LogDebug("Feature flags cache loaded: {FlagCount} flags, {OverrideCount} overrides",
            _flags.Count, overrides.Count);
    }
}
