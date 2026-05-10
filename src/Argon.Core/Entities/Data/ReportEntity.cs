namespace Argon.Entities;

using ArgonContracts;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ReportEntity : ArgonEntity, IEntityTypeConfiguration<ReportEntity>
{
    public required Guid             ReporterId              { get; set; }
    public virtual  UserEntity       Reporter                { get; set; } = null!;
    public          ReportTargetKind TargetKind              { get; set; }
    public          Guid             TargetId                { get; set; }
    public          Guid?            ChannelId               { get; set; }
    public          ulong?           MessageId               { get; set; }
    public          ReportCategory   Category                { get; set; }
    public          ReportReason     Reason                  { get; set; }
    public          string?          AdditionalInfo          { get; set; }
    public          ReportStatus     Status                  { get; set; }
    public          Guid?            ReferenceReportId       { get; set; }
    public          Guid?            AssignedOperatorId      { get; set; }
    public          string?          ResolutionNote          { get; set; }
    public          DateTimeOffset?  ResolvedAt              { get; set; }

    // Reporter context snapshot at filing time
    public          int              ReporterCredibilityAtTime { get; set; }
    public          string?          ReporterIpHash          { get; set; }
    public          int              ReporterAccountAgeDays  { get; set; }

    // Priority & escalation
    public          int              PriorityScore           { get; set; }
    public          bool             IsAutoEscalated         { get; set; }
    public          string?          EscalationRule          { get; set; }

    public void Configure(EntityTypeBuilder<ReportEntity> builder)
    {
        builder.HasIndex(x => x.ReporterId);
        builder.HasIndex(x => x.TargetId);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.Category);
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => x.PriorityScore).HasDatabaseName("idx_reports_priority");
        builder.HasIndex(x => new { x.ReporterId, x.TargetId, x.Category })
           .HasDatabaseName("idx_reports_dedup");
        builder.HasIndex(x => new { x.ReporterId, x.TargetId })
           .HasDatabaseName("idx_reports_per_target");

        builder.HasOne(x => x.Reporter)
           .WithMany()
           .HasForeignKey(x => x.ReporterId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.AdditionalInfo).HasMaxLength(2000);
        builder.Property(x => x.ResolutionNote).HasMaxLength(2000);
        builder.Property(x => x.ReporterIpHash).HasMaxLength(64);
        builder.Property(x => x.EscalationRule).HasMaxLength(64);
    }
}
