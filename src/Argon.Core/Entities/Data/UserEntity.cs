namespace Argon.Entities;

using Api.Features.CoreLogic.Otp;
using Core.Entities.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UserEntity : ArgonEntity, IMapper<UserEntity, ArgonUser>, IEntityTypeConfiguration<UserEntity>
{
    public static readonly Guid SystemUser
        = Guid.Parse("11111111-2222-1111-2222-111111111111");

    public required string  Email          { get; set; }
    public required string  Username       { get; set; }
    public required string  DisplayName    { get; set; }
    public          string? PhoneNumber    { get; set; } = null;
    public          string? PasswordDigest { get; set; } = null;
    public          string? AvatarFileId   { get; set; } = null;

    public         ICollection<SpaceMemberEntity>   ServerMembers   { get; set; } = new List<SpaceMemberEntity>();
    public virtual ICollection<DevTeamMemberEntity> TeamMemberships { get; set; } = new List<DevTeamMemberEntity>();

    public DateOnly? DateOfBirth { get; set; }

    [MaxLength(512)]
    public string? TotpSecret { get; set; }

    public ArgonAuthMode PreferredAuthMode  { get; set; }
    public OtpMethod     PreferredOtpMethod { get; set; }

    public virtual UserProfileEntity Profile { get; set; }


    public string NormalizedEmail    { get; private set; } = null!;
    public string NormalizedUsername { get; private set; } = null!;

    public bool AllowedSendOptionalEmails { get; set; }
    public bool AgreeTOS                  { get; set; }

    public virtual BotEntity BotEntity { get; set; }


    public LockdownReason  LockdownReason       { get; set; }
    public DateTimeOffset? LockDownExpiration   { get; set; }
    public bool            LockDownIsAppealable { get; set; }

    public static ArgonUser Map(scoped in UserEntity self)
        => new(self.Id, self.Username, self.DisplayName, self.AvatarFileId);

    public void Configure(EntityTypeBuilder<UserEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Email)
           .IsRequired()
           .HasMaxLength(255);

        builder.Property(x => x.Username)
           .IsRequired()
           .HasMaxLength(64);

        builder.Property(x => x.NormalizedEmail)
           .HasColumnType("varchar(255)")
           .HasComputedColumnSql("lower(\"Email\")", true)
           .ValueGeneratedOnAddOrUpdate();

        builder.Property(x => x.NormalizedUsername)
           .HasColumnType("varchar(64)")
           .HasComputedColumnSql("lower(\"Username\")", true)
           .ValueGeneratedOnAddOrUpdate();

        builder.HasIndex(x => x.NormalizedEmail).IsUnique();
        builder.HasIndex(x => x.NormalizedUsername).IsUnique();
    }
}