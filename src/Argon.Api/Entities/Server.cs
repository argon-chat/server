namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public sealed record Server
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(255)] public string Name { get; set; } = string.Empty;
    [MaxLength(255)] public string? Description { get; set; } = string.Empty;
    [MaxLength(255)] public string? AvatarUrl { get; set; } = string.Empty;
    public List<Channel> Channels { get; set; } = new();
    public List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();

    public static implicit operator ServerDto(Server server)
    {
        return new ServerDto(
            server.Id,
            server.CreatedAt,
            server.UpdatedAt,
            server.Name,
            server.Description,
            server.AvatarUrl,
            server.Channels.Select(channel => (ChannelDto)channel).ToList(),
            server.UsersToServerRelations.Select(relation => (UsersToServerRelationDto)relation).ToList()
        );
    }
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(ServerDto))]
public sealed partial record ServerDto(
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
    string Name,
    [property: DataMember(Order = 4)]
    [property: MemoryPackOrder(4)]
    [property: Id(4)]
    string? Description,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Id(5)]
    string? AvatarUrl,
    [property: DataMember(Order = 6)]
    [property: MemoryPackOrder(6)]
    [property: Id(6)]
    List<ChannelDto> Channels,
    [property: DataMember(Order = 7)]
    [property: MemoryPackOrder(7)]
    [property: Id(7)]
    List<UsersToServerRelationDto> Users
);