namespace ArgonComplexTest.Infrastructure.Presence;

using Argon.Core.Features.Transport;
using ArgonContracts;
using ion.runtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using System.Formats.Cbor;
using System.Net.WebSockets;
using System.Text;

/// <summary>Which of the hub's three client methods carried an event.</summary>
/// <remarks>
/// The three are not interchangeable and a presence test usually cares which one it got: a status
/// that reaches a space group is what the members of that space see, while the same status on
/// <c>forSelf</c> is the friends channel and carries <c>spaceId == Guid.Empty</c>. Recording the
/// channel is what lets a test tell "the space was told" from "only I was told".
/// </remarks>
public enum RealtimeStream
{
    /// <summary>The user's personal stream — <c>AppHubServer.ForUser</c>.</summary>
    ForSelf,

    /// <summary>A space group — <c>AppHubServer.BroadcastSpace</c>.</summary>
    BroadcastSpace,

    /// <summary>A channel group — <c>AppHubServer.BroadcastChannel</c>.</summary>
    BroadcastChannel
}

/// <summary>One decoded event as it arrived at one client, with everything the transport said about it.</summary>
/// <param name="ReceivedAt">
/// When this process saw it. Stamped before decoding, so an ordering assertion measures the server
/// and not the CBOR reader.
/// </param>
/// <param name="Stream">Which hub client method delivered it.</param>
/// <param name="SpaceId">
/// The space the fan-out addressed, or <see cref="Guid.Empty"/> for <see cref="RealtimeStream.ForSelf"/>
/// and <see cref="RealtimeStream.BroadcastChannel"/> (the hub does not name a space on either).
/// </param>
/// <param name="ChannelId">The channel the fan-out addressed, or <see cref="Guid.Empty"/>.</param>
/// <param name="EntryId">The replay cursor the server minted, where the stream has one.</param>
/// <param name="Event">The decoded event.</param>
public sealed record RecordedEvent(
    DateTimeOffset ReceivedAt,
    RealtimeStream Stream,
    Guid SpaceId,
    Guid ChannelId,
    string? EntryId,
    IArgonEvent Event)
{
    /// <summary>A one-line form for failure messages.</summary>
    public override string ToString()
        => $"{ReceivedAt:HH:mm:ss.fff} {Stream}" +
           (SpaceId == Guid.Empty ? "" : $" space={SpaceId}") +
           (ChannelId == Guid.Empty ? "" : $" channel={ChannelId}") +
           $" {Event}";
}

/// <summary>Which transport a <see cref="RealtimeClient"/> talks to the hub over.</summary>
public enum RealtimeTransport
{
    /// <summary>
    /// The transport the desktop client uses, and the only one this harness can drop ungracefully —
    /// see <see cref="RealtimeClient.AbortAsync"/>.
    /// </summary>
    WebSockets,

    /// <summary>
    /// The documented fallback. Everything works except <see cref="RealtimeClient.AbortAsync"/>,
    /// which has no socket to kill.
    /// </summary>
    LongPolling
}

/// <summary>Thrown by <see cref="RealtimeClient.WaitForAsync{TEvent}"/> when nothing matched in time.</summary>
/// <remarks>
/// Carries the whole recorded stream in its message. A presence failure is almost always "the wrong
/// event arrived" rather than "nothing arrived", and the difference is invisible from a bare timeout.
/// </remarks>
public sealed class RealtimeWaitTimeoutException(string message) : TimeoutException(message);

/// <summary>
/// One client's live connection to the <c>/w</c> hub, driven the way the desktop client drives it.
/// </summary>
/// <remarks>
/// <para>This is the half of the presence system that has no other way of being observed. Every
/// status change is a fan-out — <c>SpaceGrain.SetUserStatus</c> to a space group,
/// <c>UserGrain.PushFriendPresenceAsync</c> to a personal stream — and a test that only reads
/// <c>GetMemberPresence</c> afterwards cannot tell a broadcast that was never sent from one that was
/// sent twice, nor see the order they arrived in. The bugs this campaign is hunting live exactly
/// there: an Online the user never had, an Offline that should not have fired, a corrective event
/// that never came.</para>
///
/// <para>It runs over the in-memory <see cref="TestServer"/> rather than a socket on a port: the
/// suite already boots one Argon host per process through <see cref="ArgonTestEnvironment"/>, and
/// that host is not listening anywhere. <c>HttpMessageHandlerFactory</c> hands SignalR the test
/// server's handler for negotiation, and <c>WebSocketFactory</c> hands it the test server's
/// in-memory socket for the transport itself. Both halves are needed — negotiate is an ordinary HTTP
/// POST and the upgrade is not.</para>
///
/// <para>Authentication is the <c>Ticket</c> scheme, a JWT from <c>IEventBus.PickTicket</c> over the
/// session's own Ion client, so the ticket carries that client's <c>sid</c> and the hub resolves the
/// same session grain the Ion calls do. The ticket goes in the query string as well as through
/// <c>AccessTokenProvider</c>: the handler reads <c>access_token</c> from the query, and a custom
/// <c>WebSocketFactory</c> is not given the chance to set request headers on the upgrade.</para>
///
/// <para>Automatic reconnect is deliberately off. Half of what these tests assert is what the server
/// does when a client goes away, and a client that quietly comes back would repair the very thing
/// under test.</para>
/// </remarks>
public sealed class RealtimeClient : IAsyncDisposable
{
    private readonly HubConnection      connection;
    private readonly List<RecordedEvent> received = [];
    private readonly List<string>        decodeFailures = [];
    private readonly Lock                gate = new();

    // Kept so AbortAsync can kill the transport without a close frame. Reassigned on every connect,
    // because a restart builds a fresh socket through the same factory.
    private volatile WebSocket? socket;

    private RealtimeClient(HubConnection connection, TestUserSession session, RealtimeTransport transport)
    {
        this.connection = connection;
        Session         = session;
        Transport       = transport;
    }

    /// <summary>The session this connection belongs to — its user id and, crucially, its sid.</summary>
    public TestUserSession Session { get; }

    /// <summary>The user behind this connection.</summary>
    public Guid UserId => Session.UserId;

    /// <summary>The sid the hub ticket carries, and therefore the session grain this connection attaches to.</summary>
    public Guid SessionId => Session.SessionId;

    /// <summary>Which transport this client negotiated.</summary>
    public RealtimeTransport Transport { get; }

    /// <summary>The SignalR connection id — what <c>UserSessionGrain.Connections</c> holds for this client.</summary>
    public string? ConnectionId => connection.ConnectionId;

    /// <summary>Whether the hub connection is up right now.</summary>
    public bool IsConnected => connection.State == HubConnectionState.Connected;

    /// <summary>The raw connection state, for tests that care about the transition and not just the endpoints.</summary>
    public HubConnectionState State => connection.State;

    /// <summary>Payloads that arrived but could not be decoded. Non-empty means the harness is lying about the stream.</summary>
    public IReadOnlyList<string> DecodeFailures
    {
        get
        {
            lock (gate) return decodeFailures.ToArray();
        }
    }

    /// <summary>
    /// Connects a new client for <paramref name="session"/> and returns it once the hub has accepted
    /// the connection.
    /// </summary>
    /// <remarks>
    /// Two clients built from the same <see cref="TestUserSession"/> are two windows of one session:
    /// same sid, so the same session grain, two entries in its connection set. Two clients built from
    /// two sessions of the same user are two devices.
    /// </remarks>
    public static Task<RealtimeClient> ConnectAsync(TestUserSession session, CancellationToken ct = default)
        => ConnectAsync(session, RealtimeTransport.WebSockets, ct);

    /// <inheritdoc cref="ConnectAsync(TestUserSession,CancellationToken)"/>
    public static async Task<RealtimeClient> ConnectAsync(
        TestUserSession session, RealtimeTransport transport, CancellationToken ct = default)
    {
        var host   = ArgonTestEnvironment.Instance.Host;
        var server = host.Server;

        // The ticket has to come from THIS session's Ion client: the sid claim is taken from its
        // Sec-Ref header, and a ticket minted by any other client would attach the hub connection to
        // somebody else's session grain.
        var ticket = await session.Client.ForService<IEventBus>(host.Services).PickTicket(ct);

        var url = new UriBuilder(new Uri(server.BaseAddress, "w"))
        {
            Query = $"access_token={Uri.EscapeDataString(ticket)}"
        }.Uri;

        RealtimeClient? self = null;

        var builder = new HubConnectionBuilder().WithUrl(url, options =>
        {
            options.HttpMessageHandlerFactory = _ => server.CreateHandler();
            options.AccessTokenProvider       = () => Task.FromResult<string?>(ticket);

            if (transport == RealtimeTransport.LongPolling)
            {
                options.Transports = HttpTransportType.LongPolling;
                return;
            }

            options.Transports = HttpTransportType.WebSockets;
            options.WebSocketFactory = async (context, token) =>
            {
                var ws = server.CreateWebSocketClient();
                var opened = await ws.ConnectAsync(context.Uri, token);
                // Held so AbortAsync can Abort() it: that is a transport death with no close frame,
                // which is what a laptop lid or a dead Wi-Fi looks like to the hub, and the only way
                // to make the server take the grace path instead of the deliberate-offline one.
                if (self is not null)
                    self.socket = opened;
                return opened;
            };
        });

        var connection = builder.Build();

        self = new RealtimeClient(connection, session, transport);

        connection.On<byte[], string?>("forSelf",
            (payload, entryId) => self.Record(RealtimeStream.ForSelf, Guid.Empty, Guid.Empty, entryId, payload));
        connection.On<byte[], Guid, string?>("broadcastSpace",
            (payload, spaceId, entryId) => self.Record(RealtimeStream.BroadcastSpace, spaceId, Guid.Empty, entryId, payload));
        connection.On<byte[], Guid>("broadcastChannel",
            (payload, channelId) => self.Record(RealtimeStream.BroadcastChannel, Guid.Empty, channelId, null, payload));

        await connection.StartAsync(ct);
        await self.WaitForAttachAsync(ct);

        return self;
    }

    /// <summary>
    /// Returns once the server has finished <c>AppHub.OnConnectedAsync</c> for this connection.
    /// </summary>
    /// <remarks>
    /// <para><c>StartAsync</c> resolves when the handshake completes, which is before the hub has
    /// joined the connection to its <c>spaces/{id}</c> groups and attached the session grain. A test
    /// that provokes a broadcast right after connecting therefore raced the group join and, on a
    /// slow runner, lost: the event was published to a group the observer was not yet in, and no
    /// wait could ever find it (CI shard 2/4, <c>A_dnd_member_joining_a_space_...</c>, and earlier
    /// the two-device activity tests, which each worked around it locally).</para>
    ///
    /// <para>SignalR dispatches no client invocation until <c>OnConnectedAsync</c> has returned, so
    /// awaiting any hub method is the barrier. <c>UnSubscribeToChannel(Guid.Empty)</c> is the one
    /// with no effect: it removes the connection from a group it was never in and touches nothing
    /// else. A connection the hub refused (a revoked session) is aborted inside
    /// <c>OnConnectedAsync</c>; the invocation then fails and is swallowed here, leaving the client
    /// in the Disconnected state the caller goes on to assert.</para>
    /// </remarks>
    private async Task WaitForAttachAsync(CancellationToken ct)
    {
        try
        {
            await connection.InvokeAsync("UnSubscribeToChannel", Guid.Empty, ct);
        }
        catch (Exception) when (connection.State != HubConnectionState.Connected)
        {
            // Refused by the hub — the state is the answer, not this call.
        }
    }

    private void Record(RealtimeStream channel, Guid spaceId, Guid channelId, string? entryId, byte[] payload)
    {
        // Stamped before the decode so the timestamp measures the server's latency, not ours.
        var arrived = DateTimeOffset.UtcNow;

        try
        {
            var @event = IonFormatterStorage.GetFormatter<IArgonEvent>().Read(new CborReader(payload));
            lock (gate)
                received.Add(new RecordedEvent(arrived, channel, spaceId, channelId, entryId, @event));
        }
        catch (Exception e)
        {
            // Recorded rather than thrown: this runs on the hub's dispatch loop, where an exception
            // is swallowed, and a silently dropped event would read as "the server never sent it".
            lock (gate)
                decodeFailures.Add($"{channel}: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The number of events recorded so far, to be passed back as <c>from</c>.
    /// </summary>
    /// <remarks>
    /// Every query below scans the whole recorded history by default, which is what makes a wait
    /// immune to the event arriving before the wait started. When a test needs the opposite — "no
    /// Offline <em>after this point</em>", with an Offline already in the log from an earlier phase —
    /// it takes a mark first and passes it in.
    /// </remarks>
    public int Mark()
    {
        lock (gate) return received.Count;
    }

    /// <summary>Everything recorded from <paramref name="from"/> onwards, in arrival order.</summary>
    public IReadOnlyList<RecordedEvent> Records(int from = 0)
    {
        lock (gate) return received.Skip(from).ToArray();
    }

    /// <summary>Every recorded event of type <typeparamref name="TEvent"/>, in arrival order.</summary>
    public IReadOnlyList<TEvent> EventsOfType<TEvent>(int from = 0) where TEvent : IArgonEvent
    {
        lock (gate) return received.Skip(from).Select(x => x.Event).OfType<TEvent>().ToArray();
    }

    /// <summary>Every recorded event of type <typeparamref name="TEvent"/> with its delivery details.</summary>
    public IReadOnlyList<RecordedEvent> RecordsOfType<TEvent>(int from = 0) where TEvent : IArgonEvent
    {
        lock (gate) return received.Skip(from).Where(x => x.Event is TEvent).ToArray();
    }

    /// <summary>
    /// Waits until an event of type <typeparamref name="TEvent"/> satisfying <paramref name="predicate"/>
    /// has been recorded, and returns it.
    /// </summary>
    /// <exception cref="RealtimeWaitTimeoutException">
    /// Nothing matched inside <paramref name="timeout"/>. The message lists what did arrive.
    /// </exception>
    public async Task<TEvent> WaitForAsync<TEvent>(
        Func<TEvent, bool> predicate, TimeSpan timeout, int from = 0, CancellationToken ct = default)
        where TEvent : IArgonEvent
        => (TEvent)(await WaitForRecordAsync(predicate, timeout, from, ct)).Event;

    /// <summary>
    /// As <see cref="WaitForAsync{TEvent}"/>, but returns the delivery record so the test can assert
    /// on the channel and the space the event came through as well as on its contents.
    /// </summary>
    /// <exception cref="RealtimeWaitTimeoutException">
    /// Nothing matched inside <paramref name="timeout"/>. The message lists what did arrive.
    /// </exception>
    public async Task<RecordedEvent> WaitForRecordAsync<TEvent>(
        Func<TEvent, bool> predicate, TimeSpan timeout, int from = 0, CancellationToken ct = default)
        where TEvent : IArgonEvent
    {
        var found = await FirstWithinAsync(predicate, timeout, from, stopEarly: true, ct);

        if (found is not null)
            return found;

        throw new RealtimeWaitTimeoutException(
            $"No {typeof(TEvent).Name} matching the predicate arrived within {timeout.TotalSeconds:F1}s. " +
            Dump(from));
    }

    /// <summary>
    /// Waits out the whole <paramref name="window"/> and returns the first matching event if one
    /// turned up, or null if none did.
    /// </summary>
    /// <remarks>
    /// This is the "prove it does NOT happen" primitive, and the one place a fixed wait is right: an
    /// absence has no edge to poll for, so the window has to be spent. It returns early only when a
    /// match arrives, because at that point the answer is already known.
    /// </remarks>
    public Task<RecordedEvent?> FirstWithinAsync<TEvent>(
        Func<TEvent, bool> predicate, TimeSpan window, int from = 0, CancellationToken ct = default)
        where TEvent : IArgonEvent
        => FirstWithinAsync(predicate, window, from, stopEarly: true, ct);

    private async Task<RecordedEvent?> FirstWithinAsync<TEvent>(
        Func<TEvent, bool> predicate, TimeSpan window, int from, bool stopEarly, CancellationToken ct)
        where TEvent : IArgonEvent
    {
        var deadline = DateTimeOffset.UtcNow + window;

        while (true)
        {
            RecordedEvent? match;
            lock (gate)
                match = received.Skip(from).FirstOrDefault(x => x.Event is TEvent e && predicate(e));

            if (match is not null && stopEarly)
                return match;

            if (DateTimeOffset.UtcNow >= deadline)
                return match;

            await Task.Delay(25, ct);
        }
    }

    /// <summary>
    /// Fails the test if an event of type <typeparamref name="TEvent"/> matching
    /// <paramref name="predicate"/> arrives inside <paramref name="window"/>.
    /// </summary>
    /// <remarks>
    /// Spends the whole window on purpose — see <see cref="FirstWithinAsync{TEvent}"/> — so keep the
    /// window as short as the property allows. <paramref name="because"/> goes into the failure
    /// message and should say what the arrival of such an event would mean.
    /// </remarks>
    public async Task AssertNoneWithinAsync<TEvent>(
        Func<TEvent, bool> predicate, TimeSpan window, string because, int from = 0, CancellationToken ct = default)
        where TEvent : IArgonEvent
    {
        var startedAt = DateTimeOffset.UtcNow;
        var offender  = await FirstWithinAsync(predicate, window, from, stopEarly: true, ct);

        if (offender is not null)
            Assert.Fail($"{because} — but one arrived {(offender.ReceivedAt - startedAt).TotalSeconds:F1}s " +
                        $"into the {window.TotalSeconds:F0}s window: {offender}");
    }

    /// <summary>What the connection has seen, for a failure message.</summary>
    public string Dump(int from = 0)
    {
        lock (gate)
        {
            var sb = new StringBuilder();
            sb.Append($"Recorded {received.Count - from} event(s) on this connection");
            if (received.Count == from)
                sb.Append(" (none)");
            foreach (var e in received.Skip(from))
                sb.Append($"\n  - {e}");
            foreach (var f in decodeFailures)
                sb.Append($"\n  ! undecodable: {f}");
            return sb.ToString();
        }
    }

    /// <summary>The client's periodic "still here, and this is my status" — <c>AppHub.Heartbeat</c>.</summary>
    /// <remarks>
    /// The real client sends one every 15 s and one immediately after connecting. Tests send them by
    /// hand so the timing is theirs: the interesting cases are the first heartbeat after a connect
    /// (which is what corrects the status the hub assumed) and bursts that hit the grain's token
    /// bucket.
    /// </remarks>
    public Task Heartbeat(UserStatus status, CancellationToken ct = default)
        => connection.InvokeAsync("Heartbeat", status, ct);

    /// <summary>Deliberate offline — <c>AppHub.GoOffline</c>, what the client sends on logout or quit.</summary>
    public Task GoOffline(CancellationToken ct = default)
        => connection.InvokeAsync("GoOffline", ct);

    /// <summary>Join a space group by hand, for a space this connection was not a member of when it connected.</summary>
    public Task SubscribeToSpace(Guid spaceId, CancellationToken ct = default)
        => connection.InvokeAsync("SubscribeToSpace", spaceId, ct);

    /// <summary>Leave a space group.</summary>
    public Task UnSubscribeToSpace(Guid spaceId, CancellationToken ct = default)
        => connection.InvokeAsync("UnSubscribeToSpace", spaceId, ct);

    /// <summary>Join a channel group — the prerequisite for receiving <c>broadcastChannel</c>.</summary>
    public Task SubscribeToChannel(Guid channelId, CancellationToken ct = default)
        => connection.InvokeAsync("SubscribeToChannel", channelId, ct);

    /// <summary>Leave a channel group.</summary>
    public Task UnSubscribeToChannel(Guid channelId, CancellationToken ct = default)
        => connection.InvokeAsync("UnSubscribeToChannel", channelId, ct);

    /// <summary>Ask the server to replay what this client missed — <c>AppHub.Resume</c>.</summary>
    /// <remarks>
    /// Everything the replay delivers lands in this client's ordinary recorded stream, because the
    /// hub re-sends it through the same <c>forSelf</c> / <c>broadcastSpace</c> handlers.
    /// </remarks>
    /// <returns>The server's answer; <c>NeedFullResync</c> means the gap was too wide to replay.</returns>
    public Task<ResumeAck> ResumeAsync(
        string? userCursor, Dictionary<string, string>? spaceCursors = null, CancellationToken ct = default)
        => connection.InvokeAsync<ResumeAck>("Resume", userCursor, spaceCursors, ct);

    /// <summary>
    /// Closes the connection the polite way: a close frame, so the hub runs
    /// <c>OnDisconnectedAsync</c> promptly and the session detaches this connection id.
    /// </summary>
    public Task StopAsync(CancellationToken ct = default)
        => connection.StopAsync(ct);

    /// <summary>
    /// Kills the transport without a close frame, the way a dead network does, and waits for the
    /// client side to notice.
    /// </summary>
    /// <remarks>
    /// <para>This is the case the disconnect grace exists for, and it is not the same as
    /// <see cref="StopAsync"/>: both end with <c>OnDisconnectedAsync</c> on the server, but only an
    /// ungraceful drop is the shape a laptop lid or a lost Wi-Fi has, and the reconnect-within-grace
    /// behaviour is only reachable through it.</para>
    ///
    /// <para>Requires <see cref="RealtimeTransport.WebSockets"/>: there is no socket to kill on long
    /// polling, and faking it by cancelling the poll would look like a graceful close to the hub.</para>
    /// </remarks>
    public async Task AbortAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (Transport != RealtimeTransport.WebSockets)
            throw new NotSupportedException(
                $"An ungraceful drop needs {nameof(RealtimeTransport.WebSockets)}; this connection is on {Transport}.");

        var live = socket ?? throw new InvalidOperationException(
            "No WebSocket was created for this connection, so there is nothing to abort.");

        live.Abort();

        // The hub's OnDisconnectedAsync is what the test is really waiting on, but that is not
        // observable from here; the client reaching Disconnected is the closest signal, and it
        // happens on the same failed read.
        await Poll.UntilAsync(
            () => Task.FromResult(connection.State == HubConnectionState.Disconnected),
            timeout ?? TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(25),
            ct);
    }

    /// <summary>
    /// Reconnects this same client — same sid, so the same session grain, but a new connection id.
    /// </summary>
    /// <remarks>
    /// The detach-then-attach cycle is a scenario in its own right: a session that dropped and came
    /// back inside the grace window is meant to be indistinguishable from one that never dropped.
    /// Reusing the client rather than building a new one keeps the recorded event log continuous
    /// across the gap, which is what lets a test say "and no status event fired in between".
    /// </remarks>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        await connection.StartAsync(ct);
        await WaitForAttachAsync(ct);
    }

    /// <summary>Waits for the connection to reach <see cref="HubConnectionState.Disconnected"/>.</summary>
    public Task<bool> WaitForCloseAsync(TimeSpan timeout, CancellationToken ct = default)
        => Poll.UntilAsync(
            () => Task.FromResult(connection.State == HubConnectionState.Disconnected),
            timeout, TimeSpan.FromMilliseconds(25), ct);

    public async ValueTask DisposeAsync()
        => await connection.DisposeAsync();
}
