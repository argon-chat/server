namespace Argon.Api.Entities.Configurations;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public sealed class UserDeviceHistoryTypeConfiguration : IEntityTypeConfiguration<UserDeviceHistory>
{
    public void Configure(EntityTypeBuilder<UserDeviceHistory> builder)
    {
        builder.HasKey(udh => new { udh.UserId, udh.MachineId });

        builder.HasOne(udh => udh.User)
           .WithMany()
           .HasForeignKey(udh => udh.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.Property(udh => udh.MachineId)
           .IsRequired()
           .HasMaxLength(64);

        builder.Property(udh => udh.LastKnownIP)
           .HasMaxLength(64);

        builder.Property(udh => udh.RegionAddress)
           .HasMaxLength(64);

        builder.Property(udh => udh.AppId)
           .HasMaxLength(64);
    }
}
