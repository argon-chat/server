namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Grains.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class FeatureFlagTests : TestBase
{
    private async Task<FeatureFlagEntity> CreateFeatureFlagAsync(
        string flagId,
        bool defaultEnabled,
        int? rolloutPercentage = null,
        string? variants = null,
        CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await ctx.CreateDbContextAsync(ct);

        var flag = new FeatureFlagEntity
        {
            Id                = flagId,
            DefaultEnabled    = defaultEnabled,
            RolloutPercentage = rolloutPercentage,
            Variants          = variants,
            CreatedAt         = DateTimeOffset.UtcNow,
            UpdatedAt         = DateTimeOffset.UtcNow
        };

        db.FeatureFlags.Add(flag);
        await db.SaveChangesAsync(ct);
        return flag;
    }

    private async Task CreateOverrideAsync(
        string flagId,
        FeatureFlagScope scope,
        string targetId,
        bool? enabled,
        string? forcedVariant = null,
        CancellationToken ct = default)
    {
        await using var serviceScope = FactoryAsp.Services.CreateAsyncScope();
        var ctx = serviceScope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await ctx.CreateDbContextAsync(ct);

        var @override = new FeatureFlagOverrideEntity
        {
            Id            = Guid.NewGuid(),
            FeatureFlagId = flagId,
            Scope         = scope,
            TargetId      = targetId,
            Enabled       = enabled,
            ForcedVariant = forcedVariant,
            CreatedAt     = DateTimeOffset.UtcNow,
            UpdatedAt     = DateTimeOffset.UtcNow
        };

        db.FeatureFlagOverrides.Add(@override);
        await db.SaveChangesAsync(ct);
    }

    private async Task CleanupFlagAsync(string flagId, CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await ctx.CreateDbContextAsync(ct);

        var overrides = await db.FeatureFlagOverrides.Where(o => o.FeatureFlagId == flagId).ToListAsync(ct);
        db.FeatureFlagOverrides.RemoveRange(overrides);

        var flag = await db.FeatureFlags.FindAsync([flagId], ct);
        if (flag is not null)
            db.FeatureFlags.Remove(flag);

        await db.SaveChangesAsync(ct);
    }

    private IFeatureFlagGrain GetFeatureFlagGrain()
        => FactoryAsp.Services.GetRequiredService<IGrainFactory>().GetGrain<IFeatureFlagGrain>(Guid.Empty);

    #region Basic Flag Evaluation

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_FlagNotFound_ReturnsDisabled(CancellationToken ct = default)
    {
        var grain = GetFeatureFlagGrain();

        var result = await grain.EvaluateAsync("non_existent_flag", FeatureFlagEvaluationContext.Empty);

        Assert.That(result.IsEnabled, Is.False);
        Assert.That(result.FlagId, Is.EqualTo("non_existent_flag"));
    }

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_FlagEnabled_ReturnsEnabled(CancellationToken ct = default)
    {
        const string flagId = "test_enabled_flag";
        try
        {
            await CreateFeatureFlagAsync(flagId, defaultEnabled: true, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var result = await grain.EvaluateAsync(flagId, FeatureFlagEvaluationContext.Empty);

            Assert.That(result.IsEnabled, Is.True);
            Assert.That(result.ResolvedAt, Is.EqualTo(FeatureFlagScope.Global));
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_FlagDisabled_ReturnsDisabled(CancellationToken ct = default)
    {
        const string flagId = "test_disabled_flag";
        try
        {
            await CreateFeatureFlagAsync(flagId, defaultEnabled: false, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var result = await grain.EvaluateAsync(flagId, FeatureFlagEvaluationContext.Empty);

            Assert.That(result.IsEnabled, Is.False);
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    #endregion

    #region Override Priority Tests

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_UserOverride_TakesPriorityOverGlobal(CancellationToken ct = default)
    {
        const string flagId = "test_user_override";
        var userId = Guid.NewGuid();

        try
        {
            await CreateFeatureFlagAsync(flagId, defaultEnabled: false, ct: ct);
            await CreateOverrideAsync(flagId, FeatureFlagScope.User, userId.ToString(), enabled: true, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var result = await grain.EvaluateAsync(flagId, FeatureFlagEvaluationContext.ForUser(userId));

            Assert.That(result.IsEnabled, Is.True);
            Assert.That(result.ResolvedAt, Is.EqualTo(FeatureFlagScope.User));
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_CountryOverride_TakesPriorityOverClient(CancellationToken ct = default)
    {
        const string flagId = "test_country_override";

        try
        {
            await CreateFeatureFlagAsync(flagId, defaultEnabled: false, ct: ct);
            await CreateOverrideAsync(flagId, FeatureFlagScope.Client, "ios_app", enabled: false, ct: ct);
            await CreateOverrideAsync(flagId, FeatureFlagScope.Country, "RU", enabled: true, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var context = new FeatureFlagEvaluationContext
            {
                CountryCode = "RU",
                ClientId    = "ios_app"
            };

            var result = await grain.EvaluateAsync(flagId, context);

            Assert.That(result.IsEnabled, Is.True);
            Assert.That(result.ResolvedAt, Is.EqualTo(FeatureFlagScope.Country));
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_UserOverride_TakesPriorityOverCountry(CancellationToken ct = default)
    {
        const string flagId = "test_user_over_country";
        var userId = Guid.NewGuid();

        try
        {
            await CreateFeatureFlagAsync(flagId, defaultEnabled: false, ct: ct);
            await CreateOverrideAsync(flagId, FeatureFlagScope.Country, "US", enabled: true, ct: ct);
            await CreateOverrideAsync(flagId, FeatureFlagScope.User, userId.ToString(), enabled: false, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var context = FeatureFlagEvaluationContext.ForUser(userId, "US", null);

            var result = await grain.EvaluateAsync(flagId, context);

            Assert.That(result.IsEnabled, Is.False);
            Assert.That(result.ResolvedAt, Is.EqualTo(FeatureFlagScope.User));
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_ClientOverride_TakesPriorityOverGlobal(CancellationToken ct = default)
    {
        const string flagId = "test_client_override";

        try
        {
            await CreateFeatureFlagAsync(flagId, defaultEnabled: false, ct: ct);
            await CreateOverrideAsync(flagId, FeatureFlagScope.Client, "android_app", enabled: true, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var context = new FeatureFlagEvaluationContext { ClientId = "android_app" };

            var result = await grain.EvaluateAsync(flagId, context);

            Assert.That(result.IsEnabled, Is.True);
            Assert.That(result.ResolvedAt, Is.EqualTo(FeatureFlagScope.Client));
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    #endregion

    #region A/B Testing Variants

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_WithVariants_AssignsVariantConsistently(CancellationToken ct = default)
    {
        const string flagId = "test_ab_variants";
        var userId = Guid.NewGuid();

        try
        {
            await CreateFeatureFlagAsync(
                flagId,
                defaultEnabled: true,
                variants: """{"control": 50, "variant_a": 30, "variant_b": 20}""",
                ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var context = FeatureFlagEvaluationContext.ForUser(userId);

            var result1 = await grain.EvaluateAsync(flagId, context);
            var result2 = await grain.EvaluateAsync(flagId, context);

            Assert.That(result1.IsEnabled, Is.True);
            Assert.That(result1.Variant, Is.Not.Null.And.Not.Empty);
            Assert.That(result1.Variant, Is.EqualTo(result2.Variant), "Same user should get same variant");
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_ForcedVariant_OverridesDistribution(CancellationToken ct = default)
    {
        const string flagId = "test_forced_variant";
        var userId = Guid.NewGuid();

        try
        {
            await CreateFeatureFlagAsync(
                flagId,
                defaultEnabled: true,
                variants: """{"control": 50, "variant_a": 50}""",
                ct: ct);
            await CreateOverrideAsync(
                flagId,
                FeatureFlagScope.User,
                userId.ToString(),
                enabled: true,
                forcedVariant: "variant_a",
                ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var context = FeatureFlagEvaluationContext.ForUser(userId);
            var result = await grain.EvaluateAsync(flagId, context);

            Assert.That(result.IsEnabled, Is.True);
            Assert.That(result.Variant, Is.EqualTo("variant_a"));
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    #endregion

    #region Bulk & All Evaluation Tests

    [Test, CancelAfter(1000 * 60)]
    public async Task EvaluateMany_MultipleFlags_ReturnsAllResults(CancellationToken ct = default)
    {
        const string flag1 = "test_bulk_1";
        const string flag2 = "test_bulk_2";
        const string flag3 = "test_bulk_3";

        try
        {
            await CreateFeatureFlagAsync(flag1, defaultEnabled: true, ct: ct);
            await CreateFeatureFlagAsync(flag2, defaultEnabled: false, ct: ct);
            await CreateFeatureFlagAsync(flag3, defaultEnabled: true, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var flagIds = new List<string> { flag1, flag2, flag3 };
            var results = await grain.EvaluateManyAsync(flagIds, FeatureFlagEvaluationContext.Empty);

            Assert.That(results, Has.Count.EqualTo(3));
            Assert.That(results[flag1].IsEnabled, Is.True);
            Assert.That(results[flag2].IsEnabled, Is.False);
            Assert.That(results[flag3].IsEnabled, Is.True);
        }
        finally
        {
            await CleanupFlagAsync(flag1, ct);
            await CleanupFlagAsync(flag2, ct);
            await CleanupFlagAsync(flag3, ct);
        }
    }

    [Test, CancelAfter(1000 * 60)]
    public async Task EvaluateAll_ReturnsAllEnabledFlags(CancellationToken ct = default)
    {
        const string flag1 = "test_all_1";
        const string flag2 = "test_all_2";
        const string flag3 = "test_all_3";

        try
        {
            await CreateFeatureFlagAsync(flag1, defaultEnabled: true, ct: ct);
            await CreateFeatureFlagAsync(flag2, defaultEnabled: false, ct: ct);
            await CreateFeatureFlagAsync(flag3, defaultEnabled: true, ct: ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var enabledOnly = await grain.EvaluateAllAsync(FeatureFlagEvaluationContext.Empty, includeDisabled: false);
            var allFlags = await grain.EvaluateAllAsync(FeatureFlagEvaluationContext.Empty, includeDisabled: true);

            Assert.That(enabledOnly.ContainsKey(flag1), Is.True);
            Assert.That(enabledOnly.ContainsKey(flag2), Is.False, "Disabled flag should not be in enabled-only result");
            Assert.That(enabledOnly.ContainsKey(flag3), Is.True);

            Assert.That(allFlags.ContainsKey(flag1), Is.True);
            Assert.That(allFlags.ContainsKey(flag2), Is.True, "Disabled flag should be in all flags result");
            Assert.That(allFlags.ContainsKey(flag3), Is.True);
        }
        finally
        {
            await CleanupFlagAsync(flag1, ct);
            await CleanupFlagAsync(flag2, ct);
            await CleanupFlagAsync(flag3, ct);
        }
    }

    #endregion

    #region Expiration Tests

    [Test, CancelAfter(1000 * 60)]
    public async Task Evaluate_ExpiredFlag_ReturnsDisabled(CancellationToken ct = default)
    {
        const string flagId = "test_expired_flag";

        try
        {
            await using var scope = FactoryAsp.Services.CreateAsyncScope();
            var ctx = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
            await using var db = await ctx.CreateDbContextAsync(ct);

            var flag = new FeatureFlagEntity
            {
                Id             = flagId,
                DefaultEnabled = true,
                ExpiresAt      = DateTimeOffset.UtcNow.AddDays(-1), // Expired yesterday
                CreatedAt      = DateTimeOffset.UtcNow,
                UpdatedAt      = DateTimeOffset.UtcNow
            };

            db.FeatureFlags.Add(flag);
            await db.SaveChangesAsync(ct);

            var grain = GetFeatureFlagGrain();
            await grain.InvalidateCacheAsync();

            var result = await grain.EvaluateAsync(flagId, FeatureFlagEvaluationContext.Empty);

            Assert.That(result.IsEnabled, Is.False, "Expired flag should be disabled");
        }
        finally
        {
            await CleanupFlagAsync(flagId, ct);
        }
    }

    #endregion
}
