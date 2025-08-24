namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ArgonMessageTypeConfiguration : IEntityTypeConfiguration<ArgonMessage>
{
    public void Configure(EntityTypeBuilder<ArgonMessage> builder)
    {
        builder.HasKey(m => new
        {
            m.ServerId,
            m.ChannelId,
            m.MessageId
        });

        builder.HasIndex(m => new
        {
            m.ServerId,
            m.ChannelId,
            m.MessageId
        })
            .IsUnique();

        builder.Property(m => m.MessageId);

        builder.Property(m => m.Entities)
            .HasConversion<PolyListNewtonsoftJsonValueConverter<List<MessageEntity>, MessageEntity>>()
            .HasColumnType("jsonb");
    }
}
