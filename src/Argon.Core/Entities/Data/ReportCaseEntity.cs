namespace Argon.Entities;

using ArgonContracts;
using ConsoleContracts;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One reported thing, however many people reported it.
/// </summary>
/// <remarks>
/// <para>The unit a moderator works. Fifty reports about one message used to be fifty rows in the
/// queue, each resolved on its own and each feeding trust separately; they are now one case with
/// a count, a priority, the content as it was, and one decision that lands on every report.</para>
///
/// <para>At most one <em>open</em> case per <see cref="GroupKey"/>, held by a partial unique index
/// on <see cref="IsOpen"/>. A resolved case stays where it is; a new report about the same thing
/// opens a new case beside it, so history is never rewritten by a later complaint.</para>
/// </remarks>
public record ReportCaseEntity : ArgonEntity, IEntityTypeConfiguration<ReportCaseEntity>
{
    /// <summary>See <c>ReportTargetRules.GroupKey</c>.</summary>
    public required string GroupKey { get; set; }

    public ReportTargetKind TargetKind     { get; set; }
    public Guid             TargetId       { get; set; }
    public Guid?            SpaceId        { get; set; }
    public Guid?            ChannelId      { get; set; }
    public long?            MessageId      { get; set; }
    public Guid?            ConversationId { get; set; }

    /// <summary>Redundant with <see cref="Status"/>; exists so the unique index can filter on it.</summary>
    public bool IsOpen { get; set; }

    public ReportStatus   Status                   { get; set; }
    public ReportCategory TopCategory              { get; set; }
    public int            PriorityScore            { get; set; }
    public int            ReportCount              { get; set; }
    public int            IndependentReporterCount { get; set; }
    public bool           IsEscalated              { get; set; }
    public string?        EscalationRule           { get; set; }

    /// <summary>JSON of a <c>ReportContentSnapshot</c>, taken by the first report.</summary>
    public string? ContentSnapshot { get; set; }

    public Guid?            AssignedOperatorId   { get; set; }
    public Guid?            ResolvedByOperatorId { get; set; }
    public DateTimeOffset?  ResolvedAt           { get; set; }
    public string?          ResolutionNote       { get; set; }
    public ReportActionKind AppliedAction        { get; set; }

    public DateTimeOffset FirstReportedAt { get; set; }
    public DateTimeOffset LastReportedAt  { get; set; }

    public virtual ICollection<ReportEntity> Reports { get; set; } = new List<ReportEntity>();

    public void Configure(EntityTypeBuilder<ReportCaseEntity> builder)
    {
        builder.ToTable("ReportCases");

        builder.Property(x => x.GroupKey).HasMaxLength(128);
        builder.Property(x => x.EscalationRule).HasMaxLength(64);
        builder.Property(x => x.ResolutionNote).HasMaxLength(2000);

        // Both engines accept a partial unique index; the filter is what makes "one open case per
        // thing" a database fact rather than a race two reporters can win at once.
        builder.HasIndex(x => x.GroupKey)
           .IsUnique()
           .HasFilter("\"IsOpen\" = true")
           .HasDatabaseName("idx_report_cases_open_group");

        builder.HasIndex(x => new { x.IsOpen, x.PriorityScore }).HasDatabaseName("idx_report_cases_queue");
        builder.HasIndex(x => x.Status);
        builder.HasIndex(x => x.TargetId);
        builder.HasIndex(x => x.LastReportedAt);
    }
}
