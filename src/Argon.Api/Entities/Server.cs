namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public sealed record Server
{
    public                          Guid                        Id                     { get; set; } = Guid.NewGuid();
    public                          DateTime                    CreatedAt              { get; set; } = DateTime.UtcNow;
    public                          DateTime                    UpdatedAt              { get; set; } = DateTime.UtcNow;
    [MaxLength(length: 255)] public string                      Name                   { get; set; } = string.Empty;
    [MaxLength(length: 255)] public string?                     Description            { get; set; } = string.Empty;
    [MaxLength(length: 255)] public string?                     AvatarUrl              { get; set; } = string.Empty;
    public                          List<Channel>               Channels               { get; set; } = new();
    public                          List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();

    public static implicit operator ServerDto(Server server)
    {
        return new ServerDto(
                             Id: server.Id,
                             CreatedAt: server.CreatedAt,
                             UpdatedAt: server.UpdatedAt,
                             Name: server.Name,
                             Description: server.Description,
                             AvatarUrl: server.AvatarUrl,
                             Channels: server.Channels.Select(selector: channel => (ChannelDto)channel).ToList(),
                             Users: server.UsersToServerRelations.Select(selector: relation => (UsersToServerRelationDto)relation).ToList()
                            );
    }
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(alias: nameof(ServerDto))]
public sealed partial record ServerDto(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Id(id: 0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Id(id: 1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Id(id: 2)]
    DateTime UpdatedAt,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Id(id: 3)]
    string Name,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Id(id: 4)]
    string? Description,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Id(id: 5)]
    string? AvatarUrl,
    [property: DataMember(Order = 6), MemoryPackOrder(order: 6), Id(id: 6)]
    List<ChannelDto> Channels,
    [property: DataMember(Order = 7), MemoryPackOrder(order: 7), Id(id: 7)]
    List<UsersToServerRelationDto> Users
);