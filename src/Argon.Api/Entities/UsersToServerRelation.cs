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
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid ServerId { get; set; } = Guid.Empty;
    public Server Server { get; set; }
    public DateTime Joined { get; } = DateTime.UtcNow;
    public ServerRole Role { get; set; } = ServerRole.User;
    public Guid UserId { get; set; } = Guid.Empty;
    public User User { get; set; }
    public string CustomUsername { get; set; } = string.Empty;
    public bool IsBanned { get; set; }
    public bool IsMuted { get; set; }
    public DateTime? BannedUntil { get; set; }
    public DateTime? MutedUntil { get; set; }
    public string AvatarUrl { get; set; } = string.Empty;
    public string? CustomAvatarUrl { get; set; }
    public string? BanReason { get; set; }
    public string? MuteReason { get; set; }

    public static implicit operator UsersToServerRelationDto(UsersToServerRelation relation)
    {
        return new UsersToServerRelationDto(
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
            relation.MuteReason
        );
    }
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(UsersToServerRelationDto))]
public sealed partial record UsersToServerRelationDto(
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
    DateTime Joined,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Id(3)]
    ServerRole Role,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Id(4)]
    string CustomUsername,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Id(5)]
    bool IsBanned,
    [property: DataMember(Order = 6)]
    [property: MemoryPackOrder(6)]
    [property: Id(6)]
    bool IsMuted,
    [property: DataMember(Order = 7)]
    [property: MemoryPackOrder(7)]
    [property: Id(7)]
    DateTime? BannedUntil,
    [property: DataMember(Order = 8)]
    [property: MemoryPackOrder(8)]
    [property: Id(8)]
    DateTime? MutedUntil,
    [property: DataMember(Order = 9)]
    [property: MemoryPackOrder(9)]
    [property: Id(9)]
    string AvatarUrl,
    [property: DataMember(Order = 10)]
    [property: MemoryPackOrder(10)]
    [property: Id(10)]
    string? CustomAvatarUrl,
    [property: DataMember(Order = 11)]
    [property: MemoryPackOrder(11)]
    [property: Id(11)]
    string? BanReason,
    [property: DataMember(Order = 12)]
    [property: MemoryPackOrder(12)]
    [property: Id(12)]
    string? MuteReason
);