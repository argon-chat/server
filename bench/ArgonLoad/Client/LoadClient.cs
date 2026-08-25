namespace Argon.Load.Client;

using ion.runtime;
using ion.runtime.client;
using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;

/// <summary>
/// One virtual user's connection to a running server, over the real wire.
/// </summary>
/// <remarks>
/// The generated Ion client is used as-is rather than reimplemented: the framing is CBOR over HTTP
/// with the session headers below, and a second implementation of it in a load tool would be a
/// second thing to keep in step with the contracts.
/// <para>
/// Each virtual user gets its own <see cref="HttpClient"/> and its own session and machine ids. A
/// shared handler would pool connections across users and make the server see one very busy device
/// instead of a crowd, which is the opposite of what a herd test is for.
/// </para>
/// </remarks>
public sealed class LoadClient : IDisposable
{
    private readonly HttpClient       http;
    private readonly HeaderInterceptor headers = new();
    private readonly IonClient        ion;
    private readonly IServiceProvider services;

    /// <summary>The space this client is acting in, set once a scenario has picked one.</summary>
    public Guid SpaceId { get; set; }

    public LoadClient(Uri target)
    {
        http = new HttpClient(new SocketsHttpHandler
        {
            // One connection per user, kept open: the client under test holds a session, so pooling
            // several users onto one connection would understate the server's connection cost.
            MaxConnectionsPerServer = 1,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        })
        {
            BaseAddress = target,
            Timeout     = TimeSpan.FromMinutes(2)
        };

        services = new ServiceCollection().BuildServiceProvider();

        ion = IonClient.Create(http, ConnectWebSocketAsync);
        ion.WithInterceptor(headers);
    }

    public void Authenticate(string token)
        => headers.SetToken(token);

    public T Service<T>() where T : class, IIonService
        => ion.ForService<T>(services);

    private async Task<WebSocket> ConnectWebSocketAsync(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = new ClientWebSocket();

        foreach (var protocol in protocols ?? [])
            socket.Options.AddSubProtocol(protocol);

        await socket.ConnectAsync(uri, ct);
        return socket;
    }

    public void Dispose()
        => http.Dispose();

    /// <summary>
    /// Stamps what the server expects of a session: a session id, a device id, and the bearer token
    /// once the user has one.
    /// </summary>
    private sealed class HeaderInterceptor : IIonInterceptor
    {
        private readonly Guid sessionId = Guid.CreateVersion7();
        private readonly Guid machineId = Guid.CreateVersion7();

        private volatile string? token;

        public void SetToken(string? value) => token = value;

        public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems.Add("Sec-Ref", sessionId.ToString());
            context.RequestItems.Add("Sec-Ner", "1");
            context.RequestItems.Add("Sec-Carry", machineId.ToString());

            if (!string.IsNullOrEmpty(token))
                context.RequestItems.Add("Authorization", $"Bearer {token}");

            await next(context, ct);
        }
    }
}
