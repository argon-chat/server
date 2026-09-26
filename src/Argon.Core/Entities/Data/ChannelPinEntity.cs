namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>A message pinned to its channel. At most <c>ChannelGrain.PinLimit</c> per channel.</summary>
public record ChannelPinEntity : IEntityTypeConfiguration<ChannelPinEntity>
{
    public required Guid           SpaceId   { get; set; }
    public required Guid           ChannelId { get; set; }
    public required long           MessageId { get; set; }
    public required Guid           PinnedBy  { get; set; }
    public          DateTimeOffset PinnedAt  { get; set; } = DateTimeOffset.UtcNow;

    public void Configure(EntityTypeBuilder<ChannelPinEntity> builder)
    {
        builder.ToTable("ChannelPins");

        builder.HasKey(x => new { x.ChannelId, x.MessageId });

        builder.Property(x => x.ChannelId).ValueGeneratedNever();
        builder.Property(x => x.MessageId).HasColumnType("BIGINT").ValueGeneratedNever();
        builder.Property(x => x.SpaceId).IsRequired();
        builder.Property(x => x.PinnedBy).IsRequired();
        builder.Property(x => x.PinnedAt).IsRequired();
    }
}
