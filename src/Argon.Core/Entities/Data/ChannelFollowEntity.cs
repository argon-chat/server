namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// A target text or announcement channel following a source announcement channel, in any space.
/// Hard-deleted on unfollow and when either channel goes, so the pair can be followed again.
/// </summary>
public record ChannelFollowEntity : IEntityTypeConfiguration<ChannelFollowEntity>
{
    public const int MaxSourcesPerTarget   = 20;
    public const int MaxFollowersPerSource = 5000;

    public required Guid           Id              { get; set; }
    public required Guid           SourceSpaceId   { get; set; }
    public required Guid           SourceChannelId { get; set; }
    public required Guid           TargetSpaceId   { get; set; }
    public required Guid           TargetChannelId { get; set; }
    public required Guid           CreatorId       { get; set; }
    public          DateTimeOffset CreatedAt       { get; set; } = DateTimeOffset.UtcNow;

    public void Configure(EntityTypeBuilder<ChannelFollowEntity> builder)
    {
        builder.ToTable("ChannelFollows");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.HasIndex(x => new
            {
                x.SourceChannelId,
                x.TargetChannelId
            })
           .IsUnique();

        builder.HasIndex(x => x.TargetChannelId);

        // Space deletion and member removal find the links of a whole space.
        builder.HasIndex(x => x.SourceSpaceId);
        builder.HasIndex(x => x.TargetSpaceId);
    }
}
