namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Servers;

public sealed class MeetSingleInviteLinkTypeConfiguration : IEntityTypeConfiguration<MeetSingleInviteLink>
{
    public void Configure(EntityTypeBuilder<MeetSingleInviteLink> builder)
    {
        builder.HasKey(x => x.Id);
    }
}
