namespace Argon.Api.Grains.Interfaces;

using Persistence.States;

public interface IChannelManager : IGrainWithGuidKey
{
    [Alias("CreateChannel")]
    Task<ChannelStorage> CreateChannel(ChannelStorage channel);

    [Alias("GetChannel")]
    Task<ChannelStorage> GetChannel();

    [Alias("GetUsers")]
    Task<List<UserToServerRelation>> GetUsers();

    [Alias("UpdateChannel")]
    Task<ChannelStorage> UpdateChannel(ChannelStorage channel);
}