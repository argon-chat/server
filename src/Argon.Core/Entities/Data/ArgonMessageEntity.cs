namespace Argon.Entities;

using Argon.Features.BotApi;
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

    [Column(TypeName = "jsonb")]
    public List<ControlRowV1>? Controls { get; set; }

    [Column(TypeName = "jsonb")]
    public List<MessageReactionData>? Reactions { get; set; }


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

        builder.Property(m => m.Controls)
           .HasColumnType("jsonb")
           .HasConversion(
                v => v == null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(v),
                v => v == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<List<ControlRowV1>>(v));

        builder.Property(m => m.Reactions)
           .HasColumnType("jsonb")
           .HasConversion(
                v => v == null ? null : Newtonsoft.Json.JsonConvert.SerializeObject(v),
                v => v == null ? null : Newtonsoft.Json.JsonConvert.DeserializeObject<List<MessageReactionData>>(v));
    }

    public const int ReactionUserPreviewLimit = 3;

    public static ArgonMessage Map(scoped in ArgonMessageEntity self)
        => new(self.MessageId, self.Reply, self.ChannelId, self.SpaceId,
            self.Text, self.Entities, self.CreatedAt.UtcDateTime, self.CreatorId,
            self.Reactions?.Select(r => new ReactionInfo(
                r.Emoji, r.CustomEmojiId, r.UserIds.Count,
                r.UserIds.Take(ReactionUserPreviewLimit).ToList())).ToList()
            ?? []);
}