namespace ArgonComplexTest.Infrastructure.Presence;

using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using System.Formats.Cbor;
using System.Text;
using System.Threading.Channels;

/// <summary>A frame that carries no event, and how many events had arrived before it.</summary>
public sealed record ControlFrame(IRealtimeFrame Frame, int EventsBefore);

/// <summary>
/// One client's <c>EventBus.Realtime</c> stream — the Ion counterpart of <see cref="RealtimeClient"/>,
/// driven the way the web client drives it.
/// </summary>
/// <remarks>
/// <para>Over the in-memory test server's WebSocket, through the session's own <see cref="IonClient"/>:
/// the ticket is exchanged at <c>/ion.att</c> with that client's headers, so the stream attaches to
/// that session's grain. Reconnecting and resuming are off for the reason the SignalR harness has
/// them off — what the server does when a client goes away is half of what is under test.</para>
///
/// <para>Events land in the same <see cref="RecordedEvent"/> shape as on the hub, so a test can hold
/// one client of each and compare what the two transports delivered.</para>
/// </remarks>
public sealed class IonRealtimeClient : IAsyncDisposable
{
    private readonly Channel<IRealtimeCommand> commands = Channel.CreateUnbounded<IRealtimeCommand>();
    private readonly CancellationTokenSource   stop     = new();
    private readonly List<RecordedEvent>       received = [];
    private readonly List<ControlFrame>        control  = [];
    private readonly Lock                      gate     = new();

    private Task reading = Task.CompletedTask;
    private Exception? ended;

    private IonRealtimeClient(TestUserSession session) => Session = session;

    public TestUserSession Session { get; }

    /// <summary>The connection id the server announced in <see cref="Welcome"/>, or null before it.</summary>
    public string? ConnectionId => Frames<Welcome>().FirstOrDefault()?.connectionId;

    /// <summary>Whether the stream is still running.</summary>
    public bool IsConnected => !reading.IsCompleted;

    /// <summary>How the stream ended: null while it runs, or when it completed without an error.</summary>
    public Exception? Ended
    {
        get
        {
            lock (gate) return ended;
        }
    }

    /// <summary>
    /// Opens the stream for <paramref name="session"/> and returns once the server has either sent
    /// <see cref="Welcome"/> — the connect hook ran to its end — or refused the connection.
    /// </summary>
    public static async Task<IonRealtimeClient> ConnectAsync(TestUserSession session, CancellationToken ct = default)
    {
        session.Client.WithStreamOptions(o =>
        {
            o.Transports = [IonStreamTransportKind.WebSocket];
            o.Reconnect  = null;
        });

        var self   = new IonRealtimeClient(session);
        var stream = session.Client.ForService<IEventBus>(ArgonTestEnvironment.Instance.Host.Services)
           .Realtime(self.commands.Reader.ReadAllAsync(), self.stop.Token);

        self.reading = Task.Run(() => self.ReadAsync(stream), CancellationToken.None);

        await Poll.UntilAsync(() => Task.FromResult(self.ConnectionId is not null || self.reading.IsCompleted),
            TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(25), ct);

        return self;
    }

    private async Task ReadAsync(IAsyncEnumerable<IRealtimeFrame> stream)
    {
        try
        {
            await foreach (var frame in stream)
                Record(frame);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            lock (gate) ended = e;
        }
    }

    private void Record(IRealtimeFrame frame)
    {
        var arrived = DateTimeOffset.UtcNow;

        var (stream, spaceId, channelId, entryId, payload) = frame switch
        {
            ForSelf f    => (RealtimeStream.ForSelf, Guid.Empty, Guid.Empty, f.entryId, f.payload),
            ForSpace f   => (RealtimeStream.BroadcastSpace, f.spaceId, Guid.Empty, f.entryId, f.payload),
            ForChannel f => (RealtimeStream.BroadcastChannel, Guid.Empty, f.channelId, (string?)null, f.payload),
            _            => (default, Guid.Empty, Guid.Empty, null, IonBytes.Empty)
        };

        if (frame is not (ForSelf or ForSpace or ForChannel))
        {
            lock (gate) control.Add(new ControlFrame(frame, received.Count));
            return;
        }

        var @event = IonFormatterStorage.GetFormatter<IArgonEvent>().Read(new CborReader(payload.Memory));
        lock (gate)
            received.Add(new RecordedEvent(arrived, stream, spaceId, channelId, entryId, @event));
    }

    public Task Heartbeat(UserStatus status) => SendAsync(new Heartbeat(status));

    public Task GoOffline() => SendAsync(new GoOffline());

    public Task SubscribeToChannel(Guid channelId) => SendAsync(new SubscribeToChannel(channelId));

    public Task UnsubscribeFromChannel(Guid channelId) => SendAsync(new UnsubscribeFromChannel(channelId));

    public Task Typing(Guid spaceId, Guid channelId) => SendAsync(new Typing(spaceId, channelId));

    /// <summary>Sends a Resume and waits for its <see cref="Resumed"/>, which the server sends after the replay.</summary>
    public async Task<ControlFrame> ResumeAsync(
        string? userCursor, IReadOnlyList<RealtimeCursor>? spaceCursors = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var before = Controls<Resumed>().Count;

        await SendAsync(new Resume(userCursor, (spaceCursors ?? []).ToArray()));

        return await WaitForControlAsync<Resumed>(timeout ?? TimeSpan.FromSeconds(15), before, ct);
    }

    /// <summary>
    /// Returns once the server has run every command sent before it: commands are handled in order,
    /// and a Resume with no cursors changes nothing but is answered.
    /// </summary>
    public Task BarrierAsync(CancellationToken ct = default) => ResumeAsync(null, null, ct: ct);

    private Task SendAsync(IRealtimeCommand command)
        => commands.Writer.WriteAsync(command).AsTask();

    public int Mark()
    {
        lock (gate) return received.Count;
    }

    public IReadOnlyList<RecordedEvent> Records(int from = 0)
    {
        lock (gate) return received.Skip(from).ToArray();
    }

    public IReadOnlyList<T> Frames<T>() where T : IRealtimeFrame
    {
        lock (gate) return control.Select(x => x.Frame).OfType<T>().ToArray();
    }

    public IReadOnlyList<ControlFrame> Controls<T>() where T : IRealtimeFrame
    {
        lock (gate) return control.Where(x => x.Frame is T).ToArray();
    }

    public async Task<RecordedEvent> WaitForRecordAsync<TEvent>(
        Func<TEvent, bool> predicate, TimeSpan timeout, int from = 0, CancellationToken ct = default)
        where TEvent : IArgonEvent
    {
        var found = await FirstWithinAsync(predicate, timeout, from, ct);

        return found ?? throw new RealtimeWaitTimeoutException(
            $"No {typeof(TEvent).Name} matching the predicate arrived within {timeout.TotalSeconds:F1}s. {Dump(from)}");
    }

    public async Task<RecordedEvent?> FirstWithinAsync<TEvent>(
        Func<TEvent, bool> predicate, TimeSpan window, int from = 0, CancellationToken ct = default)
        where TEvent : IArgonEvent
    {
        var deadline = DateTimeOffset.UtcNow + window;

        while (true)
        {
            RecordedEvent? match;
            lock (gate)
                match = received.Skip(from).FirstOrDefault(x => x.Event is TEvent e && predicate(e));

            if (match is not null || DateTimeOffset.UtcNow >= deadline)
                return match;

            await Task.Delay(25, ct);
        }
    }

    /// <summary>Waits for the first frame of type <typeparamref name="T"/> after the <paramref name="from"/>th.</summary>
    public async Task<ControlFrame> WaitForControlAsync<T>(TimeSpan timeout, int from = 0, CancellationToken ct = default)
        where T : IRealtimeFrame
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            var match = Controls<T>().Skip(from).FirstOrDefault();
            if (match is not null)
                return match;

            if (DateTimeOffset.UtcNow >= deadline)
                throw new RealtimeWaitTimeoutException(
                    $"No {typeof(T).Name} arrived within {timeout.TotalSeconds:F1}s. {Dump()}");

            await Task.Delay(25, ct);
        }
    }

    /// <summary>Waits for the stream to end and returns how it ended; null for a clean end.</summary>
    public async Task<Exception?> WaitForEndAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        await reading.WaitAsync(timeout, ct);
        return Ended;
    }

    public string Dump(int from = 0)
    {
        lock (gate)
        {
            var sb = new StringBuilder($"Recorded {received.Count - from} event(s) on this stream");
            foreach (var e in received.Skip(from))
                sb.Append($"\n  - {e}");
            foreach (var f in control)
                sb.Append($"\n  * {f.Frame} (after {f.EventsBefore} event(s))");
            if (ended is not null)
                sb.Append($"\n  ended with {ended.GetType().Name}: {ended.Message}");
            return sb.ToString();
        }
    }

    /// <summary>Leaves the way the web client does on a page unload: the call is cancelled, which closes it gracefully.</summary>
    public async ValueTask DisposeAsync()
    {
        commands.Writer.TryComplete();
        await stop.CancelAsync();

        try
        {
            await reading.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
        }

        stop.Dispose();
    }
}
