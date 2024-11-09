namespace Grains.Interface;

using System.Runtime.Serialization;
using DataTypes;
using MemoryPack;
using MessagePack;
using Models;
using Orleans;

public interface IChannelManager : IGrainWithGuidKey
{
    [Alias(nameof(Join))]
    Task<RealtimeToken> Join(Guid userId);

    [Alias(nameof(Leave))]
    Task Leave(Guid userId);

    [Alias(nameof(GetChannel))]
    Task<ChannelDto> GetChannel();

    [Alias(nameof(UpdateChannel))]
    Task<ChannelDto> UpdateChannel(ChannelInput input);
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer, Alias(nameof(ChannelInput))]
public sealed record ChannelInput(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Key(0), Id(0)]
    string Name,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Key(1), Id(1)]
    ServerRole AccessLevel,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Key(2), Id(2)]
    string? Description,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Key(3), Id(3)]
    ChannelType ChannelType);