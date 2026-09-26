namespace Argon.Entities;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using System.ComponentModel.DataAnnotations.Schema;

/// <summary>A message written now and published later, as its author, by <c>ChannelComposerGrain</c>.</summary>
public record ScheduledPostEntity : ArgonEntity, IEntityTypeConfiguration<ScheduledPostEntity>,
                                    IMapper<ScheduledPostEntity, ScheduledPost>
{
    public required Guid SpaceId   { get; set; }
    public required Guid ChannelId { get; set; }
    public required Guid AuthorId  { get; set; }

    public required string Text { get; set; }

    [Column(TypeName = "jsonb")]
    public List<IMessageEntity> Entities { get; set; } = new();

    public DateTimeOffset       PublishAt { get; set; }
    public ScheduledPostStatus  Status    { get; set; }
    public ScheduledPostFailure Failure   { get; set; }

    /// <summary>SendMessage's randomId, so a publish repeated after a crash is deduplicated.</summary>
    public long  RandomId  { get; set; }
    public long? MessageId { get; set; }

    /// <summary>Null while pending; set once the post is published, cancelled or failed.</summary>
    public DateTimeOffset? ExpireAt { get; set; }

    public void Configure(EntityTypeBuilder<ScheduledPostEntity> builder)
    {
        builder.ToTable("ScheduledPosts");

        // Due scan and the channel's live posts.
        builder.HasIndex(x => new { x.ChannelId, x.Status, x.PublishAt });
        builder.HasIndex(x => x.AuthorId);

        builder.Property(x => x.Entities)
           .HasConversion<PolyListNewtonsoftJsonValueConverter<List<IMessageEntity>, IMessageEntity>>()
           .HasColumnType("jsonb")
           .Metadata.SetValueComparer(new PolyListJsonValueComparer<List<IMessageEntity>, IMessageEntity>());

        builder.WithTTL(x => x.ExpireAt!, CronValue.Daily);
    }

    public static ScheduledPost Map(scoped in ScheduledPostEntity self)
        => new(self.Id, self.SpaceId, self.ChannelId, self.AuthorId, self.Text, self.Entities ?? [],
            self.PublishAt.ToUniversalTime(), self.CreatedAt.ToUniversalTime(), self.Status, self.Failure, self.MessageId);
}
