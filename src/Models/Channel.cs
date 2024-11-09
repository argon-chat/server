namespace Models;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;
using Orleans;

public enum ChannelType : ushort
{
    Text,
    Voice,
    Announcement
}

public sealed record Channel
{
    public Guid     Id        { get; init; } = Guid.NewGuid();
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }  = DateTime.UtcNow;
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(255)]
    public string Description { get;      set; } = string.Empty;
    public Guid        UserId      { get; set; } = Guid.Empty;
    public ChannelType ChannelType { get; set; } = ChannelType.Text;
    public ServerRole  AccessLevel { get; set; } = ServerRole.User;
    public Guid        ServerId    { get; set; } = Guid.Empty;

    public static implicit operator ChannelDto(Channel channel) => new(channel.Id, channel.CreatedAt, channel.UpdatedAt, channel.Name,
        channel.Description, channel.UserId, channel.ChannelType, channel.AccessLevel, channel.ServerId);
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ChannelDto))]
public sealed partial record ChannelDto(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    Guid Id,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    DateTime CreatedAt,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    DateTime UpdatedAt,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    string Name,
    [property: DataMember(Order = 4), MemoryPackOrder(4), Id(4)]
    string Description,
    [property: DataMember(Order = 5), MemoryPackOrder(5), Id(5)]
    Guid UserId,
    [property: DataMember(Order = 6), MemoryPackOrder(6), Id(6)]
    ChannelType ChannelType,
    [property: DataMember(Order = 7), MemoryPackOrder(7), Id(7)]
    ServerRole AccessLevel,
    [property: DataMember(Order = 8), MemoryPackOrder(8), Id(8)]
    Guid ServerId)
{
    [property: DataMember(Order = 9), MemoryPackOrder(9), Id(9)]
    public List<UsersToServerRelationDto> ConnectedUsers { get; set; } = [];
}