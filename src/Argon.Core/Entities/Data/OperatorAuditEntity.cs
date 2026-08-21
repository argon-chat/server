namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record OperatorAuditEntity : ArgonEntity, IEntityTypeConfiguration<OperatorAuditEntity>
{
    public required Guid   OperatorId    { get; set; }
    public required string OperatorEmail { get; set; }
    public required string Action        { get; set; }
    public          string? TargetType   { get; set; }
    public          string? TargetId     { get; set; }
    public          string? Details      { get; set; }

    public void Configure(EntityTypeBuilder<OperatorAuditEntity> builder)
    {
        builder.HasIndex(x => x.OperatorId);
        builder.HasIndex(x => x.Action);
        builder.HasIndex(x => x.TargetId);
        builder.HasIndex(x => x.CreatedAt);
        builder.Property(x => x.OperatorEmail).HasMaxLength(256);
        builder.Property(x => x.Action).HasMaxLength(128);
        builder.Property(x => x.TargetType).HasMaxLength(128);
        builder.Property(x => x.TargetId).HasMaxLength(256);
        builder.Property(x => x.Details).HasMaxLength(4096);
    }
}
