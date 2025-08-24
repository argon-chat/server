namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ServerMemberArchetypeTypeConfiguration : IEntityTypeConfiguration<ServerMemberArchetype>
{
    public void Configure(EntityTypeBuilder<ServerMemberArchetype> builder)
    {
        builder.HasKey(x => new
        {
            x.ServerMemberId,
            x.ArchetypeId
        });

        builder.HasOne(x => x.ServerMember)
           .WithMany(x => x.ServerMemberArchetypes)
           .HasForeignKey(x => x.ServerMemberId);

        builder.HasOne(x => x.Archetype)
           .WithMany(x => x.ServerMemberRoles)
           .HasForeignKey(x => x.ArchetypeId);
    }
}
