namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class ServerMemberTypeConfiguration : IEntityTypeConfiguration<ServerMember>
{
    public void Configure(EntityTypeBuilder<ServerMember> builder)
    {
        builder.HasOne(x => x.Server)
           .WithMany(x => x.Users)
           .HasForeignKey(x => x.ServerId);

        builder.HasOne(x => x.User)
           .WithMany(x => x.ServerMembers)
           .HasForeignKey(x => x.UserId);
    }
}
