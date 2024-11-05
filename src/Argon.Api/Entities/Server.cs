namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public sealed record Server
{
    public                  Guid                        Id                     { get; set; } = Guid.NewGuid();
    public                  DateTime                    CreatedAt              { get; set; } = DateTime.UtcNow;
    public                  DateTime                    UpdatedAt              { get; set; } = DateTime.UtcNow;
    [MaxLength(255)] public string                      Name                   { get; set; } = string.Empty;
    [MaxLength(255)] public string?                     Description            { get; set; } = string.Empty;
    [MaxLength(255)] public string?                     AvatarUrl              { get; set; } = string.Empty;
    public                  List<Channel>               Channels               { get; set; } = new();
    public                  List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();

    public static implicit operator ServerDto(Server server)
        => new(
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

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(nameof(ServerDto))]
public sealed partial record ServerDto(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    DateTime UpdatedAt,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    string Name,
    [property: DataMember(Order = 4), MemoryPackOrder(4), Id(4)]
    string? Description,
    [property: DataMember(Order = 5), MemoryPackOrder(5), Id(5)]
    string? AvatarUrl,
    [property: DataMember(Order = 6), MemoryPackOrder(6), Id(6)]
    List<ChannelDto> Channels,
    [property: DataMember(Order = 7), MemoryPackOrder(7), Id(7)]
    List<UsersToServerRelationDto> Users
);