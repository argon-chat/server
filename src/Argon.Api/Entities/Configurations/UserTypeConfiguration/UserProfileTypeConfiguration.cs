namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Newtonsoft.Json;

public sealed class UserProfileTypeConfiguration : IEntityTypeConfiguration<UserProfile>
{
    public void Configure(EntityTypeBuilder<UserProfile> builder)
    {
        builder.Property(u => u.Badges)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v ?? new List<string>()),
                v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>()
            );

        builder.HasOne(x => x.User)
           .WithOne(x => x.Profile)
           .HasForeignKey<UserProfile>(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
