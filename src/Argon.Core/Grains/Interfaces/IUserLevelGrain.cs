namespace Argon.Grains.Interfaces;

/// <summary>
/// Grain for managing user level and XP progression.
/// Keyed by UserId.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IUserLevelGrain)}")]
public interface IUserLevelGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Awards XP to the user. Handles level-up logic automatically.
    /// </summary>
    /// <param name="amount">Amount of XP to award.</param>
    /// <param name="source">Source of XP (voice, message, bonus, etc.).</param>
    [Alias(nameof(AwardXpAsync))]
    ValueTask AwardXpAsync(int amount, XpSource source);

    /// <summary>
    /// Gets current level details for the user.
    /// </summary>
    [Alias(nameof(GetLevelDetailsAsync))]
    ValueTask<MyLevelDetails> GetLevelDetailsAsync();

    /// <summary>
    /// Claims the coin for reaching level 100.
    /// Creates inventory item with template: year_{YYYY}_coin_lvl{N}
    /// Resets level to 1. Max 5 tiers per year.
    /// </summary>
    /// <returns>True if coin was claimed, false if not eligible or max tier reached.</returns>
    [Alias(nameof(ClaimMedalAsync))]
    ValueTask<bool> ClaimMedalAsync();
}

/// <summary>
/// Source of XP for tracking purposes.
/// </summary>
public enum XpSource
{
    Voice = 0,
    Message = 1,
    DailyBonus = 2,
    Achievement = 3,
    Event = 4
}
