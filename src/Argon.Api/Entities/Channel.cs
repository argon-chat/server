namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using Contracts;

public sealed record Channel
{
    public Guid        Id          { get; init; } = Guid.NewGuid();
    public DateTime    CreatedAt   { get; init; } = DateTime.UtcNow;
    public DateTime    UpdatedAt   { get; set; }  = DateTime.UtcNow;
    public Guid        UserId      { get; set; }  = Guid.Empty;
    public ChannelType ChannelType { get; set; }  = ChannelType.Text;
    public ServerRole  AccessLevel { get; set; }  = ServerRole.User;
    public Guid        ServerId    { get; set; }  = Guid.Empty;
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(255)]
    public string Description { get; set; } = string.Empty;
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ChannelDto))]
public sealed partial record ChannelDto(
    Guid Id,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Name,
    string Description,
    Guid UserId,
    ChannelType ChannelType,
    ServerRole AccessLevel,
    Guid ServerId)
{
    [property: DataMember(Order = 9), MemoryPackOrder(9), Id(9)]
    public Dictionary<Guid, UsersToServerRelationDto> ConnectedUsers { get; set; } = [];
    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Guid Id { get; set; } = Id;
    [DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    public DateTime CreatedAt { get; set; } = CreatedAt;
    [DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    public DateTime UpdatedAt { get; set; } = UpdatedAt;
    [DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    public string Name { get; set; } = Name;
    [DataMember(Order = 4), MemoryPackOrder(4), Id(4)]
    public string Description { get; set; } = Description;
    [DataMember(Order = 5), MemoryPackOrder(5), Id(5)]
    public Guid UserId { get; set; } = UserId;
    [DataMember(Order = 6), MemoryPackOrder(6), Id(6)]
    public ChannelType ChannelType { get; set; } = ChannelType;
    [DataMember(Order = 7), MemoryPackOrder(7), Id(7)]
    public ServerRole AccessLevel { get; set; } = AccessLevel;
    [DataMember(Order = 8), MemoryPackOrder(8), Id(8)]
    public Guid ServerId { get; set; } = ServerId;
}