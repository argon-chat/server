namespace Argon.Core.Entities.Data;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UserBlockEntity : IMapper<UserBlockEntity, UserBlock>, IEntityTypeConfiguration<UserBlockEntity>
{
    public const string         TableName = "user_blocks";
    public       Guid           UserId    { get; set; }
    public       Guid           BlockedId { get; set; }
    public       DateTimeOffset CreatedAt { get; set; }

    public static UserBlock Map(scoped in UserBlockEntity self)
        => new(self.UserId, self.BlockedId, self.CreatedAt.UtcDateTime);

    public void Configure(EntityTypeBuilder<UserBlockEntity> builder)
    {
        builder.ToTable(TableName);

        builder.HasKey(x => new
        {
            x.UserId,
            x.BlockedId
        });

        builder.Property(x => x.UserId)
           .IsRequired();

        builder.Property(x => x.BlockedId)
           .IsRequired();

        builder.Property(x => x.CreatedAt)
           .HasColumnType("timestamptz")
           .HasDefaultValueSql("now()")
           .ValueGeneratedOnAdd();

        builder.HasIndex(x => x.UserId)
           .HasDatabaseName("idx_user_blocks_user");
    }
}