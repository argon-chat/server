namespace Grains.Interface;

using Models;
using Models.DTO;
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