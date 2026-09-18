namespace Argon.Entities;

using ion.runtime;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UserProfileEntity : ArgonEntity, IEntityTypeConfiguration<UserProfileEntity>, IMapper<UserProfileEntity, ArgonUserProfile>
{
    public required Guid         UserId             { get; set; }
    public virtual  UserEntity   User               { get; set; }
    public          string?      CustomStatus       { get; set; }
    public          string?      CustomStatusIconId { get; set; }
    public          DateOnly?    DateOfBirth        { get; set; }
    public          string?      Bio                { get; set; }
    public          List<string> Badges             { get; set; } = new();
    public          int?         BackgroundId       { get; set; }
    public          int?         VoiceCardEffectId  { get; set; }
    public          int?         AvatarFrameId      { get; set; }
    public          int?         NickEffectId       { get; set; }
    public          int?         PrimaryColor       { get; set; }
    public          int?         AccentColor        { get; set; }


    public void Configure(EntityTypeBuilder<UserProfileEntity> builder)
    {
        builder.Property(x => x.CustomStatus)
           .HasMaxLength(128);
        builder.Property(x => x.CustomStatusIconId)
           .HasMaxLength(128);
        builder.Property(x => x.Bio)
           .HasMaxLength(512);

        builder.Property(u => u.Badges)
           .HasColumnType("jsonb")
           .HasConversion(
                v => JsonConvert.SerializeObject(v),
                v => JsonConvert.DeserializeObject<List<string>>(v) ?? new List<string>()
            )
           .Metadata.SetValueComparer(new JsonValueComparer<List<string>>());

        builder.HasOne(x => x.User)
           .WithOne(x => x.Profile)
           .HasForeignKey<UserProfileEntity>(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }

    public static ArgonUserProfile Map(scoped in UserProfileEntity self)
        => new(
            self.UserId,
            // A look's own name and picture, filled in by the projection: this mapper has no scope,
            // and the answer differs between them.
            null,
            null,
            self.CustomStatus, 
            self.CustomStatusIconId,
            null,
            self.DateOfBirth,
            self.Bio,
            new IonArray<string>(self.Badges),
            IonArray<SpaceMemberArchetype>.Empty,
            self.BackgroundId,
            self.VoiceCardEffectId,
            self.AvatarFrameId,
            self.NickEffectId,
            self.PrimaryColor,
            self.AccentColor,
            // The user row is the authority on "in Argon since", but most call sites read the profile
            // without joining it. The profile row is inserted in the same transaction as the user
            // (see registration), so its own CreatedAt is the same instant and serves as the fallback.
            (self.User is { } owner ? owner.CreatedAt : self.CreatedAt).UtcDateTime,

            // Left empty here and filled by ICosmeticProfileProjection, which needs a database and a
            // feature-flag read to answer. This mapper is static and has neither — so a profile that
            // never passes through the projection carries no cosmetics, which is the safe direction:
            // a missed call site renders as "wearing nothing" rather than as somebody else's look.
            null,
            null);
}