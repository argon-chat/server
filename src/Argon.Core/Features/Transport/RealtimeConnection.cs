namespace Argon.Core.Features.Transport;

using ion.runtime.network;
using Microsoft.AspNetCore.SignalR;

/// <summary>
/// A live realtime connection, whichever transport carries it — what <see cref="HubConnectionRegistry"/>
/// needs to end one.
/// </summary>
public interface IRealtimeConnection
{
    /// <summary>Tells the client why it is about to be closed. Best effort.</summary>
    Task SayGoodbyeAsync(string reason, CancellationToken ct);

    /// <summary>Closes the connection for good; the client is not invited back.</summary>
    void Close(string reason);
}

/// <summary>A connection to <see cref="AppHub"/>.</summary>
public sealed class SignalRRealtimeConnection(HubCallerContext context, IHubContext<AppHub> hub) : IRealtimeConnection
{
    public Task SayGoodbyeAsync(string reason, CancellationToken ct)
        => hub.Clients.Client(context.ConnectionId).SendAsync(HubConnectionRegistry.SessionRevokedMessage, reason, ct);

    public void Close(string reason) => context.Abort();
}

/// <summary>An <c>EventBus.Realtime</c> stream.</summary>
public sealed class IonRealtimeConnection(IIonStreamContext context) : IRealtimeConnection
{
    public async Task SayGoodbyeAsync(string reason, CancellationToken ct)
        => await context.SendAsync<IRealtimeFrame>(new SessionRevoked(reason), ct);

    // Graceful, so the goodbye queued ahead of it is flushed first.
    public void Close(string reason) => context.Close(reason, allowReconnect: false);
}
