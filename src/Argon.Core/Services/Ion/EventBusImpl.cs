namespace Argon.Services.Ion;

using System.Runtime.CompilerServices;
using Api.Features.Bus;

public class EventBusImpl(ILogger<IEventBus> logger) : IEventBus
{
    public async IAsyncEnumerable<IArgonEvent> ForServer(Guid spaceId, CancellationToken ct = default)
    {
        var client = this.GetClusterClient();

        await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).BeginRealtimeSession();

        await foreach (var e in await this.GetClusterClient().Streams().CreateClientStream(spaceId))
            yield return e;
    }

    public async Task Dispatch(IArgonClientEvent ev, CancellationToken ct = default) => await DispatchTree(ev);

    public async IAsyncEnumerable<IArgonEvent> Pipe(IAsyncEnumerable<IArgonClientEvent>? dispatchEvents, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sessionId = this.GetSessionId();
        var userId    = this.GetUserId();
        var client    = this.GetClusterClient();

        await client.GetGrain<IUserSessionGrain>(sessionId).BeginRealtimeSession();

        var masterStream = await client.Streams()
           .GetOrCreateSubscriptionCoupler(sessionId, userId, ct);

        await using var masterEnum = masterStream.GetAsyncEnumerator(ct);
        await using var clientEnum = dispatchEvents?.GetAsyncEnumerator(ct);

        var mvSpace  = masterEnum.MoveNextAsync().AsTask();
        var mvClient = clientEnum != null ? clientEnum.MoveNextAsync().AsTask() : Task.FromResult(false);
        var subs     = SubscribeTyMySpaces(userId, sessionId, client, ct);

        while (!ct.IsCancellationRequested)
        {
            var finished = await Task.WhenAny(mvSpace, mvClient);

            if (finished == mvSpace)
            {
                if (!mvSpace.Result) break;
                yield return masterEnum.Current;
                mvSpace = masterEnum.MoveNextAsync().AsTask();
            }
            else
            {
                if (!mvClient.Result) break;
                await DispatchTree(clientEnum!.Current, client);
                mvClient = clientEnum.MoveNextAsync().AsTask();
            }
        }

        await subs;
    }

    private async ValueTask SubscribeTyMySpaces(Guid userId, Guid sessionId, IClusterClient client, CancellationToken ct = default)
    {
        var spaceIds = await client.GetGrain<IUserGrain>(userId).GetMyServersIds(ct);
        foreach (var spaceId in spaceIds)
            await client.Streams().AssignSubscribe(sessionId, spaceId);
    }

    private async ValueTask DispatchTree(IArgonClientEvent ev, IClusterClient client)
    {
        switch (ev)
        {
            case IAmTypingEvent typing:
                await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).OnTypingEmit(typing.channelId);
                break;
            case IAmStopTypingEvent stopTyping:
                await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).OnTypingStopEmit(stopTyping.channelId);
                break;
            case HeartBeatEvent heartbeat:
                if (!await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).HeartBeatAsync(heartbeat.status))
                    throw new Exception("drop connection when session expired");
                break;
            case SubscribeToMySpaces:

            default:
                return;
        }
    }
}