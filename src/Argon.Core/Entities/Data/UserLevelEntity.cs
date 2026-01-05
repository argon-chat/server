namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Stores persistent level and XP data for a user.
/// Medal/coin tracking is done via inventory items with template: year_{YYYY}_coin_lvl{N}
/// </summary>
public record UserLevelEntity : IEntityTypeConfiguration<UserLevelEntity>
{
    public Guid UserId { get; set; }
    
    /// <summary>
    /// Total accumulated XP across all time (never resets).
    /// </summary>
    public long TotalXpAllTime { get; set; }
    
    /// <summary>
    /// Current XP in the current level cycle (resets on coin claim).
    /// </summary>
    public int CurrentCycleXp { get; set; }
    
    /// <summary>
    /// Current level (1-100, resets on coin claim).
    /// </summary>
    public int CurrentLevel { get; set; } = 1;
    
    /// <summary>
    /// Last time XP was awarded (for rate limiting).
    /// </summary>
    public DateTimeOffset LastXpAward { get; set; }
    
    /// <summary>
    /// Flag indicating user has reached level 100 and can claim a coin.
    /// </summary>
    public bool CanClaimMedal { get; set; }
    
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public void Configure(EntityTypeBuilder<UserLevelEntity> builder)
    {
        builder.HasKey(x => x.UserId);
        
        builder.HasIndex(x => x.CurrentLevel);
        builder.HasIndex(x => x.CanClaimMedal);
        builder.HasIndex(x => x.TotalXpAllTime);
    }
}
