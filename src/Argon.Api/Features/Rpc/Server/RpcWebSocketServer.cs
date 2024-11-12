namespace ActualLab.Rpc.Server;

using System.Net;
using System.Net.WebSockets;
using Argon.Api.Features.Jwt;
using Argon.Api.Grains.Interfaces;
using Clients;
using Collections;
using Infrastructure;
using WebSockets;
using Time;

public class RpcWebSocketServer(
    RpcWebSocketServer.Options settings,
    IServiceProvider services,
    IGrainFactory grainFactory,
    TokenAuthorization tokenAuthorization
    ) : RpcServiceBase(services)
{
    public record Options
    {
        public static Options Default { get; set; } = new();

        public bool ExposeBackend { get; init; } = false;
        public string RequestPath { get; init; } = RpcWebSocketClient.Options.Default.RequestPath;
        public string BackendRequestPath { get; init; } = RpcWebSocketClient.Options.Default.BackendRequestPath;
        public string SerializationFormatParameterName { get; init; } = RpcWebSocketClient.Options.Default.SerializationFormatParameterName;
        public string ClientIdParameterName { get; init; } = RpcWebSocketClient.Options.Default.ClientIdParameterName;
        public TimeSpan ChangeConnectionDelay { get; init; } = TimeSpan.FromSeconds(0.5);
        public Func<WebSocketAcceptContext> ConfigureWebSocket { get; init; } = () => new();
    }

    public Options Settings { get; } = settings;
    public RpcWebSocketServerPeerRefFactory PeerRefFactory { get; }
        = services.GetRequiredService<RpcWebSocketServerPeerRefFactory>();
    public RpcServerConnectionFactory ServerConnectionFactory { get; }
        = services.GetRequiredService<RpcServerConnectionFactory>();
    public RpcWebSocketChannelOptionsProvider WebSocketChannelOptionsProvider { get; }
        = services.GetRequiredService<RpcWebSocketChannelOptionsProvider>();

    public async Task Invoke(HttpContext context, bool isBackend)
    {
        var cancellationToken = context.RequestAborted;
        if (!context.WebSockets.IsWebSocketRequest) 
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            return;
        }
        if (!context.Request.Headers.TryGetValue(HttpRequestHeader.Authorization.ToString(), out var value))
        {
            context.Response.StatusCode = 403;
            return;
        }

        var validationResult = await tokenAuthorization.AuthorizeByToken(value.ToString());

        if (!validationResult.IsSuccess)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = validationResult.Error }, cancellationToken);
            return;
        }

        var userData = validationResult.Value;

        WebSocket?          webSocket  = null;
        RpcConnection?      connection = null;
        IFusionSessionGrain?fs         = null;
        try {
            var peerRef = PeerRefFactory.Invoke(this, context, isBackend).RequireServer();
            var peer    = Hub.GetServerPeer(peerRef);
            fs      = grainFactory.GetGrain<IFusionSessionGrain>(peer.Id);

            var webSocketAcceptContext = Settings.ConfigureWebSocket.Invoke();
            var acceptWebSocketTask    = context.WebSockets.AcceptWebSocketAsync(webSocketAcceptContext);
            webSocket = await acceptWebSocketTask.ConfigureAwait(false);
            var properties = PropertyBag.Empty
                .Set((RpcPeer)peer)
                .Set(context)
                .Set(webSocket);
            var webSocketOwner = new WebSocketOwner(peer.Ref.ToString(), webSocket, Services);
            var webSocketChannelOptions = WebSocketChannelOptionsProvider.Invoke(peer, properties);
            var channel = new WebSocketChannel<RpcMessage>(
                webSocketChannelOptions, webSocketOwner, cancellationToken) {
                OwnsWebSocketOwner = false,
            };
            connection = await ServerConnectionFactory
                .Invoke(peer, channel, properties, cancellationToken)
                .ConfigureAwait(false);

            if (peer.IsConnected()) {
                var delay = Settings.ChangeConnectionDelay;
                Log.LogWarning("{Peer} is already connected, will change its connection in {Delay}...",
                    peer, delay.ToShortString());
                await peer.Hub.Clock.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await fs.BeginRealtimeSession(userData.id, userData.machineId);
            }
            peer.SetConnection(connection);
            await channel.WhenClosed.ConfigureAwait(false);
        }
        catch (Exception e) {
            if (connection != null || e.IsCancellationOf(cancellationToken))
                return; // Intended: this is typically a normal connection termination

            var request = context.Request;
            Log.LogWarning(e, "Failed to accept RPC connection: {Path}{Query}", request.Path, request.QueryString);
            if (webSocket != null)
                return;

            try {
                context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            }
            catch {
                // Intended
            }
        }
        finally {
            webSocket?.Dispose();
            await (fs?.EndRealtimeSession() ?? ValueTask.CompletedTask);
        }
    }
}
