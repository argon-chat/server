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
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    [MaxLength(255)] public string Name { get; set; } = string.Empty;
    [MaxLength(255)] public string Description { get; set; } = string.Empty;
    public Guid UserId { get; set; } = Guid.Empty;
    public ChannelType ChannelType { get; set; } = ChannelType.Text;
    public ServerRole AccessLevel { get; set; } = ServerRole.User;
    public Guid ServerId { get; set; } = Guid.Empty;

    public static implicit operator ChannelDto(Channel channel)
    {
        return new ChannelDto(
            channel.Id,
            channel.CreatedAt,
            channel.UpdatedAt,
            channel.Name,
            channel.Description,
            channel.UserId,
            channel.ChannelType,
            channel.AccessLevel,
            channel.ServerId
        );
    }
}

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(ChannelDto))]
public sealed partial record ChannelDto(
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
    string Description,
    [property: DataMember(Order = 5)]
    [property: MemoryPackOrder(5)]
    [property: Id(5)]
    Guid UserId,
    [property: DataMember(Order = 6)]
    [property: MemoryPackOrder(6)]
    [property: Id(6)]
    ChannelType ChannelType,
    [property: DataMember(Order = 7)]
    [property: MemoryPackOrder(7)]
    [property: Id(7)]
    ServerRole AccessLevel,
    [property: DataMember(Order = 8)]
    [property: MemoryPackOrder(8)]
    [property: Id(8)]
    Guid ServerId
);