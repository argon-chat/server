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

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias("Argon.Api.Entities.Channel")]
public sealed partial record Channel
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

    [MaxLength(255)]
    [Id(3)]
    [MemoryPackOrder(3)]
    [DataMember(Order = 3)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(255)]
    [Id(4)]
    [MemoryPackOrder(4)]
    [DataMember(Order = 4)]
    public string Description { get; set; } = string.Empty;

    [Id(5)]
    [MemoryPackOrder(5)]
    [DataMember(Order = 5)]
    public Guid UserId { get; set; } = Guid.Empty;

    [Id(6)]
    [MemoryPackOrder(6)]
    [DataMember(Order = 6)]
    public ChannelType ChannelType { get; set; } = ChannelType.Text;

    [Id(7)]
    [MemoryPackOrder(7)]
    [DataMember(Order = 7)]
    public ServerRole AccessLevel { get; set; } = ServerRole.User;

    [Id(8)]
    [MemoryPackOrder(8)]
    [DataMember(Order = 8)]
    public Guid ChannelId { get; set; } = Guid.Empty;
}