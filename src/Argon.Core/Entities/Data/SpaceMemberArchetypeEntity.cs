namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SpaceMemberArchetypeEntity : IEntityTypeConfiguration<SpaceMemberArchetypeEntity>, IMapper<SpaceMemberArchetypeEntity, SpaceMemberArchetype>
{
    public Guid SpaceMemberId { get; set; }
    public Guid ArchetypeId    { get; set; }

    public virtual ArchetypeEntity Archetype { get; set; }
    public virtual SpaceMemberEntity ServerMember { get; set; }


    public void Configure(EntityTypeBuilder<SpaceMemberArchetypeEntity> builder)
    {
        builder.HasKey(x => new {
            ServerMemberId = x.SpaceMemberId,
            x.ArchetypeId
        });

        builder.HasOne(x => x.ServerMember)
           .WithMany(x => x.SpaceMemberArchetypes)
           .HasForeignKey(x => x.SpaceMemberId);

        builder.HasOne(x => x.Archetype)
           .WithMany(x => x.SpaceMemberRoles)
           .HasForeignKey(x => x.ArchetypeId);
    }

    public static SpaceMemberArchetype Map(scoped in SpaceMemberArchetypeEntity self)
        => new(self.SpaceMemberId, self.ArchetypeId);
}
