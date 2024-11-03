namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public enum ServerRole : ushort // TODO: sort out roles and how we actually want to handle them
{
    User,
    Admin,
    Owner
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias("Argon.Api.Entities.UsersToServerRelation")]
public sealed partial record UsersToServerRelation
{
    [System.ComponentModel.DataAnnotations.Key]
    [Id(0)]
    [MemoryPackOrder(0)]
    [DataMember(Order = 0)]
    public Guid Id { get; private set; } = Guid.NewGuid();

    [Id(1)]
    [MemoryPackOrder(1)]
    [DataMember(Order = 1)]
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    [Id(2)]
    [MemoryPackOrder(2)]
    [DataMember(Order = 2)]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [Id(3)]
    [MemoryPackOrder(3)]
    [DataMember(Order = 3)]
    public Guid ServerId { get; set; } = Guid.Empty;

    [Id(4)]
    [MemoryPackOrder(4)]
    [DataMember(Order = 4)]
    public DateTime Joined { get; } = DateTime.UtcNow;

    [Id(5)]
    [MemoryPackOrder(5)]
    [DataMember(Order = 5)]
    public ServerRole Role { get; set; } = ServerRole.User;

    [Id(6)]
    [MemoryPackOrder(6)]
    [DataMember(Order = 6)]
    public Guid UserId { get; set; } = Guid.Empty;

    [MaxLength(255)]
    [Id(7)]
    [MemoryPackOrder(7)]
    [DataMember(Order = 7)]
    public string CustomUsername { get; set; } = string.Empty;

    [Id(8)]
    [MemoryPackOrder(8)]
    [DataMember(Order = 8)]
    public bool IsBanned { get; set; }

    [Id(9)]
    [MemoryPackOrder(9)]
    [DataMember(Order = 9)]
    public bool IsMuted { get; set; }

    [Id(10)]
    [MemoryPackOrder(10)]
    [DataMember(Order = 10)]
    public DateTime? BannedUntil { get; set; }

    [Id(11)]
    [MemoryPackOrder(11)]
    [DataMember(Order = 11)]
    public DateTime? MutedUntil { get; set; }

    [MaxLength(255)]
    [Id(12)]
    [MemoryPackOrder(12)]
    [DataMember(Order = 12)]
    public string AvatarUrl { get; set; } = string.Empty;

    [MaxLength(255)]
    [Id(13)]
    [MemoryPackOrder(13)]
    [DataMember(Order = 13)]
    public string? CustomAvatarUrl { get; set; }

    [MaxLength(255)]
    [Id(14)]
    [MemoryPackOrder(14)]
    [DataMember(Order = 14)]
    public string? BanReason { get; set; }

    [MaxLength(255)]
    [Id(15)]
    [MemoryPackOrder(15)]
    [DataMember(Order = 15)]
    public string? MuteReason { get; set; }
}