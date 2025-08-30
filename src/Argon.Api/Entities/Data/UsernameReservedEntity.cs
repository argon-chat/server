namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UsernameReservedEntity : IEntityTypeConfiguration<UsernameReservedEntity>
{
    [System.ComponentModel.DataAnnotations.Key]
    public Guid Id { get;                   set; }
    public string UserName           { get; set; }
    public string NormalizedUserName { get; set; }
    public bool   IsBanned           { get; set; }
    public bool   IsReserved         { get; set; }
    public void Configure(EntityTypeBuilder<UsernameReservedEntity> builder)
        => builder.HasIndex(x => x.NormalizedUserName)
           .IsUnique();
}