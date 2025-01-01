namespace Argon.Services;

using Features.Rpc;

public class EventBusService(IClusterClient clusterClient) : IEventBus
{
    public async Task<IArgonStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId)
        => await clusterClient.Streams().CreateClientStream(ServerId);

    public async Task<IArgonStream<IArgonEvent>> SubscribeToMeEvents()
    {
        var user      = this.GetUser();
        var sessionId = Guid.NewGuid();

        await clusterClient.GetGrain<IFusionSessionGrain>(sessionId)
           .BeginRealtimeSession(user.id, user.machineId, UserStatus.Online);

        ArgonTransportContext.Current.SubscribeToDisconnect(
            async () => await clusterClient.GetGrain<IFusionSessionGrain>(sessionId).EndRealtimeSession());

        return await clusterClient.Streams().CreateClientStream(user.id);
    }
}