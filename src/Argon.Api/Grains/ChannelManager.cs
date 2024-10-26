namespace Argon.Api.Grains;

using Interfaces;
using Persistence.States;

public class ChannelManager : Grain, IChannelManager
{
    public async Task<ChannelStorage> CreateChannel(ChannelStorage channel)
    {
        throw new NotImplementedException();
    }

    public async Task<ChannelStorage> GetChannel()
    {
        throw new NotImplementedException();
    }

    public async Task<List<UserToServerRelation>> GetUsers()
    {
        throw new NotImplementedException();
    }

    public async Task<ChannelStorage> UpdateChannel(ChannelStorage channel)
    {
        throw new NotImplementedException();
    }
}