namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ChannelEntitlementOverwriteTypeConfiguration : IEntityTypeConfiguration<ChannelEntitlementOverwrite>
{
    public void Configure(EntityTypeBuilder<ChannelEntitlementOverwrite> builder)
    {
        builder.HasOne(cpo => cpo.Channel)
           .WithMany(c => c.EntitlementOverwrites)
           .HasForeignKey(cpo => cpo.ChannelId);

        builder.HasOne(cpo => cpo.Archetype)
           .WithMany()
           .HasForeignKey(cpo => cpo.ArchetypeId)
           .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(cpo => cpo.ServerMember)
           .WithMany()
           .HasForeignKey(cpo => cpo.ServerMemberId)
           .OnDelete(DeleteBehavior.Restrict);
    }
}
