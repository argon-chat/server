namespace Argon.Services.Ion;

using Api.Features.Bus;

public class EventBusImpl : IEventBus
{
    public async IAsyncEnumerable<IArgonEvent> ForServer(Guid spaceId)
    {
        await foreach (var e in await this.GetClusterClient().Streams().CreateClientStream(spaceId))
            yield return e;
    }

    public async IAsyncEnumerable<IArgonEvent> ForSelf()
    {
        var client = this.GetClusterClient();

        await client.GetGrain<IUserSessionGrain>(this.GetSessionId()).BeginRealtimeSession();

        await foreach (var e in await client.Streams().CreateClientStream(this.GetUserId()))
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


//public class AsyncStreamMux<T> : IAsyncDisposable
//{
//    private readonly Channel<T> _channel = Channel.CreateUnbounded<T>();
//    private readonly ConcurrentDictionary<IAsyncEnumerable<T>, CancellationTokenSource> _subscriptions = new();
//    private readonly CancellationTokenSource _shutdown = new();

//    public async IAsyncEnumerable<T> GetStream([EnumeratorCancellation] CancellationToken cancellationToken = default)
//    {
//        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);

//        try
//        {
//            while (await _channel.Reader.WaitToReadAsync(linked.Token))
//            {
//                while (_channel.Reader.TryRead(out var item))
//                    yield return item;
//            }
//        }
//        finally
//        {
//            await DisposeAsync();
//        }
//    }

//    public void Subscribe(IAsyncEnumerable<T> source)
//    {
//        var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
//        if (_subscriptions.TryAdd(source, cts))
//        {
//            _ = Task.Run(async () => {
//                try
//                {
//                    await foreach (var item in source.WithCancellation(cts.Token))
//                        await _channel.Writer.WriteAsync(item, cts.Token);
//                }
//                catch (OperationCanceledException) { }
//                catch (Exception ex)
//                {
//                    Console.Error.WriteLine($"[Mux] Source error: {ex}");
//                }
//                finally
//                {
//                    _subscriptions.TryRemove(source, out _);
//                }
//            }, cts.Token);
//        }
//    }

//    public void Unsubscribe(IAsyncEnumerable<T> source)
//    {
//        if (_subscriptions.TryRemove(source, out var cts))
//            cts.Cancel();
//    }

//    public void Complete() => _channel.Writer.TryComplete();

//    public async ValueTask DisposeAsync()
//    {
//        await _shutdown.CancelAsync();
//        foreach (var (_, cts) in _subscriptions)
//            await cts.CancelAsync();

//        _subscriptions.Clear();
//        _channel.Writer.TryComplete();

//        await Task.Yield();
//    }
//}