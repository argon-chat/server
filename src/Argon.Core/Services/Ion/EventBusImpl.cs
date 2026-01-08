namespace Argon.Services.Ion;

using System.Runtime.CompilerServices;
using Api.Features.Bus;

public class EventBusImpl(ILogger<IEventBus> logger) : IEventBus
{
    public async IAsyncEnumerable<IArgonEvent> ForServer(Guid spaceId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var client = this.GetClusterClient();
        await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).BeginRealtimeSession();

        var stream = await client.Streams().CreateClientStream(spaceId);
        try
        {
            await foreach (var e in stream.WithCancellation(ct))
                yield return e;
        }
        finally
        {
            await stream.DisposeAsync();
        }
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
        var subscriptionTask = SubscribeToMySpacesAsync(userId, sessionId, client, ct);

        await foreach (var ev in MergeStreams(masterStream, dispatchEvents, client, sessionId, logger, ct))
            yield return ev;

        await subscriptionTask;
    }

    private static async IAsyncEnumerable<IArgonEvent> MergeStreams(
        IAsyncEnumerable<IArgonEvent> serverEvents,
        IAsyncEnumerable<IArgonClientEvent>? clientEvents,
        IClusterClient client,
        Guid sessionId,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var serverEnum = serverEvents.GetAsyncEnumerator(ct);
        var clientEnum = clientEvents?.GetAsyncEnumerator(ct);

        try
        {
            var serverTask = GetNextOrNullAsync(serverEnum);
            var clientTask = clientEnum != null 
                ? ProcessNextClientEventAsync(clientEnum, client, sessionId, logger) 
                : Task.FromResult(false);

            while (!ct.IsCancellationRequested)
            {
                var completed = await Task.WhenAny(serverTask, clientTask);

                if (completed == serverTask)
                {
                    var serverEvent = await serverTask;
                    if (serverEvent == null) break;

                    yield return serverEvent;
                    serverTask = GetNextOrNullAsync(serverEnum);
                }
                else
                {
                    if (!await clientTask) break;
                    clientTask = ProcessNextClientEventAsync(clientEnum!, client, sessionId, logger);
                }
            }
        }
        finally
        {
            await DisposeEnumeratorSafelyAsync(serverEnum, logger);
            if (clientEnum != null)
            {
                await DisposeEnumeratorSafelyAsync(clientEnum, logger);
            }
        }
    }

    private static async ValueTask DisposeEnumeratorSafelyAsync<T>(IAsyncEnumerator<T> enumerator, ILogger logger)
    {
        try
        {
            await enumerator.DisposeAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error disposing enumerator");
        }
    }

    private static async Task<IArgonEvent?> GetNextOrNullAsync(IAsyncEnumerator<IArgonEvent> enumerator)
    {
        try
        {
            return await enumerator.MoveNextAsync() ? enumerator.Current : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<bool> ProcessNextClientEventAsync(
        IAsyncEnumerator<IArgonClientEvent> enumerator,
        IClusterClient client,
        Guid sessionId,
        ILogger logger)
    {
        try
        {
            if (!await enumerator.MoveNextAsync()) 
                return false;
            
            await DispatchTree(enumerator.Current, client, sessionId);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing client event");
            return false;
        }
    }

    private static async ValueTask SubscribeToMySpacesAsync(Guid userId, Guid sessionId, IClusterClient client, CancellationToken ct = default)
    {
        var spaceIds = await client.GetGrain<IUserGrain>(userId).GetMyServersIds(ct);
        var tasks = spaceIds.Select(spaceId => client.Streams().AssignSubscribe(sessionId, spaceId).AsTask());
        await Task.WhenAll(tasks);
    }

    private static async ValueTask DispatchTree(IArgonClientEvent ev, IClusterClient client, Guid sessionId, CancellationToken ct = default)
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
        }
    }
}