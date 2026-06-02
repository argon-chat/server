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

    public async ValueTask<string?> FindFlagIdByUssdCodeAsync(string code)
    {
        var trimmed = code?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;

        await using var ctx = await contextFactory.CreateDbContextAsync();

        return await ctx.FeatureFlags
            .AsNoTracking()
            .Where(f => !f.IsDeleted && f.UssdActivationCode == trimmed)
            .Select(f => f.Id)
            .FirstOrDefaultAsync();
    }

    public async ValueTask<FeatureFlagResult> ActivateForUserAsync(Guid userId, string flagId)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        var flag = await ctx.FeatureFlags.FirstOrDefaultAsync(f => f.Id == flagId && !f.IsDeleted);
        if (flag is null)
        {
            logger.LogWarning("ActivateForUserAsync: flag {FlagId} not found", flagId);
            return FeatureFlagResult.Disabled(flagId);
        }

        if (flag.ExpiresAt.HasValue && flag.ExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            logger.LogWarning("ActivateForUserAsync: flag {FlagId} has expired", flagId);
            return FeatureFlagResult.Disabled(flagId);
        }

        var targetId = userId.ToString();
        var existing = await ctx.FeatureFlagOverrides.FirstOrDefaultAsync(o =>
            o.FeatureFlagId == flagId && o.Scope == FeatureFlagScope.User && o.TargetId == targetId);

        if (existing is null)
        {
            ctx.FeatureFlagOverrides.Add(new FeatureFlagOverrideEntity
            {
                Id            = Guid.CreateVersion7(),
                FeatureFlagId = flagId,
                Scope         = FeatureFlagScope.User,
                TargetId      = targetId,
                Enabled       = true
            });
        }
        else
        {
            existing.Enabled   = true;
            existing.IsDeleted = false;
            existing.DeletedAt = null;
        }

        await ctx.SaveChangesAsync();

        // Refresh this activation's cache so the returned evaluation reflects the new override.
        _cacheExpiry = DateTime.MinValue;
        await EnsureCacheLoadedAsync();

        return EvaluateFlag(flagId, FeatureFlagEvaluationContext.ForUser(userId));
    }

    public async ValueTask<List<FeatureFlagSummaryDto>> ListFlagsAsync()
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        return await ctx.FeatureFlags
            .AsNoTracking()
            .Where(f => !f.IsDeleted)
            .OrderBy(f => f.Id)
            .Select(f => new FeatureFlagSummaryDto(
                f.Id,
                f.Description,
                f.DefaultEnabled,
                f.RolloutPercentage,
                f.Variants != null,
                f.UssdActivationCode,
                f.ExpiresAt,
                f.Overrides.Count(o => !o.IsDeleted),
                f.CreatedAt))
            .ToListAsync();
    }

    public async ValueTask<FeatureFlagDetailsDto?> GetFlagAsync(string flagId)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        var flag = await ctx.FeatureFlags
            .AsNoTracking()
            .Include(f => f.Overrides.Where(o => !o.IsDeleted))
            .FirstOrDefaultAsync(f => f.Id == flagId && !f.IsDeleted);

        if (flag is null)
            return null;

        return new FeatureFlagDetailsDto(
            flag.Id,
            flag.Description,
            flag.DefaultEnabled,
            flag.RolloutPercentage,
            flag.Variants,
            flag.UssdActivationCode,
            flag.ExpiresAt,
            flag.CreatedAt,
            flag.Overrides
                .Select(o => new FeatureFlagOverrideDto(
                    o.Id, o.Scope, o.TargetId, o.Enabled, o.RolloutPercentage, o.ForcedVariant, o.CreatedAt))
                .ToList());
    }

    public async ValueTask<FeatureFlagOpResult> CreateFlagAsync(FeatureFlagInput input)
    {
        var validation = ValidateFlagInput(input);
        if (validation is not null)
            return FeatureFlagOpResult.Fail(validation);

        await using var ctx = await contextFactory.CreateDbContextAsync();

        if (await ctx.FeatureFlags.AnyAsync(f => f.Id == input.FlagId))
            return FeatureFlagOpResult.Fail($"Flag '{input.FlagId}' already exists");

        var code = NormalizeUssd(input.UssdActivationCode);
        if (code is not null && await ctx.FeatureFlags.AnyAsync(f => f.UssdActivationCode == code))
            return FeatureFlagOpResult.Fail($"USSD code '{code}' is already in use");

        ctx.FeatureFlags.Add(new FeatureFlagEntity
        {
            Id                 = input.FlagId,
            Description        = input.Description,
            DefaultEnabled     = input.DefaultEnabled,
            RolloutPercentage  = input.RolloutPercentage,
            Variants           = input.Variants,
            UssdActivationCode = code,
            ExpiresAt          = input.ExpiresAt
        });

        await ctx.SaveChangesAsync();
        _cacheExpiry = DateTime.MinValue;

        logger.LogInformation("Created feature flag {FlagId}", input.FlagId);
        return FeatureFlagOpResult.Ok(input.FlagId);
    }

    public async ValueTask<FeatureFlagOpResult> UpdateFlagAsync(FeatureFlagInput input)
    {
        var validation = ValidateFlagInput(input);
        if (validation is not null)
            return FeatureFlagOpResult.Fail(validation);

        await using var ctx = await contextFactory.CreateDbContextAsync();

        var flag = await ctx.FeatureFlags.FirstOrDefaultAsync(f => f.Id == input.FlagId && !f.IsDeleted);
        if (flag is null)
            return FeatureFlagOpResult.Fail($"Flag '{input.FlagId}' not found");

        var code = NormalizeUssd(input.UssdActivationCode);
        if (code is not null && await ctx.FeatureFlags.AnyAsync(f => f.UssdActivationCode == code && f.Id != input.FlagId))
            return FeatureFlagOpResult.Fail($"USSD code '{code}' is already in use");

        flag.Description        = input.Description;
        flag.DefaultEnabled     = input.DefaultEnabled;
        flag.RolloutPercentage  = input.RolloutPercentage;
        flag.Variants           = input.Variants;
        flag.UssdActivationCode = code;
        flag.ExpiresAt          = input.ExpiresAt;

        await ctx.SaveChangesAsync();
        _cacheExpiry = DateTime.MinValue;

        logger.LogInformation("Updated feature flag {FlagId}", input.FlagId);
        return FeatureFlagOpResult.Ok(input.FlagId);
    }

    public async ValueTask<FeatureFlagOpResult> DeleteFlagAsync(string flagId)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        var flag = await ctx.FeatureFlags.FirstOrDefaultAsync(f => f.Id == flagId && !f.IsDeleted);
        if (flag is null)
            return FeatureFlagOpResult.Fail($"Flag '{flagId}' not found");

        flag.IsDeleted          = true;
        flag.DeletedAt          = DateTimeOffset.UtcNow;
        flag.UssdActivationCode = null; // release the USSD code so it can be reused

        await ctx.SaveChangesAsync();
        _cacheExpiry = DateTime.MinValue;

        logger.LogInformation("Deleted feature flag {FlagId}", flagId);
        return FeatureFlagOpResult.Ok(flagId);
    }

    public async ValueTask<FeatureFlagOpResult> SetOverrideAsync(FeatureFlagOverrideInput input)
    {
        if (string.IsNullOrWhiteSpace(input.TargetId))
            return FeatureFlagOpResult.Fail("Target id cannot be empty");
        if (input.RolloutPercentage is < 0 or > 100)
            return FeatureFlagOpResult.Fail("Rollout percentage must be between 0 and 100");

        await using var ctx = await contextFactory.CreateDbContextAsync();

        if (!await ctx.FeatureFlags.AnyAsync(f => f.Id == input.FlagId && !f.IsDeleted))
            return FeatureFlagOpResult.Fail($"Flag '{input.FlagId}' not found");

        var existing = await ctx.FeatureFlagOverrides.FirstOrDefaultAsync(o =>
            o.FeatureFlagId == input.FlagId && o.Scope == input.Scope && o.TargetId == input.TargetId);

        if (existing is null)
        {
            ctx.FeatureFlagOverrides.Add(new FeatureFlagOverrideEntity
            {
                Id                = Guid.CreateVersion7(),
                FeatureFlagId     = input.FlagId,
                Scope             = input.Scope,
                TargetId          = input.TargetId,
                Enabled           = input.Enabled,
                RolloutPercentage = input.RolloutPercentage,
                ForcedVariant     = input.ForcedVariant
            });
        }
        else
        {
            existing.Enabled           = input.Enabled;
            existing.RolloutPercentage = input.RolloutPercentage;
            existing.ForcedVariant     = input.ForcedVariant;
            existing.IsDeleted         = false;
            existing.DeletedAt         = null;
        }

        await ctx.SaveChangesAsync();
        _cacheExpiry = DateTime.MinValue;

        return FeatureFlagOpResult.Ok(input.FlagId);
    }

    public async ValueTask<FeatureFlagOpResult> DeleteOverrideAsync(Guid overrideId)
    {
        await using var ctx = await contextFactory.CreateDbContextAsync();

        var existing = await ctx.FeatureFlagOverrides.FirstOrDefaultAsync(o => o.Id == overrideId && !o.IsDeleted);
        if (existing is null)
            return FeatureFlagOpResult.Fail("Override not found");

        existing.IsDeleted = true;
        existing.DeletedAt = DateTimeOffset.UtcNow;

        await ctx.SaveChangesAsync();
        _cacheExpiry = DateTime.MinValue;

        return FeatureFlagOpResult.Ok(existing.FeatureFlagId);
    }

    private static string? NormalizeUssd(string? code)
        => string.IsNullOrWhiteSpace(code) ? null : code.Trim();

    private static string? ValidateFlagInput(FeatureFlagInput input)
    {
        if (string.IsNullOrWhiteSpace(input.FlagId))
            return "Flag id cannot be empty";

        if (input.RolloutPercentage is < 0 or > 100)
            return "Rollout percentage must be between 0 and 100";

        if (!string.IsNullOrWhiteSpace(input.Variants))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(input.Variants);
                if (parsed is null || parsed.Count == 0)
                    return "Variants JSON must be a non-empty object of {name: weight}";
            }
            catch (JsonException)
            {
                return "Variants must be valid JSON of shape {name: weight}";
            }
        }

        return null;
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
