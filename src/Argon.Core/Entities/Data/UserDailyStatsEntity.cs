namespace Argon.Core.Entities.Data;

using Argon.Features.EF;
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
        // The key already serves per-user reads in either date order.
        builder.HasKey(x => new { x.UserId, x.Date });

        builder.HasIndex(x => x.Date).IsHashSharded();
    }
}
