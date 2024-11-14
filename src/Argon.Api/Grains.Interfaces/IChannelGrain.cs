namespace Argon.Api.Grains.Interfaces;

using Contracts;
using Contracts.etc;
using Entities;
using RealtimeToken = Features.Sfu.RealtimeToken;

public interface IChannelGrain : IGrainWithGuidKey
{
    // for join\leave\mute\unmute notifications
    public const string UserTransformNotificationStream  = $"{nameof(IChannelGrain)}.user.transform";
    public const string ChannelMessageNotificationStream = $"{nameof(IChannelGrain)}.user.messages";

    [Alias("Join")]
    Task<Maybe<RealtimeToken>> Join(Guid userId);

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