namespace Argon.Services.Ion;

using Api.Features.Bus;

public class EventBusImpl(ILogger<IEventBus> logger) : IEventBus
{
    public async IAsyncEnumerable<IArgonEvent> ForServer(Guid spaceId)
    {
        var client = this.GetClusterClient();

        await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).BeginRealtimeSession();
        
        await foreach (var e in await this.GetClusterClient().Streams().CreateClientStream(spaceId))
            yield return e;
    }

    public async Task Dispatch(IArgonClientEvent ev) => await DispatchTree(ev);

    private ValueTask DispatchTree(IArgonClientEvent ev)
        => ev switch
        {
            IAmTypingEvent typing         => this.GetGrain<IUserSessionGrain>(this.GetSessionId()).OnTypingEmit(typing.channelId),
            IAmStopTypingEvent stopTyping => this.GetGrain<IUserSessionGrain>(this.GetSessionId()).OnTypingStopEmit(stopTyping.channelId),
            HeartBeatEvent heartbeat      => this.GetGrain<IUserSessionGrain>(this.GetSessionId()).HeartBeatAsync(heartbeat.status),
            _                             => ValueTask.CompletedTask
        };
}