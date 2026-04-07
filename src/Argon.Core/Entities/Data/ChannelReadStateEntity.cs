namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ChannelReadStateEntity : IEntityTypeConfiguration<ChannelReadStateEntity>
{
    public required Guid           UserId            { get; set; }
    public required Guid           ChannelId         { get; set; }
    public          Guid?          SpaceId           { get; set; }
    public          long           LastReadMessageId { get; set; }
    public          int            MentionCount      { get; set; }
    public          DateTimeOffset UpdatedAt         { get; set; } = DateTimeOffset.UtcNow;

    public void Configure(EntityTypeBuilder<ChannelReadStateEntity> builder)
    {
        builder.ToTable("ChannelReadStates");

        builder.HasKey(x => new { x.UserId, x.ChannelId });

        builder.Property(x => x.UserId).IsRequired();
        builder.Property(x => x.ChannelId).IsRequired();
        builder.Property(x => x.LastReadMessageId).IsRequired();
        builder.Property(x => x.MentionCount).IsRequired();
        builder.Property(x => x.UpdatedAt).IsRequired();

        builder.HasIndex(x => new { x.UserId, x.SpaceId })
            .HasDatabaseName("ix_channel_read_states_user_space");

        builder.HasIndex(x => x.UserId)
            .HasDatabaseName("ix_channel_read_states_user");
    }
}
