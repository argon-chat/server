namespace Argon.Api.Entities.Configurations;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ArchetypeTypeConfiguration : IEntityTypeConfiguration<Archetype>
{
    public void Configure(EntityTypeBuilder<Archetype> builder)
    {
        builder.HasOne(x => x.Server)
           .WithMany(x => x.Archetypes)
           .HasForeignKey(x => x.ServerId);

        builder.Property(x => x.Colour)
           .HasConversion<ColourConverter>();
    }
}
