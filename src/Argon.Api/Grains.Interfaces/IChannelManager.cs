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

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
[Alias(nameof(ChannelInput))]
public sealed partial record ChannelInput(
    [property: DataMember(Order = 0)]
    [property: MemoryPackOrder(0)]
    [property: Key(0)]
    [property: Id(0)]
    string Name,
    [property: DataMember(Order = 1)]
    [property: MemoryPackOrder(1)]
    [property: Key(1)]
    [property: Id(1)]
    ServerRole AccessLevel,
    [property: DataMember(Order = 2)]
    [property: MemoryPackOrder(2)]
    [property: Key(2)]
    [property: Id(2)]
    string? Description,
    [property: DataMember(Order = 3)]
    [property: MemoryPackOrder(3)]
    [property: Key(3)]
    [property: Id(3)]
    ChannelType ChannelType
);