namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public sealed record User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [Required] [MaxLength(255)] public string Email { get; set; } = string.Empty;
    [MaxLength(255)] [MinLength(6)] public string? Username { get; set; } = string.Empty;
    [MaxLength(30)] public string? PhoneNumber { get; set; } = string.Empty;
    [MaxLength(511)] public string? PasswordDigest { get; set; } = string.Empty;
    [MaxLength(1023)] public string? AvatarUrl { get; set; } = string.Empty;
    [MaxLength(7)] public string? OTP { get; set; } = string.Empty;
    public DateTime? DeletedAt { get; set; }
    public List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();

    public static implicit operator UserDto(User user)
    {
        return new UserDto(
            user.Id,
            user.CreatedAt,
            user.UpdatedAt,
            user.Email,
            user.Username,
            user.PhoneNumber,
            user.AvatarUrl,
            user.DeletedAt,
            user.UsersToServerRelations.Select(relation => (ServerDto)relation.Server).ToList()
        );
    }
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(UserDto))]
public sealed partial record UserDto(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Id(0)]
    Guid Id,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Id(1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Id(2)]
    DateTime UpdatedAt,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Id(3)]
    string Email,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Id(4)]
    string? Username,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Id(5)]
    string? PhoneNumber,
    [property: DataMember(Order = 6)]
    [property: MemoryPackOrder(6)]
    [property: Id(6)]
    string? AvatarUrl,
    [property: DataMember(Order = 7)]
    [property: MemoryPackOrder(7)]
    [property: Id(7)]
    DateTime? DeletedAt,
    [property: DataMember(Order = 8)]
    [property: MemoryPackOrder(8)]
    [property: Id(8)]
    List<ServerDto> Servers
);