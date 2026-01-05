namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Stores daily aggregated statistics for a user.
/// Partitioned by date for efficient queries and cleanup of old data.
/// </summary>
public record UserDailyStatsEntity : IEntityTypeConfiguration<UserDailyStatsEntity>
{
    public Guid     UserId       { get; set; }
    public DateOnly Date         { get; set; }
    
    /// <summary>
    /// Total time spent in voice channels in seconds.
    /// </summary>
    public int TimeInVoiceSeconds { get; set; }
    
    /// <summary>
    /// Number of voice calls initiated or joined.
    /// </summary>
    public int CallsMade { get; set; }
    
    /// <summary>
    /// Number of messages sent.
    /// </summary>
    public int MessagesSent { get; set; }
    
    /// <summary>
    /// XP earned today (for audit purposes).
    /// </summary>
    public int XpEarned { get; set; }

    public void Configure(EntityTypeBuilder<UserDailyStatsEntity> builder)
    {
        builder.HasKey(x => new { x.UserId, x.Date });
        
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.Date);
        
        // Composite index for efficient user+date range queries
        builder.HasIndex(x => new { x.UserId, x.Date })
            .IsDescending(false, true);
    }
}
