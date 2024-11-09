namespace Models.DTO;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;
using Orleans;

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ServerInput))]
public sealed partial record ServerInput(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    string? Description,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2), Id(2)]
    string? AvatarUrl);