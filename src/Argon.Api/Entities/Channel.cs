namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

public enum ChannelType : ushort
{
    Text,
    Voice,
    Announcement
}

public sealed record Channel
{
    public                          Guid        Id          { get; set; } = Guid.NewGuid();
    public                          DateTime    CreatedAt   { get; set; } = DateTime.UtcNow;
    public                          DateTime    UpdatedAt   { get; set; } = DateTime.UtcNow;
    [MaxLength(length: 255)] public string      Name        { get; set; } = string.Empty;
    [MaxLength(length: 255)] public string      Description { get; set; } = string.Empty;
    public                          Guid        UserId      { get; set; } = Guid.Empty;
    public                          ChannelType ChannelType { get; set; } = ChannelType.Text;
    public                          ServerRole  AccessLevel { get; set; } = ServerRole.User;
    public                          Guid        ServerId    { get; set; } = Guid.Empty;

    public static implicit operator ChannelDto(Channel channel)
        => new(
               Id: channel.Id,
               CreatedAt: channel.CreatedAt,
               UpdatedAt: channel.UpdatedAt,
               Name: channel.Name,
               Description: channel.Description,
               UserId: channel.UserId,
               ChannelType: channel.ChannelType,
               AccessLevel: channel.AccessLevel,
               ServerId: channel.ServerId
              );
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(alias: nameof(ChannelDto))]
public sealed partial record ChannelDto(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Id(id: 0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Id(id: 1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Id(id: 2)]
    DateTime UpdatedAt,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Id(id: 3)]
    string Name,
    [property: DataMember(Order = 4), MemoryPackOrder(order: 4), Id(id: 4)]
    string Description,
    [property: DataMember(Order = 5), MemoryPackOrder(order: 5), Id(id: 5)]
    Guid UserId,
    [property: DataMember(Order = 6), MemoryPackOrder(order: 6), Id(id: 6)]
    ChannelType ChannelType,
    [property: DataMember(Order = 7), MemoryPackOrder(order: 7), Id(id: 7)]
    ServerRole AccessLevel,
    [property: DataMember(Order = 8), MemoryPackOrder(order: 8), Id(id: 8)]
    Guid ServerId
)
{
    [property: DataMember(Order = 9), MemoryPackOrder(order: 9), Id(id: 9)]
    public List<UsersToServerRelationDto> ConnectedUsers { get; set; } = [];
}