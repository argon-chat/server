namespace Argon.Api.Grains.Interfaces;

using System.Runtime.Serialization;
using Entities;
using MemoryPack;
using MessagePack;
using Sfu;

public interface IChannelManager : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<RealtimeToken> Join(Guid userId);

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("GetChannel")]
    Task<ChannelDto> GetChannel();

    [Alias("UpdateChannel")]
    Task<ChannelDto> UpdateChannel(ChannelInput input);
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ChannelInput))]
public sealed partial record ChannelInput(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    ServerRole AccessLevel,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2), Id(2)]
    string? Description,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Key(3), Id(3)]
    ChannelType ChannelType);