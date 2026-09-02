namespace Argon.Entities;

using ArgonContracts;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One person's report, attached to the case for the thing they reported.
/// </summary>
/// <remarks>
/// <para>Status, assignment and resolution are copied down from the case whenever the case moves,
/// so a report can be read on its own and still say what became of it. The case is the thing that
/// is decided; the report is who said what, and the standing they had when they said it.</para>
///
/// <para><see cref="ReporterIpHash"/> and <see cref="ReporterDeviceHash"/> are HMACs under the
/// deployment's key (see <c>ReporterIdentityHasher</c>), or null when it keeps none. They exist to
/// tell one reporter from five accounts on one machine, and for nothing else.</para>
///
/// <para><see cref="CaseId"/> is nullable only for rows written before cases existed.</para>
/// </remarks>
public record ReportEntity : ArgonEntity, IEntityTypeConfiguration<ReportEntity>
{
    public required Guid       ReporterId { get; set; }
    public virtual  UserEntity Reporter   { get; set; } = null!;

    public         Guid?            CaseId { get; set; }
    public virtual ReportCaseEntity? Case  { get; set; }

    public ReportTargetKind TargetKind     { get; set; }
    public Guid             TargetId       { get; set; }
    public Guid?            ChannelId      { get; set; }
    public ulong?           MessageId      { get; set; }
    public Guid?            ConversationId { get; set; }

    public ReportCategory Category       { get; set; }
    public ReportReason   Reason         { get; set; }
    public string?        AdditionalInfo { get; set; }
    public ReportStatus   Status         { get; set; }

    /// <summary>Unused since cases; kept because the column exists and the wire field still does.</summary>
    public Guid? ReferenceReportId { get; set; }

    public Guid?           AssignedOperatorId   { get; set; }
    public Guid?           ResolvedByOperatorId { get; set; }
    public string?         ResolutionNote       { get; set; }
    public DateTimeOffset? ResolvedAt           { get; set; }

    // Reporter standing at filing time — what the escalation rule judged them on.
    public int     ReporterCredibilityAtTime { get; set; }
    public string? ReporterIpHash            { get; set; }
    public string? ReporterDeviceHash        { get; set; }
    public int     ReporterAccountAgeDays    { get; set; }

    /// <summary>Whether this report added a reporter the case counts as a distinct person.</summary>
    public bool IsIndependent { get; set; }

    // Priority & escalation, as decided when this report arrived.
    public int     PriorityScore   { get; set; }
    public bool    IsAutoEscalated { get; set; }
    public string? EscalationRule  { get; set; }

    public void Configure(EntityTypeBuilder<ReportEntity> builder)
    {
        builder.HasIndex(x => x.ReporterId);
        builder.HasIndex(x => x.TargetId);
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.Category);
        builder.HasIndex(x => x.CreatedAt);
        builder.HasIndex(x => x.CaseId);
        builder.HasIndex(x => x.PriorityScore).HasDatabaseName("idx_reports_priority");
        builder.HasIndex(x => new { x.ReporterId, x.TargetId, x.Category })
           .HasDatabaseName("idx_reports_dedup");
        builder.HasIndex(x => new { x.ReporterId, x.TargetId })
           .HasDatabaseName("idx_reports_per_target");

        builder.HasOne(x => x.Reporter)
           .WithMany()
           .HasForeignKey(x => x.ReporterId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Case)
           .WithMany(x => x.Reports)
           .HasForeignKey(x => x.CaseId)
           .OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.AdditionalInfo).HasMaxLength(2000);
        builder.Property(x => x.ResolutionNote).HasMaxLength(2000);
        builder.Property(x => x.ReporterIpHash).HasMaxLength(64);
        builder.Property(x => x.ReporterDeviceHash).HasMaxLength(64);
        builder.Property(x => x.EscalationRule).HasMaxLength(64);
    }
}
