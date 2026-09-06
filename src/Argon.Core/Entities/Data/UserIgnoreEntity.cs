namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One person quietly ignoring another. Unlike <see cref="UserBlockEntity"/> nothing is torn down
/// and the other side is not told: they can still write, but what they send does not count as
/// unread for the ignorer. Kept as its own table rather than a flag on the block so a client can
/// offer both and lift either on its own.
/// </summary>
public record UserIgnoreEntity : IMapper<UserIgnoreEntity, UserIgnore>, IEntityTypeConfiguration<UserIgnoreEntity>
{
    public const string         TableName = "user_ignores";
    public       Guid           UserId    { get; set; }
    public       Guid           IgnoredId { get; set; }
    public       DateTimeOffset CreatedAt { get; set; }

    public static UserIgnore Map(scoped in UserIgnoreEntity self)
        => new(self.UserId, self.IgnoredId, self.CreatedAt.UtcDateTime);

    public void Configure(EntityTypeBuilder<UserIgnoreEntity> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(x => new
        {
            x.UserId,
            x.IgnoredId
        });

        builder.Property(x => x.UserId)
           .IsRequired();

        builder.Property(x => x.IgnoredId)
           .IsRequired();

        builder.Property(x => x.CreatedAt)
           .HasColumnType("timestamptz")
           .HasDefaultValueSql("now()")
           .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.UserId)
           .HasDatabaseName("idx_user_ignores_user");
    }
}
