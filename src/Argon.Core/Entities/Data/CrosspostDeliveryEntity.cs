namespace Argon.Core.Entities.Data;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One source message landed in one target channel. Claimed before the copy is written, so a
/// redelivery finds it and nothing a member sends can stand in for it.
/// </summary>
public record CrosspostDeliveryEntity : IEntityTypeConfiguration<CrosspostDeliveryEntity>
{
    /// <summary>Long past any retry of a delivery job.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public required Guid           TargetChannelId { get; set; }
    public required Guid           SourceChannelId { get; set; }
    public required long           SourceMessageId { get; set; }
    public          long?          MessageId       { get; set; }
    public          DateTimeOffset CreatedAt       { get; set; }
    public          DateTimeOffset ExpireAt        { get; set; }

    public void Configure(EntityTypeBuilder<CrosspostDeliveryEntity> builder)
    {
        builder.ToTable("CrosspostDeliveries");

        builder.HasKey(x => new
        {
            x.TargetChannelId,
            x.SourceChannelId,
            x.SourceMessageId
        });

        builder.Property(x => x.ExpireAt)
           .HasColumnType("timestamptz")
           .IsRequired();

        builder.WithTTL(x => x.ExpireAt, CronValue.Daily);
    }
}
