namespace Argon.Entities;

using ion.runtime;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UserProfileEntity : ArgonEntity, IEntityTypeConfiguration<UserProfileEntity>, IMapper<UserProfileEntity, ArgonUserProfile>
{
    public required Guid         UserId             { get; set; }
    public virtual  UserEntity   User               { get; set; }
    public          string?      CustomStatus       { get; set; }
    public          string?      CustomStatusIconId { get; set; }
    public          string?      BannerFileId       { get; set; }
    public          DateOnly?    DateOfBirth        { get; set; }
    public          string?      Bio                { get; set; }
    public          List<string> Badges             { get; set; } = new();


    public void Configure(EntityTypeBuilder<UserProfileEntity> builder)
    {
        builder.Property(x => x.CustomStatus)
           .HasMaxLength(128);
        builder.Property(x => x.CustomStatusIconId)
           .HasMaxLength(128);
        builder.Property(x => x.BannerFileId)
           .HasMaxLength(128);
        builder.Property(x => x.Bio)
           .HasMaxLength(512);

        builder.Property(u => u.Badges)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>()
            );

        builder.HasOne(x => x.User)
           .WithOne(x => x.Profile)
           .HasForeignKey<UserProfileEntity>(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }

    public static ArgonUserProfile Map(scoped in UserProfileEntity self)
        => new(
            self.UserId, 
            self.CustomStatus, 
            self.CustomStatusIconId,
            self.BannerFileId,
            self.DateOfBirth,
            self.Bio,
            false,
            new IonArray<string>(self.Badges),
            IonArray<SpaceMemberArchetype>.Empty);
}