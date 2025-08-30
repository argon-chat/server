namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UserDeviceHistoryEntity : IEntityTypeConfiguration<UserDeviceHistoryEntity>
{
    [MaxLength(64)]
    public string MachineId { get;                      set; }
    public         Guid            UserId        { get; set; }
    public virtual UserEntity      User          { get; set; }
    public         DateTimeOffset? LastLoginTime { get; set; }
    public         string          LastKnownIP   { get; set; }
    public         string          RegionAddress { get; set; }
    public         string          AppId         { get; set; }
    public         DeviceTypeKind  DeviceType    { get; set; }


    public void Configure(EntityTypeBuilder<UserDeviceHistoryEntity> builder)
    {
        builder.HasKey(x => new
        {
            x.UserId,
            x.MachineId
        });

        builder.HasOne(x => x.User)
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);

        builder.Property(x => x.MachineId)
           .IsRequired()
           .HasMaxLength(64);

        builder.Property(x => x.LastKnownIP)
           .HasMaxLength(64);

        builder.Property(x => x.RegionAddress)
           .HasMaxLength(64);

        builder.Property(x => x.AppId)
           .HasMaxLength(64);
    }
}

public enum DeviceTypeKind
{
    Unknown,
    WindowsDesktop,
    OsxDesktop,
    Browser,
    IosMobile,
    AndroidMobile,
    Xbox,
    SteamDevice
}