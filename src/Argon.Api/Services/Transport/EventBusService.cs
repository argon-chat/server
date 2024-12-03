namespace Argon.Services;

using Features.Rpc;

public class EventBusService(IClusterClient clusterClient) : IEventBus
{
    public async ValueTask<IArgonStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId)
        => await clusterClient.Streams().CreateClientStream(ServerId);

    public async ValueTask<IArgonStream<IArgonEvent>> SubscribeToMeEvents()
    {
        var user = this.GetUser();
        return await clusterClient.Streams().CreateClientStream(user.id);
    }
}