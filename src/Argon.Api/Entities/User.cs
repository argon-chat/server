namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public sealed record User
{
    public                                     Guid     Id        { get; set; } = Guid.NewGuid();
    public                                     DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public                                     DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [Required, MaxLength(length: 255)]  public string   Email     { get; set; } = string.Empty;

    [MaxLength(length: 255), MinLength(length: 6)] 
    public string? Username { get; set; } = string.Empty;

    [MaxLength(length: 30)]   public string?                     PhoneNumber            { get; set; } = string.Empty;
    [MaxLength(length: 511)]  public string?                     PasswordDigest         { get; set; } = string.Empty;
    [MaxLength(length: 1023)] public string?                     AvatarUrl              { get; set; } = string.Empty;
    [MaxLength(length: 7)]    public string?                     OTP                    { get; set; } = string.Empty;
    public                           DateTime?                   DeletedAt              { get; set; }
    public                           List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();

    public static implicit operator UserDto(User user)
        => new(
            Id: user.Id,
            CreatedAt: user.CreatedAt,
            UpdatedAt: user.UpdatedAt,
            Email: user.Email,
            Username: user.Username,
            PhoneNumber: user.PhoneNumber,
            AvatarUrl: user.AvatarUrl,
            DeletedAt: user.DeletedAt,
            Servers: user.UsersToServerRelations.Select(selector: relation => (ServerDto)relation.Server).ToList()
        );
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(alias: nameof(UserDto))]
public sealed partial record UserDto(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Id(id: 0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Id(id: 1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Id(id: 2)]
    DateTime UpdatedAt,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Id(id: 3)]
    string Email,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Id(id: 4)]
    string? Username,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Id(id: 5)]
    string? PhoneNumber,
    [property: DataMember(Order = 6), MemoryPackOrder(order: 6), Id(id: 6)]
    string? AvatarUrl,
    [property: DataMember(Order = 7), MemoryPackOrder(order: 7), Id(id: 7)]
    DateTime? DeletedAt,
    [property: DataMember(Order = 8), MemoryPackOrder(order: 8), Id(id: 8)]
    List<ServerDto> Servers
);