namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

public record ArgonMessageEntity : ArgonEntityWithOwnershipNoKey, IEntityTypeConfiguration<ArgonMessageEntity>,
                                   IMapper<ArgonMessageEntity, ArgonMessage>
{
    public          long  MessageId { get; set; }
    public required Guid  SpaceId   { get; set; }
    public required Guid  ChannelId { get; set; }
    public          long? Reply     { get; set; }

    public required string Text { get; set; }

    [Column(TypeName = "jsonb")]
    public List<IMessageEntity> Entities { get; set; } = new();


    public void Configure(EntityTypeBuilder<ArgonMessageEntity> builder)
    {
        builder.HasKey(m => new
        {
            m.SpaceId,
            m.ChannelId,
            m.MessageId
        });
        builder.HasIndex(m => new
            {
                m.SpaceId,
                m.ChannelId,
                m.CreatedAt
            })
           .IncludeProperties(m => new
            {
                m.Text,
                m.Entities
            });

        builder.Property(m => m.MessageId)
           .HasColumnType("BIGINT")
           .ValueGeneratedOnAdd()
           .HasDefaultValueSql("unique_rowid()");

        builder.Property(m => m.Reply)
           .HasColumnType("BIGINT");

        builder.Property(m => m.Entities)
           .HasConversion<PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>>()
           .HasColumnType("jsonb");
    }

    public static ArgonMessage Map(scoped in ArgonMessageEntity self)
        => new(self.MessageId, self.Reply, self.ChannelId, self.SpaceId,
            self.Text, self.Entities, self.CreatedAt.UtcDateTime, self.CreatorId);
}