namespace Argon.Api.Services.Fusion;

using ActualLab.Rpc;
using Contracts;
using Features.Rpc;

public class EventBusService(IClusterClient clusterClient, IFusionContext fusionContext) : IEventBus
{
    public async ValueTask<RpcStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId)
    {
        var stream = await clusterClient.Streams().CreateClientStream(ServerId);
        return stream.AsRpcStream();
    }

    public async ValueTask<RpcStream<IArgonEvent>> SubscribeToMeEvents()
    {
        var user   = await fusionContext.GetUserDataAsync();
        var stream = await clusterClient.Streams().CreateClientStream(user.id);
        return stream.AsRpcStream();
    }
}

