namespace Models.DTO;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;
using Orleans;

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(nameof(UserCredentialsInput))]
public sealed record UserCredentialsInput(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    string Email,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    string? Username,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2), Id(2)]
    string? PhoneNumber,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Key(3), Id(3)]
    string? Password,
    [property: DataMember(Order = 4), MemoryPackOrder(4), Key(4), Id(4)]
    string? OtpCode);