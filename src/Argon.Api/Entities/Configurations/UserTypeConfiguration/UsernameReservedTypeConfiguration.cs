namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class UsernameReservedTypeConfiguration : IEntityTypeConfiguration<UsernameReserved>
{
    public void Configure(EntityTypeBuilder<UsernameReserved> builder)
    {
        builder.HasIndex(x => x.NormalizedUserName)
           .IsUnique();
    }
}
