namespace Models.DTO;

using System.Runtime.Serialization;
using MemoryPack;
using MessagePack;
using Orleans;

[Serializable, GenerateSerializer, MemoryPackable, MessagePackObject, Alias(nameof(JwtToken))]
public partial record struct JwtToken(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    string token);