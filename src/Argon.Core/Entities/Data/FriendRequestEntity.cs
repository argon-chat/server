namespace Argon.Core.Entities.Data;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record FriendRequestEntity : IEntityTypeConfiguration<FriendRequestEntity>, IMapper<FriendRequestEntity, FriendRequest>
{
    public const string         TableName = "user_friend_requests";
    public       Guid           RequesterId { get; set; }
    public       Guid           TargetId    { get; set; }
    public       DateTimeOffset RequestedAt { get; set; }
    public       DateOnly       ExpiredAt   { get; set; }

    public void Configure(EntityTypeBuilder<FriendRequestEntity> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(x => new
        {
            x.RequesterId,
            x.TargetId
        });

        builder.Property(x => x.RequesterId)
           .IsRequired();

        builder.Property(x => x.TargetId)
           .IsRequired();

        builder.Property(x => x.RequestedAt)
           .HasColumnType("timestamptz")
           .HasDefaultValueSql("now()")
           .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.RequesterId)
           .HasDatabaseName("idx_friend_requests_requester");

        builder.HasIndex(x => x.TargetId)
           .HasDatabaseName("idx_friend_requests_target");

        builder.Property(x => x.ExpiredAt)
           .AsTTlField();

        builder.WithTTL(x => x.RequestedAt, CronValue.Daily);
    }

    public static FriendRequest Map(scoped in FriendRequestEntity self)
        => new(self.RequesterId, self.TargetId, self.RequestedAt.UtcDateTime);
}