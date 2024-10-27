namespace Argon.Api.Grains;

using Interfaces;
using Persistence.States;

public class ChannelManager(
    [PersistentState("channels", "OrleansStorage")]
    IPersistentState<ChannelStorage> channelStore,
    IGrainFactory grainFactory
) : Grain, IChannelManager
{
    public async Task<ChannelStorage> CreateChannel(ChannelStorage channel)
    {
        if (channelStore.State.Id != Guid.Empty) throw new Exception("Channel already exists");

        channelStore.State = channel;
        await channelStore.WriteStateAsync();
        return await GetChannel();
    }

    public async Task<ChannelStorage> GetChannel()
    {
        await channelStore.ReadStateAsync();
        return channelStore.State;
    }

    public async Task<List<UserToServerRelation>> GetUsers()
    {
        throw new NotImplementedException();
    }

    public async Task<ChannelStorage> UpdateChannel(ChannelStorage channel)
    {
        channelStore.State = channel;
        await channelStore.WriteStateAsync();
        return await GetChannel();
    }
}