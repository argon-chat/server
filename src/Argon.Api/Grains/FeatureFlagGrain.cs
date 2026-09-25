namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Singleton grain for feature flag evaluation.
/// Evaluates by context over one snapshot of all flags and overrides, shared through the cache.
/// </summary>
/// <remarks>
/// The snapshot lives in <see cref="HybridCache"/> rather than in each activation, so a write drops it
/// for every activation on this silo at once and for other silos when their local copy lapses.
/// </remarks>
[StatelessWorker]
public sealed class FeatureFlagGrain(
    IDbContextFactory<ApplicationDbContext> contextFactory,
    HybridCache cache,
    ILogger<FeatureFlagGrain> logger) : Grain, IFeatureFlagGrain
{
    private const string SnapshotKey = "feature-flags:snapshot";

    private static readonly HybridCacheEntryOptions SnapshotOptions = new()
    {
        Expiration           = TimeSpan.FromMinutes(2),
        LocalCacheExpiration = TimeSpan.FromSeconds(15)
    };

    // The lookups are built once per snapshot instance, not per evaluation.
    private FeatureFlagSnapshot? _snapshot;
    private FrozenDictionary<string, FeatureFlagRow> _flags = FrozenDictionary<string, FeatureFlagRow>.Empty;
    private FrozenDictionary<string, FeatureFlagOverrideRow[]> _overridesByFlag = FrozenDictionary<string, FeatureFlagOverrideRow[]>.Empty;

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
        => cache.RemoveAsync(SnapshotKey);

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

        // Deleted overrides keep their row and its unique key, so they are looked up too and revived.
        var targetId = userId.ToString();
        var existing = await ctx.FeatureFlagOverrides.IgnoreQueryFilters().FirstOrDefaultAsync(o =>
            o.FeatureFlagId == flagId && o.Scope == FeatureFlagScope.User && o.TargetId == targetId);

        if (existing is null)
        {
            ctx.FeatureFlagOverrides.Add(new FeatureFlagOverrideEntity
            {
                Id            = ArgonId.New(),
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

        // Drop the shared snapshot so the returned evaluation reflects the new override.
        await cache.RemoveAsync(SnapshotKey);
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

        var flag = await ctx.FeatureFlags.FirstOrDefaultAsync(f => f.Id == input.FlagId);
        if (flag is { IsDeleted: false })
            return FeatureFlagOpResult.Fail($"Flag '{input.FlagId}' already exists");

        var code = NormalizeUssd(input.UssdActivationCode);
        if (code is not null && await ctx.FeatureFlags.AnyAsync(f => f.UssdActivationCode == code))
            return FeatureFlagOpResult.Fail($"USSD code '{code}' is already in use");

        // A deleted flag keeps its row, so its id is taken back rather than inserted a second time.
        if (flag is null)
        {
            flag = new FeatureFlagEntity { Id = input.FlagId };
            ctx.FeatureFlags.Add(flag);
        }
        else
        {
            flag.IsDeleted = false;
            flag.DeletedAt = null;
            flag.CreatedAt = DateTimeOffset.UtcNow;
        }

        flag.Description        = input.Description;
        flag.DefaultEnabled     = input.DefaultEnabled;
        flag.RolloutPercentage  = input.RolloutPercentage;
        flag.Variants           = input.Variants;
        flag.UssdActivationCode = code;
        flag.ExpiresAt          = input.ExpiresAt;

        await ctx.SaveChangesAsync();
        await cache.RemoveAsync(SnapshotKey);

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
        await cache.RemoveAsync(SnapshotKey);

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

        // Its overrides go with it, so a flag created again under this id starts without them.
        await ctx.FeatureFlagOverrides
            .Where(o => o.FeatureFlagId == flagId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.IsDeleted, true)
                .SetProperty(o => o.DeletedAt, flag.DeletedAt));

        await cache.RemoveAsync(SnapshotKey);

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

        var existing = await ctx.FeatureFlagOverrides.IgnoreQueryFilters().FirstOrDefaultAsync(o =>
            o.FeatureFlagId == input.FlagId && o.Scope == input.Scope && o.TargetId == input.TargetId);

        if (existing is null)
        {
            ctx.FeatureFlagOverrides.Add(new FeatureFlagOverrideEntity
            {
                Id                = ArgonId.New(),
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
        await cache.RemoveAsync(SnapshotKey);

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
        await cache.RemoveAsync(SnapshotKey);

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
        FeatureFlagRow flag,
        FeatureFlagOverrideRow[] overrides,
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
        FeatureFlagRow flag,
        FeatureFlagOverrideRow? userOverride,
        FeatureFlagOverrideRow? countryOverride,
        FeatureFlagOverrideRow? clientOverride,
        FeatureFlagEvaluationContext context)
    {
        // User-level override (highest priority)
        if (Decide(flag, userOverride, context) is { } byUser)
            return (byUser, FeatureFlagScope.User);

        // Country-level override
        if (Decide(flag, countryOverride, context) is { } byCountry)
            return (byCountry, FeatureFlagScope.Country);

        // Client-level override
        if (Decide(flag, clientOverride, context) is { } byClient)
            return (byClient, FeatureFlagScope.Client);

        // Global default with optional percentage rollout
        return (EvaluateGlobalDefault(flag, context), FeatureFlagScope.Global);
    }

    /// <summary>
    /// An override's switch when it sets one, else its percentage, else null to inherit from the scope below.
    /// </summary>
    private static bool? Decide(FeatureFlagRow flag, FeatureFlagOverrideRow? @override, FeatureFlagEvaluationContext context)
        => @override?.Enabled
        ?? (@override?.RolloutPercentage is { } percentage ? InRollout(flag, percentage, context) : null);

    private static bool EvaluateGlobalDefault(FeatureFlagRow flag, FeatureFlagEvaluationContext context)
        => flag.RolloutPercentage is { } percentage ? InRollout(flag, percentage, context) : flag.DefaultEnabled;

    /// <summary>
    /// The one bucketing every percentage uses, so a user in a 30% override is in a 30% rollout of the same flag too.
    /// </summary>
    private static bool InRollout(FeatureFlagRow flag, int percentage, FeatureFlagEvaluationContext context)
    {
        // Use user ID for consistent rollout, or flag ID for anonymous
        var hashInput = context.UserId.HasValue
            ? $"{flag.Id}:{context.UserId.Value}"
            : flag.Id;

        return GetStableHash(hashInput) % 100 < percentage;
    }

    private string? ResolveVariant(
        FeatureFlagRow flag,
        FeatureFlagEvaluationContext context,
        FeatureFlagOverrideRow? userOverride,
        FeatureFlagOverrideRow? countryOverride,
        FeatureFlagOverrideRow? clientOverride)
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

    private string? AssignVariant(FeatureFlagRow flag, Guid? userId)
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

    // Widened before Math.Abs: int.MinValue has no positive int and threw for one input in 2^32. Every
    // other input keeps the bucket it had.
    private static long GetStableHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Math.Abs((long)BitConverter.ToInt32(bytes, 0));
    }

    private async Task EnsureCacheLoadedAsync()
    {
        var snapshot = await cache.GetOrCreateAsync(SnapshotKey, contextFactory,
            static async (factory, ct) =>
            {
                await using var ctx = await factory.CreateDbContextAsync(ct);

                var flags = await ctx.FeatureFlags
                    .AsNoTracking()
                    .Where(f => !f.IsDeleted)
                    .Select(f => new FeatureFlagRow(f.Id, f.DefaultEnabled, f.RolloutPercentage, f.Variants, f.ExpiresAt))
                    .ToArrayAsync(ct);

                var overrides = await ctx.FeatureFlagOverrides
                    .AsNoTracking()
                    .Where(o => !o.IsDeleted)
                    .Select(o => new FeatureFlagOverrideRow(o.FeatureFlagId, o.Scope, o.TargetId, o.Enabled, o.ForcedVariant, o.RolloutPercentage))
                    .ToArrayAsync(ct);

                return new FeatureFlagSnapshot(flags, overrides);
            },
            SnapshotOptions);

        if (ReferenceEquals(snapshot, _snapshot))
            return;

        _flags = snapshot.Flags.ToFrozenDictionary(f => f.Id);
        _overridesByFlag = snapshot.Overrides
            .GroupBy(o => o.FeatureFlagId)
            .ToFrozenDictionary(
                g => g.Key,
                g => g.OrderByDescending(o => o.Scope).ToArray());

        _snapshot = snapshot;

        logger.LogDebug("Feature flags cache loaded: {FlagCount} flags, {OverrideCount} overrides",
            _flags.Count, snapshot.Overrides.Length);
    }
}

// Immutable so an L1 hit returns the same instance, which is what lets an activation skip rebuilding
// its lookups until the snapshot actually changes.

[ImmutableObject(true)]
public sealed record FeatureFlagSnapshot(FeatureFlagRow[] Flags, FeatureFlagOverrideRow[] Overrides);

[ImmutableObject(true)]
public sealed record FeatureFlagRow(
    string Id,
    bool DefaultEnabled,
    int? RolloutPercentage,
    string? Variants,
    DateTimeOffset? ExpiresAt);

[ImmutableObject(true)]
public sealed record FeatureFlagOverrideRow(
    string FeatureFlagId,
    FeatureFlagScope Scope,
    string TargetId,
    bool? Enabled,
    string? ForcedVariant,
    int? RolloutPercentage = null);
