namespace Argon.Api.Services;

using ActualLab.Rpc;
using Contracts;
using Orleans.Streams;
using System.Threading.Channels;
using Features.Rpc;
using Argon.Api.Grains.Interfaces;

public class EventBusService(IClusterClient clusterClient) : IEventBus
{
    public async ValueTask<RpcStream<ServerEvent>> SubscribeToServerEvents(Guid ServerId)
    {
        ArgonStream<ServerEvent>.
        return clusterClient.GetStreamProvider(IServerGrain.ProviderId).GetStream<ServerEvent>(new StreamId())
    }
}

