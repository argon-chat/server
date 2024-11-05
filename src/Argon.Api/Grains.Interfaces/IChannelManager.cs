namespace Argon.Api.Grains.Interfaces;

using System.Runtime.Serialization;
using Entities;
using MemoryPack;
using MessagePack;
using Sfu;

public interface IChannelManager : IGrainWithGuidKey
{
    [Alias(alias: "Join")]
    Task<RealtimeToken> Join(Guid userId);

    [Alias(alias: "Leave")]
    Task Leave(Guid userId);

    [Alias(alias: "GetChannel")]
    Task<ChannelDto> GetChannel();

    [Alias(alias: "UpdateChannel")]
    Task<ChannelDto> UpdateChannel(ChannelInput input);
}

[DataContract, MemoryPackable(generateType: GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(alias: nameof(ChannelInput))]
public sealed partial record ChannelInput(
    [property: DataMember(Order = 0), MemoryPackOrder(order: 0), Key(x: 0), Id(id: 0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(order: 1), Key(x: 1), Id(id: 1)]
    ServerRole AccessLevel,
    [property: DataMember(Order = 2), MemoryPackOrder(order: 2), Key(x: 2), Id(id: 2)]
    string? Description,
    [property: DataMember(Order = 3), MemoryPackOrder(order: 3), Key(x: 3), Id(id: 3)]
    ChannelType ChannelType
);