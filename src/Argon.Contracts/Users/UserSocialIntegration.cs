namespace Argon.Users;

using System.ComponentModel.DataAnnotations.Schema;

public record UserSocialIntegration : ArgonEntity
{
    public Guid UserId { get; set; }
    public User User   { get; set; }

    [MaxLength(128)]
    public required string SocialId { get; set; }
    public required SocialKind Kind { get; set; }
    [Column(TypeName = "jsonb")]
    public required string UserData { get; set; }
}

public enum SocialKind
{
    None,
    Telegram
}

[MessagePackObject(true), TsInterface]
public record UserSocialIntegrationDto(Guid SocialId, string Kind, string UserData);

public static class UserSocialExtensions
{
    public static       UserSocialIntegrationDto       ToDto(this UserSocialIntegration user) => new(user.Id, user.Kind.ToString(), user.UserData);
    public async static Task<UserSocialIntegrationDto> ToDto(this Task<UserSocialIntegration> user) => (await user).ToDto();
    public static       List<UserSocialIntegrationDto> ToDto(this List<UserSocialIntegration> msg) => msg.Select(x => x.ToDto()).ToList();
    public async static Task<List<UserSocialIntegrationDto>> ToDto(this Task<List<UserSocialIntegration>> msg) => (await msg).ToDto();
}