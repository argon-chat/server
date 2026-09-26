namespace Argon.Entities;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

/// <summary>What a user left unsent in a channel's composer. One per (user, channel); last write wins.</summary>
public record MessageDraftEntity : IEntityTypeConfiguration<MessageDraftEntity>, IMapper<MessageDraftEntity, MessageDraft>
{
    public required Guid UserId    { get; set; }
    public required Guid ChannelId { get; set; }
    public required Guid SpaceId   { get; set; }

    public required string Text { get; set; }

    [Column(TypeName = "jsonb")]
    public List<IMessageEntity> Entities { get; set; } = new();

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Thirty days after the last save.</summary>
    public DateTimeOffset ExpireAt { get; set; }

    public void Configure(EntityTypeBuilder<MessageDraftEntity> builder)
    {
        builder.ToTable("MessageDrafts");

        builder.HasKey(x => new { x.UserId, x.ChannelId });

        builder.Property(x => x.Entities)
           .HasConversion<PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>>()
           .HasColumnType("jsonb")
           .Metadata.SetValueComparer(new PolyListJsonValueComparer<List<IMessageEntity>, IMessageEntity>());

        builder.WithTTL(x => x.ExpireAt, CronValue.Daily);
    }

    public static MessageDraft Map(scoped in MessageDraftEntity self)
        => new(self.ChannelId, self.Text, self.Entities ?? [], self.UpdatedAt.ToUniversalTime());
}
