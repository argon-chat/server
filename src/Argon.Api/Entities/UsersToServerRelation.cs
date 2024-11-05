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
    public Guid       Id              { get; init; } = Guid.NewGuid();
    public DateTime   CreatedAt       { get; init; } = DateTime.UtcNow;
    public DateTime   UpdatedAt       { get; set; }  = DateTime.UtcNow;
    public Guid       ServerId        { get; set; }  = Guid.Empty;
    public Server     Server          { get; set; }
    public DateTime   Joined          { get; set; } = DateTime.UtcNow;
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
            relation.Id,
            relation.CreatedAt,
            relation.Joined,
            relation.Role,
            relation.CustomUsername,
            relation.IsBanned,
            relation.IsMuted,
            relation.BannedUntil,
            relation.MutedUntil,
            relation.AvatarUrl,
            relation.CustomAvatarUrl,
            relation.BanReason,
            relation.MuteReason,
            relation.ServerId,
            relation.UserId
        );
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(nameof(UsersToServerRelationDto))]
public sealed partial record UsersToServerRelationDto(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    DateTime Joined,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    ServerRole Role,
    [property: DataMember(Order = 4), MemoryPackOrder(4), Id(4)]
    string CustomUsername,
    [property: DataMember(Order = 5), MemoryPackOrder(5), Id(5)]
    bool IsBanned,
    [property: DataMember(Order = 6), MemoryPackOrder(6), Id(6)]
    bool IsMuted,
    [property: DataMember(Order = 7), MemoryPackOrder(7), Id(7)]
    DateTime? BannedUntil,
    [property: DataMember(Order = 8), MemoryPackOrder(8), Id(8)]
    DateTime? MutedUntil,
    [property: DataMember(Order = 9), MemoryPackOrder(9), Id(9)]
    string AvatarUrl,
    [property: DataMember(Order = 10), MemoryPackOrder(10), Id(10)]
    string? CustomAvatarUrl,
    [property: DataMember(Order = 11), MemoryPackOrder(11), Id(11)]
    string? BanReason,
    [property: DataMember(Order = 12), MemoryPackOrder(12), Id(12)]
    string? MuteReason,
    [property: DataMember(Order = 13), MemoryPackOrder(13), Id(13)]
    Guid ServerId,
    [property: DataMember(Order = 14), MemoryPackOrder(14), Id(14)]
    Guid UserId
);