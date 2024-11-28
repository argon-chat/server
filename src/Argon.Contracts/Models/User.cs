namespace Argon.Contracts.Models;

using MessagePack;
using System.ComponentModel.DataAnnotations;
using Newtonsoft.Json;
using Reinforced.Typings.Attributes;

[MessagePackObject(true), TsInterface]
public sealed record User : ArgonEntity
{
    public static readonly Guid SystemUser
        = Guid.Parse("11111111-2222-1111-2222-111111111111");

    [MaxLength(255)]
    public required string Email { get; set; }
    [MaxLength(64)]
    public required string Username { get; set; }
    [MaxLength(64)]
    public required string DisplayName { get; set; }
    [MaxLength(64)]
    public string? PhoneNumber { get; set; } = null;
    [MaxLength(512), IgnoreMember, JsonIgnore]
    public string? PasswordDigest { get; set; } = null;
    [MaxLength(128)]
    public string? AvatarFileId { get; set; } = null;
    [MaxLength(128), IgnoreMember, JsonIgnore]
    public string? OtpHash { get; set; } = null;
    [IgnoreMember]
    public ICollection<ServerMember> ServerMembers { get; set; } = new List<ServerMember>();

    public LockdownReason LockdownReason     { get; set; }
    public DateTime?      LockDownExpiration { get; set; }
}