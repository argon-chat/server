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

    private async ValueTask DispatchTree(IArgonClientEvent ev)
    {
        switch (ev)
        {
            case IAmTypingEvent typing:
                await this.GetGrain<IUserSessionGrain>(this.GetSessionId()).OnTypingEmit(typing.channelId);
                break;
            case IAmStopTypingEvent stopTyping:
                await this.GetGrain<IUserSessionGrain>(this.GetSessionId()).OnTypingStopEmit(stopTyping.channelId);
                break;
            case HeartBeatEvent heartbeat:
                if (!await this.GetGrain<IUserSessionGrain>(this.GetSessionId()).HeartBeatAsync(heartbeat.status))
                    throw new Exception("drop connection when sesssion expired"); 
                break;
            default:
                return;
        }
    }
}