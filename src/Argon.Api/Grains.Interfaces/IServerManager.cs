namespace Argon.Api.Grains.Interfaces;

using Persistence.States;
using Sfu;

[Alias("Argon.Api.Grains.Interfaces.IServerManager")]
public interface IServerManager : IGrainWithGuidKey
{
    [Alias("CreateServer")]
    Task<ServerStorage> CreateServer(string name, string description, Guid userId);

    [Alias("CreateJoinLink")]
    Task<string> CreateJoinLink();

    [Alias("AddUser")]
    Task AddUser(UserToServerRelation Relation);

    [Alias("GetChannels")]
    Task<IEnumerable<ChannelStorage>> GetChannels();

    [Alias("AddChannel")]
    Task<ChannelStorage> AddChannel(ChannelStorage channel);

    [Alias("GetChannel")]
    Task<ChannelStorage> GetChannel(Guid channelId);

    [Alias("GetServer")]
    Task<ServerStorage> GetServer();

    [Alias("JoinChannel")]
    Task<RealtimeToken> JoinChannel(Guid userId, Guid channelId);
}