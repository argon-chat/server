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

        // ExpiredAt, not RequestedAt. A row is expired once the named column is in the past, and
        // RequestedAt defaults to now() on insert — so naming it declared every friend request expired
        // the instant it was written, and asked whatever applies the TTL to delete the entire table on
        // its first run. Nothing has happened only because the clause is emitted from CreateTable and
        // this table already existed; the day a reconciler turns the TTL on, or migrations are
        // regenerated, it would have.
        //
        // ExpiredAt is what AsTTlField exists for — it converts the DateOnly to timestamptz precisely
        // so a TTL can read it — and FriendsGrain sets it six months out when the request is made.
        builder.WithTTL(x => x.ExpiredAt, CronValue.Daily);
    }

    public static FriendRequest Map(scoped in FriendRequestEntity self)
        => new(self.RequesterId, self.TargetId, self.RequestedAt.UtcDateTime);
}