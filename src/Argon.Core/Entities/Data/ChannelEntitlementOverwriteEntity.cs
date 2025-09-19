namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record ChannelEntitlementOverwriteEntity 
    : ArgonEntityWithOwnership, 
      IArchetypeOverwrite, 
      IEntityTypeConfiguration<ChannelEntitlementOverwriteEntity>,
      IMapper<ChannelEntitlementOverwriteEntity, ChannelEntitlementOverwrite>
{
    public Guid ChannelId { get; set; }
    public virtual ChannelEntity Channel { get; set; }

    public IArchetypeScope Scope { get; set; }

    public Guid? ArchetypeId { get; set; }
    public virtual ArchetypeEntity Archetype { get; set; }

    public Guid? SpaceMemberId { get; set; }
    public virtual SpaceMemberEntity SpaceMember { get; set; }

    public ArgonEntitlement Allow { get; set; }
    public ArgonEntitlement Deny  { get; set; }

    public void Configure(EntityTypeBuilder<ChannelEntitlementOverwriteEntity> builder)
    {
        builder.HasOne(cpo => cpo.Channel)
           .WithMany(c => c.EntitlementOverwrites)
           .HasForeignKey(cpo => cpo.ChannelId);

        builder.HasOne(cpo => cpo.Archetype)
           .WithMany()
           .HasForeignKey(cpo => cpo.ArchetypeId)
           .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(cpo => cpo.SpaceMember)
           .WithMany()
           .HasForeignKey(cpo => cpo.SpaceMemberId)
           .OnDelete(DeleteBehavior.Restrict);
    }

    public static ChannelEntitlementOverwrite Map(scoped in ChannelEntitlementOverwriteEntity self)
        => throw new NotImplementedException();
}
