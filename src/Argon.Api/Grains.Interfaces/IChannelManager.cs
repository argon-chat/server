namespace Argon.Api.Grains.Interfaces;

using Entities;
using Sfu;

public interface IChannelManager : IGrainWithGuidKey
{
    [Alias("Join")]
    Task<RealtimeToken> Join(Guid userId);

    [Alias("Leave")]
    Task Leave(Guid userId);

    [Alias("GetChannel")]
    Task<ChannelDto> GetChannel();
}