namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;
using Orleans;

public sealed record User
{
    public Guid     Id        { get; init; } = Guid.NewGuid();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }  = DateTime.UtcNow;
    [Required, MaxLength(255)]
    public string Email { get; set; } = string.Empty;
    [MaxLength(255)]
    public string? Username { get; set; } = string.Empty;
    [MaxLength(30)]
    public string? PhoneNumber { get; set; } = string.Empty;
    [MaxLength(511)]
    public string? PasswordDigest { get; set; } = string.Empty;
    [MaxLength(1023)]
    public string? AvatarFileId { get; set; } = string.Empty;
    [MaxLength(128)]
    public string? OtpHash { get;                                    set; } = string.Empty;
    public DateTime?                   DeletedAt              { get; set; }
    public List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();

    public static implicit operator UserDto(User user) => new(user.Id, user.CreatedAt, user.UpdatedAt, user.Email, user.Username, user.PhoneNumber,
        user.AvatarFileId, user.DeletedAt, user.UsersToServerRelations.Select(relation => (ServerDto)relation.Server).ToList());
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(UserDto))]
public sealed record UserDto(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    DateTime UpdatedAt,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    string Email,
    [property: DataMember(Order = 4), MemoryPackOrder(4), Id(4)]
    string? Username,
    [property: DataMember(Order = 5), MemoryPackOrder(5), Id(5)]
    string? PhoneNumber,
    [property: DataMember(Order = 6), MemoryPackOrder(6), Id(6)]
    string? AvatarUrl,
    [property: DataMember(Order = 7), MemoryPackOrder(7), Id(7)]
    DateTime? DeletedAt,
    [property: DataMember(Order = 8), MemoryPackOrder(8), Id(8)]
    List<ServerDto> Servers);