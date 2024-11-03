namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;

public enum ServerRole : ushort // TODO: sort out roles and how we actually want to handle them
{
    User,
    Admin,
    Owner
}

public class UsersToServerRelation : ApplicationRecord
{
    [Id(3)] public Guid ServerId { get; set; } = Guid.Empty;
    [Id(4)] public DateTime Joined { get; } = DateTime.UtcNow;
    [Id(5)] public ServerRole Role { get; set; } = ServerRole.User;
    [Id(6)] public Guid UserId { get; set; } = Guid.Empty;
    [MaxLength(255), Id(7)] public string CustomUsername { get; set; } = string.Empty;
    [Id(8)] public bool IsBanned { get; set; } = false;
    [Id(9)] public bool IsMuted { get; set; } = false;
    [Id(10)] public DateTime? BannedUntil { get; set; } = null;
    [Id(11)] public DateTime? MutedUntil { get; set; } = null;
    [MaxLength(255), Id(12)] public string AvatarUrl { get; set; } = string.Empty;
    [MaxLength(255), Id(13)] public string? CustomAvatarUrl { get; set; } = null;
    [MaxLength(255), Id(14)] public string? BanReason { get; set; } = null;
    [MaxLength(255), Id(15)] public string? MuteReason { get; set; } = null;
}