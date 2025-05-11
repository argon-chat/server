namespace Argon.Users;

[MessagePackObject(true)]
public record User : ArgonEntity
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
    [MaxLength(64), IgnoreMember, JsonIgnore, TsIgnore]
    public string? PhoneNumber { get; set; } = null;
    [MaxLength(512), IgnoreMember, JsonIgnore, TsIgnore]
    public string? PasswordDigest { get; set; } = null;
    [MaxLength(128)]
    public string? AvatarFileId { get; set; } = null;
    [MaxLength(128), IgnoreMember, JsonIgnore, TsIgnore]
    public string? OtpHash { get; set; } = null;
    [IgnoreMember]
    public ICollection<ServerMember> ServerMembers { get; set; } = new List<ServerMember>();

    public LockdownReason  LockdownReason     { get; set; }
    public DateTimeOffset? LockDownExpiration { get; set; }

    [IgnoreMember]
    public virtual UserProfile Profile { get; set; }
}

[MessagePackObject(true), TsInterface]
public sealed record UserDto(Guid UserId, string Username, string DisplayName, string? AvatarFileId);

public static class UserExtensions
{
    public static       UserDto       ToDto(this User user)       => new(user.Id, user.Username, user.DisplayName, user.AvatarFileId);
    public async static Task<UserDto> ToDto(this Task<User> user) => (await user).ToDto();
}