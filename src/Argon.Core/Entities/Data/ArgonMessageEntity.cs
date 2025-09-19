namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;
using ion.runtime;

public record ArgonMessageEntity : ArgonEntityWithOwnershipNoKey, IEntityTypeConfiguration<ArgonMessageEntity>,
                                   IMapper<ArgonMessageEntity, ArgonMessage>
{
    [Required, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public ulong MessageId { get;  set; }
    public Guid   ServerId  { get; set; }
    public Guid   ChannelId { get; set; }
    public ulong? Reply     { get; set; }

    [MaxLength(2048)]
    public string Text { get; set; }

    [Column(TypeName = "jsonb")]
    public List<IMessageEntity> Entities { get; set; } = new();


    public void Configure(EntityTypeBuilder<ArgonMessageEntity> builder)
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
           .HasConversion<PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>>()
           .HasColumnType("jsonb");
    }

    public static ArgonMessage Map(scoped in ArgonMessageEntity self)
        => new(self.MessageId, self.Reply, self.ChannelId, self.ServerId,
            self.Text, self.Entities, self.CreatedAt.UtcDateTime, self.CreatorId);
}