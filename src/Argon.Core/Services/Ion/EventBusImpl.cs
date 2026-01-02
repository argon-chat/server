namespace Argon.Services.Ion;

using System.Runtime.CompilerServices;
using Api.Features.Bus;

public class EventBusImpl(ILogger<IEventBus> logger) : IEventBus
{
    public async IAsyncEnumerable<IArgonEvent> ForServer(Guid spaceId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = this.GetClusterClient();
        await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).BeginRealtimeSession();

        await foreach (var e in await client.Streams().CreateClientStream(spaceId).ConfigureAwait(false))
            yield return e;
    }
        
    public async Task Dispatch(IArgonClientEvent ev, CancellationToken ct = default) => 
        await DispatchTree(ev, this.GetClusterClient(), this.GetSessionId(), ct);

    public async IAsyncEnumerable<IArgonEvent> Pipe(IAsyncEnumerable<IArgonClientEvent>? dispatchEvents, 
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sessionId = this.GetSessionId();
        var userId = this.GetUserId();
        var client = this.GetClusterClient();

        await client.GetGrain<IUserSessionGrain>(sessionId).BeginRealtimeSession();

        var masterStream = await client.Streams().GetOrCreateSubscriptionCoupler(sessionId, userId, ct);
        var subscriptionTask = SubscribeTyMySpaces(userId, sessionId, client, ct);

        await foreach (var ev in MergeStreams(masterStream, dispatchEvents, client, sessionId, ct).ConfigureAwait(false))
            yield return ev;

        await subscriptionTask;
    }

    private async static IAsyncEnumerable<IArgonEvent> MergeStreams(
        IAsyncEnumerable<IArgonEvent> serverEvents,
        IAsyncEnumerable<IArgonClientEvent>? clientEvents,
        IClusterClient client,
        Guid sessionId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var serverEnum = serverEvents.GetAsyncEnumerator(ct);
        var clientEnum = clientEvents?.GetAsyncEnumerator(ct);

        try
        {
            var serverTask = GetNextOrNull(serverEnum, ct);
            var clientTask = clientEnum != null ? ProcessNextClientEvent(clientEnum, client, sessionId, ct) : Task.FromResult(false);

            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(serverTask, clientTask);

                if (completed == serverTask)
                {
                    var serverEvent = await serverTask;
                    if (serverEvent == null) break;

                    yield return serverEvent;
                    serverTask = GetNextOrNull(serverEnum, ct);
                }
                else
                {
                    if (!await clientTask) break;
                    clientTask = ProcessNextClientEvent(clientEnum!, client, sessionId, ct);
                }
            }
        }
        finally
        {
            await DisposeEnumeratorSafely(serverEnum);
            if (clientEnum != null)
            {
                await DisposeEnumeratorSafely(clientEnum);
            }
        }
    }

    private async static ValueTask DisposeEnumeratorSafely<T>(IAsyncEnumerator<T> enumerator)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (NotSupportedException)
        {
            // Some stream implementations throw on dispose after cancellation
        }
    }

    private async static Task<IArgonEvent?> GetNextOrNull(IAsyncEnumerator<IArgonEvent> enumerator, CancellationToken ct)
    {
        try
        {
            return await enumerator.MoveNextAsync() ? enumerator.Current : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private async static Task<bool> ProcessNextClientEvent(
        IAsyncEnumerator<IArgonClientEvent> enumerator,
        IClusterClient client,
        Guid sessionId,
        CancellationToken ct)
    {
        try
        {
            if (!await enumerator.MoveNextAsync()) return false;
            await DispatchTree(enumerator.Current, client, sessionId, ct);
            return true;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private async ValueTask SubscribeTyMySpaces(Guid userId, Guid sessionId, IClusterClient client, CancellationToken ct = default)
    {
        var spaceIds = await client.GetGrain<IUserGrain>(userId).GetMyServersIds(ct);
        var tasks = spaceIds.Select(spaceId => client.Streams().AssignSubscribe(sessionId, spaceId).AsTask());
        await Task.WhenAll(tasks);
    }

    private async static ValueTask DispatchTree(IArgonClientEvent ev, IClusterClient client, Guid sessionId, CancellationToken ct = default)
    {
        var sessionGrain = client.GetGrain<IUserSessionGrain>(sessionId);

        switch (ev)
        {
            case IAmTypingEvent typing:
                await sessionGrain.OnTypingEmit(typing.channelId);
                break;
            case IAmStopTypingEvent stopTyping:
                await sessionGrain.OnTypingStopEmit(stopTyping.channelId);
                break;
            case HeartBeatEvent heartbeat:
                if (!await sessionGrain.HeartBeatAsync(heartbeat.status))
                    throw new InvalidOperationException("Session expired, dropping connection");
                break;
            case SubscribeToMySpaces:
                break;
            default:
                return;
        }
    }
}