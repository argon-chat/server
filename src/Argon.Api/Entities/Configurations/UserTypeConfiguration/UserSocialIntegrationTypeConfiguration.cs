namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class UserSocialIntegrationTypeConfiguration : IEntityTypeConfiguration<UserSocialIntegration>
{
    public void Configure(EntityTypeBuilder<UserSocialIntegration> builder)
    {
        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(x => x.SocialId);
    }
}
