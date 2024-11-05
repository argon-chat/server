namespace Argon.Api.Entities;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public enum ServerRole : ushort // TODO: sort out roles and how we actually want to handle them
{
    User,
    Admin,
    Owner
}

public sealed record UsersToServerRelation
{
    public Guid       Id              { get; set; } = Guid.NewGuid();
    public DateTime   CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime   UpdatedAt       { get; set; } = DateTime.UtcNow;
    public Guid       ServerId        { get; set; } = Guid.Empty;
    public Server     Server          { get; set; }
    public DateTime   Joined          { get; }      = DateTime.UtcNow;
    public ServerRole Role            { get; set; } = ServerRole.User;
    public Guid       UserId          { get; set; } = Guid.Empty;
    public User       User            { get; set; }
    public string     CustomUsername  { get; set; } = string.Empty;
    public bool       IsBanned        { get; set; }
    public bool       IsMuted         { get; set; }
    public DateTime?  BannedUntil     { get; set; }
    public DateTime?  MutedUntil      { get; set; }
    public string     AvatarUrl       { get; set; } = string.Empty;
    public string?    CustomAvatarUrl { get; set; }
    public string?    BanReason       { get; set; }
    public string?    MuteReason      { get; set; }

    public static implicit operator UsersToServerRelationDto(UsersToServerRelation relation)
        => new(
               Id: relation.Id,
               CreatedAt: relation.CreatedAt,
               Joined: relation.Joined,
               Role: relation.Role,
               CustomUsername: relation.CustomUsername,
               IsBanned: relation.IsBanned,
               IsMuted: relation.IsMuted,
               BannedUntil: relation.BannedUntil,
               MutedUntil: relation.MutedUntil,
               AvatarUrl: relation.AvatarUrl,
               CustomAvatarUrl: relation.CustomAvatarUrl,
               BanReason: relation.BanReason,
               MuteReason: relation.MuteReason,
               ServerId: relation.ServerId,
               UserId: relation.UserId
              );
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(alias: nameof(UsersToServerRelationDto))]
public sealed partial record UsersToServerRelationDto(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Id(id: 0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Id(id: 1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Id(id: 2)]
    DateTime Joined,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Id(id: 3)]
    ServerRole Role,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Id(id: 4)]
    string CustomUsername,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Id(id: 5)]
    bool IsBanned,
    [property: DataMember(Order = 6), MemoryPackOrder(order: 6), Id(id: 6)]
    bool IsMuted,
    [property: DataMember(Order = 7), MemoryPackOrder(order: 7), Id(id: 7)]
    DateTime? BannedUntil,
    [property: DataMember(Order = 8), MemoryPackOrder(order: 8), Id(id: 8)]
    DateTime? MutedUntil,
    [property: DataMember(Order = 9), MemoryPackOrder(order: 9), Id(id: 9)]
    string AvatarUrl,
    [property: DataMember(Order = 10), MemoryPackOrder(order: 10), Id(id: 10)]
    string? CustomAvatarUrl,
    [property: DataMember(Order = 11), MemoryPackOrder(order: 11), Id(id: 11)]
    string? BanReason,
    [property: DataMember(Order = 12), MemoryPackOrder(order: 12), Id(id: 12)]
    string? MuteReason,
    [property: DataMember(Order = 13), MemoryPackOrder(order: 13), Id(id: 13)]
    Guid ServerId,
    [property: DataMember(Order = 14), MemoryPackOrder(order: 14), Id(id: 14)]
    Guid UserId
);