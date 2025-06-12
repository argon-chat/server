namespace Argon.Services;

using Argon.Api.Features.Bus;

public class EventBusService : IEventBus
{
    public async Task<IArgonStream<IArgonEvent>> SubscribeToServerEvents(Guid ServerId)
        => await this.GetClusterClient().Streams().CreateClientStream(ServerId);

    public async Task<IArgonStream<IArgonEvent>> SubscribeToMeEvents()
        => throw null;
}