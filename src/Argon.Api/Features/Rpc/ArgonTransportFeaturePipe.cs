namespace Argon.Services;

using System.Net.WebSockets;
using Microsoft.AspNetCore.Connections;

public sealed class ArgonTransportFeaturePipe : IDisposable, IAsyncDisposable
{
    private WebSocket?         _webSocket;
    private ConnectionContext? _webTransport;

    private WebSocketCancellationTokenSource _wsCt;

    public bool IsWebSocket    => _webSocket is not null;
    public bool IsWebTransport => _webTransport is not null;

    public WebSocket         WebSocket    => _webSocket!;
    public ConnectionContext WebTransport => _webTransport!;

    public Guid ConnectionId { get; } = Guid.NewGuid();

    public CancellationToken ConnectionClosed
    {
        get
        {
            if (IsWebTransport)
                return _webTransport!.ConnectionClosed;
            if (IsWebSocket)
                return _wsCt.Token;
            throw new InvalidOperationException();
        }
    }

    public static ArgonTransportFeaturePipe CreateForWt(ConnectionContext webTransport) => new()
    {
        _webTransport = webTransport
    };

    public static ArgonTransportFeaturePipe CreateForWs(WebSocket webSocket) => new()
    {
        _webSocket = webSocket,
        _wsCt      = new WebSocketCancellationTokenSource(webSocket)
    };


    public async ValueTask FlushAsync()
    {
        if (IsWebTransport)
            await _webTransport!.Transport.Output.FlushAsync(ConnectionClosed);
    }

    public async ValueTask WriteAsync(byte[] arr)
    {
        if (IsWebTransport)
            await _webTransport!.Transport.Output.WriteAsync(arr, ConnectionClosed);
        else if (IsWebSocket)
            await _webSocket!.SendAsync(arr, WebSocketMessageType.Binary, WebSocketMessageFlags.EndOfMessage, ConnectionClosed);
        else
            throw new InvalidOperationException();
    }

    public void Abort(ConnectionAbortedException exception)
    {
        if (IsWebSocket)
            _webSocket!.Abort();
        else if (IsWebTransport)
            _webTransport!.Abort(exception);
        throw new InvalidOperationException();
    }

    public void Dispose()
    {
        _webSocket?.Dispose();
        if (_webTransport is IDisposable webTransportDisposable)
            webTransportDisposable.Dispose();
        else if (_webTransport != null)
            _ = _webTransport.DisposeAsync().AsTask();
        ((IDisposable)_wsCt).Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_webSocket is IAsyncDisposable webSocketAsyncDisposable)
            await webSocketAsyncDisposable.DisposeAsync();
        else if (_webSocket != null)
            _webSocket.Dispose();
        if (_webTransport != null) await _webTransport.DisposeAsync();
        await ((IAsyncDisposable)_wsCt).DisposeAsync();
    }
}