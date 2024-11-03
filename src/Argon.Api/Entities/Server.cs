namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias("Argon.Api.Entities.Server")]
public sealed partial record Server
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

    [MaxLength(255)]
    [Id(5)]
    [MemoryPackOrder(5)]
    [DataMember(Order = 5)]
    public string AvatarUrl { get; set; } = string.Empty;
}