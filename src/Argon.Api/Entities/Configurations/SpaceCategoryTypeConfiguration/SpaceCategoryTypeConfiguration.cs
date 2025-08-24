namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Servers;

public sealed class SpaceCategoryTypeConfiguration : IEntityTypeConfiguration<SpaceCategory>
{
    public void Configure(EntityTypeBuilder<SpaceCategory> builder)
    {
        builder.HasOne(x => x.Server)
           .WithMany(x => x.SpaceCategories)
           .HasForeignKey(x => x.ServerId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(x => x.Channels)
           .WithOne(x => x.Category)
           .HasForeignKey(x => x.CategoryId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
