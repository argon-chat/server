namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UsernameReservedEntity : IEntityTypeConfiguration<UsernameReservedEntity>
{
    [Key]
    public Guid Id { get;                            set; }
    public required string UserName           { get; set; }
    public required string NormalizedUserName { get; set; }
    public          bool   IsBanned           { get; set; }
    public          bool   IsReserved         { get; set; }
    public void Configure(EntityTypeBuilder<UsernameReservedEntity> builder)
    {
        builder.Property(x => x.NormalizedUserName)
           .HasMaxLength(64);
        builder.HasIndex(x => x.NormalizedUserName)
           .IsUnique();
    }
}