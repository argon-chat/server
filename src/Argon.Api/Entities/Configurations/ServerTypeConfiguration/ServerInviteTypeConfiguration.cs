namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ServerInviteTypeConfiguration : IEntityTypeConfiguration<ServerInvite>
{
    public void Configure(EntityTypeBuilder<ServerInvite> builder)
    {
        builder.HasOne(c => c.Server)
           .WithMany(s => s.ServerInvites)
           .HasForeignKey(c => c.ServerId);

        builder.HasKey(x => x.Id);
    }
}
