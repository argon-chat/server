namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UserTrustScoreEntity : IEntityTypeConfiguration<UserTrustScoreEntity>
{
    public Guid           UserId                   { get; set; }
    public int            TrustScore               { get; set; } = 1000;
    public int            TotalReportsReceived     { get; set; }
    public int            ConfirmedReportsReceived { get; set; }
    public int            TotalReportsFiled        { get; set; }
    public int            FalseReportsFiled        { get; set; }
    public int            AutoActionsApplied       { get; set; }

    // Multi-dimensional score breakdown
    public int            ContentViolationScore    { get; set; }
    public int            SocialBehaviorScore      { get; set; }
    public int            CommercialAbuseScore     { get; set; }
    public int            PositiveSignalScore      { get; set; }

    // Reporter credibility (0-100)
    public int            ReporterCredibility      { get; set; } = 50;

    // Tracking
    public int            UniqueReporterCount      { get; set; }
    public int            BlockedByCount           { get; set; }
    public DateTimeOffset? LastConfirmedReportAt   { get; set; }
    public DateTimeOffset LastRecalculatedAt       { get; set; }
    public DateTimeOffset CreatedAt                { get; set; }
    public DateTimeOffset UpdatedAt                { get; set; }

    public void Configure(EntityTypeBuilder<UserTrustScoreEntity> builder)
    {
        builder.HasKey(x => x.UserId);
        builder.HasIndex(x => x.TrustScore);
        builder.HasIndex(x => x.ReporterCredibility);
    }
}
