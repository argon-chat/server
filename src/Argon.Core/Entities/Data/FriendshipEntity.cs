namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record FriendshipEntity : IEntityTypeConfiguration<FriendshipEntity>, IMapper<FriendshipEntity, Friendship>
{
    public const string         TableName = "friendship_entity";
    public       Guid           UserId    { get; set; }
    public       Guid           FriendId  { get; set; }
    public       DateTimeOffset CreatedAt { get; set; }

    public void Configure(EntityTypeBuilder<FriendshipEntity> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(x => new { x.UserId, x.FriendId });

        builder.Property(x => x.UserId)
           .IsRequired();

        builder.Property(x => x.FriendId)
           .IsRequired();

        builder.Property(x => x.CreatedAt)
           .HasColumnType("timestamptz")
           .HasDefaultValueSql("now()")
           .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.UserId)
           .HasDatabaseName("idx_friendships_user");
    }

    public static Friendship Map(scoped in FriendshipEntity self)
        => new (self.UserId, self.FriendId, self.CreatedAt.UtcDateTime);
}