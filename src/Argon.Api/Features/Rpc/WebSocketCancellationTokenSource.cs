namespace Argon.Services;

using System.Net.WebSockets;
using R3;

public class WebSocketCancellationTokenSource : IDisposable, IAsyncDisposable
{
    private readonly WebSocket                       _webSocket;
    private readonly CancellationTokenSource         _cts = new();
    private readonly IDisposable                     _subscription;
    private readonly BehaviorSubject<WebSocketState> _stateSubject;

    public CancellationToken Token => _cts.Token;

    public WebSocketCancellationTokenSource(WebSocket webSocket)
    {
        _webSocket    = webSocket;
        _stateSubject = new BehaviorSubject<WebSocketState>(_webSocket.State);

        _subscription = Observable
           .Interval(TimeSpan.FromMilliseconds(500))
           .Select(_ => _webSocket.State)
           .DistinctUntilChanged()
           .Do(state => _stateSubject.OnNext(state))
           .Where(state => state is WebSocketState.Closed or WebSocketState.Aborted)
           .Subscribe(_ => _cts.Cancel());
    }

    public Observable<WebSocketState> StateChanged => _stateSubject.AsObservable();

    void IDisposable.Dispose()
    {
        _cts.Cancel();
        _subscription.Dispose();
        _stateSubject.Dispose();
        _cts.Dispose();
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        await CastAndDispose(_webSocket);
        await CastAndDispose(_cts);
        await CastAndDispose(_subscription);
        await CastAndDispose(_stateSubject);

        return;

        async static ValueTask CastAndDispose(IDisposable resource)
        {
            if (resource is IAsyncDisposable resourceAsyncDisposable)
                await resourceAsyncDisposable.DisposeAsync();
            else
                resource.Dispose();
        }
    }
}