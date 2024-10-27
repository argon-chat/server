namespace Argon.Api.Grains.Interfaces;

using Persistence.States;
using Sfu;

[Alias("Argon.Api.Grains.Interfaces.IChannelManager")]
public interface IChannelManager : IGrainWithGuidKey
{
    [Alias("CreateChannel")]
    Task<ChannelStorage> CreateChannel(ChannelStorage channel);

    [Alias("GetChannel")]
    Task<ChannelStorage> GetChannel();

    [Alias("JoinLink")]
    Task<RealtimeToken> JoinLink(Guid userId, Guid serverId);

    [Alias("UpdateChannel")]
    Task<ChannelStorage> UpdateChannel(ChannelStorage channel);
}