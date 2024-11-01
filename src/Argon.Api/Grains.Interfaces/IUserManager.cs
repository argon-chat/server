namespace Argon.Api.Grains.Interfaces;

using Persistence.States;
using Sfu;

public interface IUserManager : IGrainWithGuidKey
{
    [Alias("CreateServer")]
    Task<ServerStorage> CreateServer(string name, string description);

    [Alias("GetServers")]
    Task<List<ServerStorage>> GetServers();

    [Alias("GetServerChannels")]
    Task<IEnumerable<ChannelStorage>> GetServerChannels(Guid serverId);

    [Alias("GetChannel")]
    Task<ChannelStorage> GetChannel(Guid serverId, Guid channelId);

    [Alias("JoinChannel")]
    Task<RealtimeToken> JoinChannel(Guid serverId, Guid channelId);
}