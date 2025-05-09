namespace Argon.Users;

using System.ComponentModel.DataAnnotations.Schema;

public record UserSocialIntegration : ArgonEntity
{
    public Guid UserId { get; set; }
    public User User   { get; set; }

    [MaxLength(128)]
    public required string     SocialId { get; set; }
    public required SocialKind Kind     { get; set; }
    [Column(TypeName = "jsonb")]
    public required string     UserData { get; set; }
}

public enum SocialKind
{
    None,
    Telegram
}