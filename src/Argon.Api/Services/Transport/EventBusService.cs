namespace Argon.Services;

using Features.Rpc;

public class EventBusService : IEventBus
{
    public async Task<IArgonStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId)
        => await this.GetClusterClient().Streams().CreateClientStream(ServerId);

    public async Task<IArgonStream<IArgonEvent>> SubscribeToMeEvents()
    {
        var user   = this.GetUser();
        var client = this.GetClusterClient();

        await client.GetGrain<IUserSessionGrain>(Guid.NewGuid())
           .BeginRealtimeSession(user.id, user.machineId, UserStatus.Online);

        //ArgonTransportContext.Current.SubscribeToDisconnect(
        //    async () => await clusterClient.GetGrain<IFusionSessionGrain>(user.machineId).EndRealtimeSession());

        return await client.Streams().CreateClientStream(user.id);
    }
}