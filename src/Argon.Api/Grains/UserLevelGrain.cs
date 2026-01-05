namespace Argon.Grains;

using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using Microsoft.EntityFrameworkCore;
using Orleans.Providers;
using Persistence.States;

/// <summary>
/// Manages user level and XP progression.
/// 
/// XP/Level Design (balanced for ~1 medal per year with active use):
/// - XP per voice minute: 2 XP
/// - Level formula: XP required = sum(50 * i^1.3) for levels 1 to i
/// - Total XP for 100 levels: ~75,000 XP
/// - Active user (500 hours/year voice): 60,000 XP from voice alone
/// - With bonuses and consistent use: achievable in 1 year
/// 
/// Medal System:
/// - Reaching level 100 unlocks ability to claim coin
/// - Coin template: year_{YYYY}_coin_lvl{N} where N = 1-5
/// - Claiming coin resets level to 1, keeps total XP history
/// - Max 5 tiers per year, after tier 5 no more coins for that year
/// </summary>
public class UserLevelGrain(
    [PersistentState("user-level-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<UserLevelGrainState> state,
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    ILogger<UserLevelGrain> logger) : Grain, IUserLevelGrain
{
    private IDisposable? _persistTimer;
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Maximum level before medal can be claimed.
    /// </summary>
    private const int MaxLevel = 100;

    /// <summary>
    /// Multiplier for expanding XP
    ///
    /// 8.42 ~= 144069 XP
    /// </summary>
    private const double Multiplier = 8.42;

    /// <summary>
    /// Maximum coin tier per year.
    /// </summary>
    private const int MaxCoinTier = 5;

    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await state.ReadStateAsync(cancellationToken);

        // Load from database if not initialized
        if (!state.State.IsInitialized)
        {
            await LoadFromDatabaseAsync();
        }

        // Set up periodic persist timer
        _persistTimer = this.RegisterGrainTimer(
            static async (grain, _) => await grain.PersistToDatabaseAsync(),
            this,
            PersistInterval,
            PersistInterval);
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _persistTimer?.Dispose();

        if (state.State.IsDirty)
        {
            await PersistToDatabaseAsync();
        }
    }

    public async ValueTask AwardXpAsync(int amount, XpSource source)
    {
        if (amount <= 0 || state.State.CanClaimMedal)
            return;

        state.State.TotalXpAllTime += amount;
        state.State.CurrentCycleXp += amount;
        state.State.LastXpAward = DateTimeOffset.UtcNow;
        state.State.IsDirty = true;

        // Check for level up
        var newLevel = CalculateLevelFromXp(state.State.CurrentCycleXp);
        if (newLevel > state.State.CurrentLevel)
        {
            var oldLevel = state.State.CurrentLevel;
            state.State.CurrentLevel = Math.Min(newLevel, MaxLevel);

            logger.LogInformation(
                "User {UserId} leveled up from {OldLevel} to {NewLevel}",
                this.GetPrimaryKey(), oldLevel, state.State.CurrentLevel);

            if (state.State.CurrentLevel >= MaxLevel)
            {
                state.State.CanClaimMedal = true;
                logger.LogInformation("User {UserId} reached level 100 and can claim coin", this.GetPrimaryKey());
            }
        }

        await state.WriteStateAsync();
    }

    public ValueTask<MyLevelDetails> GetLevelDetailsAsync()
    {
        var currentLevel = state.State.CurrentLevel;
        var currentXp = state.State.CurrentCycleXp;
        var xpForCurrentLevel = GetXpRequiredForLevel(currentLevel);
        var xpForNextLevel = currentLevel >= MaxLevel ? xpForCurrentLevel : GetXpRequiredForLevel(currentLevel + 1);

        return ValueTask.FromResult(new MyLevelDetails(
            totalXp: currentXp,
            currentLevel: currentLevel,
            xpForNextLevel: xpForNextLevel,
            xpForCurrentLevel: xpForCurrentLevel,
            readyToClaimCoin: state.State.CanClaimMedal));
    }

    public async ValueTask<bool> ClaimMedalAsync()
    {
        if (!state.State.CanClaimMedal)
            return false;

        var userId = this.GetPrimaryKey();
        var currentYear = DateTime.UtcNow.Year;

        try
        {
            // Determine which tier coin to give
            var nextTier = await GetNextCoinTierAsync(userId, currentYear);

            if (nextTier > MaxCoinTier)
            {
                logger.LogInformation(
                    "User {UserId} already has max tier coin for {Year}, cannot claim more",
                    userId, currentYear);
                
                // Still reset level but don't give coin
                await ResetLevelProgressAsync();
                return false;
            }

            // Build coin template id: year_2026_coin_lvl1
            var coinTemplateId = $"year_{currentYear}_coin_lvl{nextTier}";

            // Give coin via inventory system
            var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.Empty);
            var success = await inventoryGrain.GiveCoinFor(userId, coinTemplateId);

            if (!success)
            {
                logger.LogError(
                    "Failed to give coin {TemplateId} to user {UserId}",
                    coinTemplateId, userId);
                return false;
            }

            logger.LogInformation(
                "User {UserId} claimed {Year} coin tier {Tier}",
                userId, currentYear, nextTier);

            // Reset level progress
            await ResetLevelProgressAsync();

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to claim coin for user {UserId}", userId);
            return false;
        }
    }

    /// <summary>
    /// Determines the next coin tier for a user for a given year.
    /// Checks existing inventory items with pattern year_{year}_coin_lvl{N}.
    /// </summary>
    private async Task<int> GetNextCoinTierAsync(Guid userId, int year)
    {
        var inventoryGrain = grainFactory.GetGrain<IInventoryGrain>(Guid.Empty);
        var items = await inventoryGrain.GetItemsForUserAsync(userId);

        var coinPrefix = $"year_{year}_coin_lvl";
        var existingTiers = items
            .Where(x => x.id.StartsWith(coinPrefix))
            .Select(x =>
            {
                var tierStr = x.id[coinPrefix.Length..];
                return int.TryParse(tierStr, out var tier) ? tier : 0;
            })
            .Where(x => x > 0)
            .ToList();

        if (existingTiers.Count == 0)
            return 1;

        return existingTiers.Max() + 1;
    }

    private async Task ResetLevelProgressAsync()
    {
        state.State.CurrentLevel = 1;
        state.State.CurrentCycleXp = 0;
        state.State.CanClaimMedal = false;
        state.State.IsDirty = true;

        await state.WriteStateAsync();
        await PersistToDatabaseAsync();
    }

    /// <summary>
    /// Calculates level from total XP using the progression formula.
    /// XP for level n = sum(Multiplier * i^1.3) for i from 1 to n.
    /// </summary>
    private static int CalculateLevelFromXp(int xp)
    {
        var level = 1;
        var totalXpRequired = 0;

        while (level <= MaxLevel)
        {
            var xpForNextLevel = (int)(Multiplier * Math.Pow(level, 1.3));
            if (totalXpRequired + xpForNextLevel > xp)
                break;

            totalXpRequired += xpForNextLevel;
            level++;
        }

        return Math.Min(level, MaxLevel);
    }

    /// <summary>
    /// Gets total XP required to reach a specific level.
    /// </summary>
    private static int GetXpRequiredForLevel(int level)
    {
        if (level <= 1)
            return 0;

        var totalXp = 0;
        for (var i = 1; i < level; i++)
        {
            totalXp += (int)(Multiplier * Math.Pow(i, 1.3));
        }

        return totalXp;
    }

    private async Task LoadFromDatabaseAsync()
    {
        try
        {
            await using var ctx = await context.CreateDbContextAsync();

            var userId = this.GetPrimaryKey();
            var levelData = await ctx.UserLevels.FirstOrDefaultAsync(x => x.UserId == userId);

            if (levelData is not null)
            {
                state.State.TotalXpAllTime = levelData.TotalXpAllTime;
                state.State.CurrentCycleXp = levelData.CurrentCycleXp;
                state.State.CurrentLevel = levelData.CurrentLevel;
                state.State.CanClaimMedal = levelData.CanClaimMedal;
                state.State.LastXpAward = levelData.LastXpAward;
            }
            else
            {
                // Create new record
                await ctx.UserLevels.AddAsync(new UserLevelEntity
                {
                    UserId = userId,
                    TotalXpAllTime = 0,
                    CurrentCycleXp = 0,
                    CurrentLevel = 1,
                    CanClaimMedal = false,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                await ctx.SaveChangesAsync();
            }

            state.State.IsInitialized = true;
            await state.WriteStateAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load level data from database for user {UserId}", this.GetPrimaryKey());
            state.State.IsInitialized = true; // Mark as initialized to avoid retry loops
        }
    }

    private async Task PersistToDatabaseAsync()
    {
        if (!state.State.IsDirty)
            return;

        try
        {
            await using var ctx = await context.CreateDbContextAsync();

            var userId = this.GetPrimaryKey();
            var levelData = await ctx.UserLevels.FirstOrDefaultAsync(x => x.UserId == userId);

            if (levelData is not null)
            {
                levelData.TotalXpAllTime = state.State.TotalXpAllTime;
                levelData.CurrentCycleXp = state.State.CurrentCycleXp;
                levelData.CurrentLevel = state.State.CurrentLevel;
                levelData.CanClaimMedal = state.State.CanClaimMedal;
                levelData.LastXpAward = state.State.LastXpAward;
                levelData.UpdatedAt = DateTimeOffset.UtcNow;

                ctx.UserLevels.Update(levelData);
            }
            else
            {
                await ctx.UserLevels.AddAsync(new UserLevelEntity
                {
                    UserId = userId,
                    TotalXpAllTime = state.State.TotalXpAllTime,
                    CurrentCycleXp = state.State.CurrentCycleXp,
                    CurrentLevel = state.State.CurrentLevel,
                    CanClaimMedal = state.State.CanClaimMedal,
                    LastXpAward = state.State.LastXpAward,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }

            await ctx.SaveChangesAsync();

            state.State.IsDirty = false;
            state.State.LastPersist = DateTimeOffset.UtcNow;

            await state.WriteStateAsync();

            logger.LogDebug("Persisted level data for user {UserId}", userId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist level data for user {UserId}", this.GetPrimaryKey());
        }
    }
}
