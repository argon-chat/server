namespace Argon.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record UserEntity : ArgonEntity, IMapper<UserEntity, ArgonUser>, IEntityTypeConfiguration<UserEntity>
{
    public static readonly Guid SystemUser
        = Guid.Parse("11111111-2222-1111-2222-111111111111");

    [MaxLength(255)]
    public required string Email { get; set; }
    [MaxLength(64)]
    public required string Username { get; set; }
    [MaxLength(64)]
    public required string NormalizedUsername { get; set; }
    [MaxLength(64)]
    public required string DisplayName { get; set; }
    [MaxLength(64)]
    public string? PhoneNumber { get; set; } = null;
    [MaxLength(512)]
    public string? PasswordDigest { get; set; } = null;
    [MaxLength(128)]
    public string? AvatarFileId { get; set; } = null;

    public ICollection<SpaceMemberEntity> ServerMembers { get; set; } = new List<SpaceMemberEntity>();

    public LockdownReason LockdownReason { get; set; }
    public DateTimeOffset? LockDownExpiration { get; set; }

    public DateOnly? DateOfBirth { get; set; }


    public virtual UserProfileEntity Profile { get; set; }

    public static ArgonUser Map(scoped in UserEntity self)
        => new(self.Id, self.Username, self.DisplayName, self.AvatarFileId);

    public void Configure(EntityTypeBuilder<UserEntity> builder)
    {
        builder.HasKey(x => x.Id);
        builder.HasIndex(x => x.Email).IsUnique();
        builder.HasIndex(x => x.NormalizedUsername).IsUnique();
        builder.HasIndex(x => x.DisplayName);
    }
}