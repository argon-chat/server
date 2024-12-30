namespace Argon.Services;

using Features.Rpc;

public class EventBusService(IClusterClient clusterClient) : IEventBus
{
    public async Task<IArgonStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId)
        => await clusterClient.Streams().CreateClientStream(ServerId);

    public async Task<IArgonStream<IArgonEvent>> SubscribeToMeEvents()
    {
        var user = this.GetUser();
        return await clusterClient.Streams().CreateClientStream(user.id);
    }
}