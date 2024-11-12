namespace Argon.Api.Services;

using ActualLab.Rpc;
using Contracts;
using Features.Rpc;

public class EventBusService(IClusterClient clusterClient) : IEventBus
{
    public async ValueTask<RpcStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId)
    {
        var stream = await clusterClient.Streams().CreateClientStream(ServerId);
        return stream.AsRpcStream();
    }
}