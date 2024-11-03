namespace Argon.Api.Grains.Interfaces;

using Sfu;

public interface IChannelManager : IGrainWithGuidKey
{
    [Alias("CreateChannel")]
    Task CreateChannel(object channel);

    [Alias("GetChannel")]
    Task GetChannel();

    [Alias("JoinLink")]
    Task<RealtimeToken> JoinLink(Guid userId, Guid serverId);

    [Alias("UpdateChannel")]
    Task UpdateChannel(object channel);
}