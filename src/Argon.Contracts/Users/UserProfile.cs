namespace Argon.Users;

using System.ComponentModel.DataAnnotations.Schema;
using ArchetypeModel;

public record UserProfile : ArgonEntity
{
    public required Guid UserId { get; set; }
    public virtual  User User   { get; set; }


    [MaxLength(128)]
    public string? CustomStatus { get; set; }
    [MaxLength(128)]
    public string? CustomStatusIconId { get; set; }

    [MaxLength(128)]
    public string? BannerFileId { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    [MaxLength(512)]
    public string? Bio { get; set; }

    public bool IsPremium { get; set; }

    [Column(TypeName = "jsonb")]
    public List<string> Badges { get; set; }
}

[MessagePackObject(true), TsInterface]
public sealed record UserProfileDto(
    Guid UserId,
    string? CustomStatus,
    string? CustomStatusIconId,
    string? BannerFileId,
    DateOnly? DateOfBirth,
    string? Bio,
    bool IsPremium,
    List<string> Badges)
{
    public List<ServerMemberArchetypeDto> Archetypes { get; set; } = new();
}

public static class UserProfileExtensions
{
    public static       UserProfileDto       ToDto(this UserProfile user)       => new(user.UserId, user.CustomStatus, user.CustomStatusIconId, user.BannerFileId, user.DateOfBirth, user.Bio, user.IsPremium, user.Badges);
    public async static Task<UserProfileDto> ToDto(this Task<UserProfile> user) => (await user).ToDto();
}