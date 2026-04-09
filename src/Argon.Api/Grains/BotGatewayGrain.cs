namespace Argon.Api.Grains;

using Argon.Features.BotApi;
using System.Threading.Channels;

/// <summary>
/// Gateway grain for bot SSE event streaming.
/// Each bot (BotAsUserId) gets one grain instance.
/// Stores a bounded ring buffer for event replay (resume support) and
/// writes new events to a Channel{T} consumed by the SSE endpoint.
/// </summary>
public sealed class BotGatewayGrain(ILogger<BotGatewayGrain> logger) : Grain, IBotGatewayGrain
{
    private BotIntent                _intents;
    private bool                     _isConnected;
    private long                     _eventCounter;
    private Channel<BotSseEvent>?    _channel;
    private IGrainTimer?             _heartbeatTimer;

    // Ring buffer for resume support — last N events
    private const int EventBufferCapacity = 500;
    private readonly LinkedList<BotSseEvent> _eventBuffer = new();

    public Task<List<Guid>> ConnectAsync(BotIntent intents)
    {
        _intents     = intents;
        _isConnected = true;
        _channel     = Channel.CreateBounded<BotSseEvent>(new BoundedChannelOptions(1000)
        {
            FullMode    = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        // Heartbeat every 30 seconds
        _heartbeatTimer = this.RegisterGrainTimer(
            ct =>
            {
                _ = WriteEvent(new BotSseEvent
                {
                    Id   = NextEventId(),
                    Type = BotEventType.Heartbeat,
                    Data = new { timestamp = DateTimeOffset.UtcNow }
                });
                return Task.CompletedTask;
            },
            new GrainTimerCreationOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));

        logger.LogInformation("Bot {BotId} connected with intents {Intents}", this.GetPrimaryKey(), intents);

        // Return bot's space IDs — the SSE endpoint will await this then
        // send a READY event with the space list
        return GrainFactory.GetGrain<IUserGrain>(this.GetPrimaryKey()).GetMyServersIds();
    }

    public Task DisconnectAsync()
    {
        _isConnected = false;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        _channel?.Writer.TryComplete();
        _channel = null;

        logger.LogInformation("Bot {BotId} disconnected", this.GetPrimaryKey());
        return Task.CompletedTask;
    }

    public Task DispatchEventAsync(BotSseEvent evt, BotIntent requiredIntent)
    {
        if (!_isConnected || _channel is null)
            return Task.CompletedTask;

        // Filter by intents
        if ((requiredIntent & _intents) == 0)
            return Task.CompletedTask;

        return WriteEvent(evt);
    }

    public Task<bool> IsConnectedAsync() => Task.FromResult(_isConnected);

    public Task<List<BotSseEvent>> GetEventsSinceAsync(string lastEventId)
    {
        var result = new List<BotSseEvent>();
        var found  = false;

        foreach (var evt in _eventBuffer)
        {
            if (found)
                result.Add(evt);
            else if (evt.Id == lastEventId)
                found = true;
        }

        return Task.FromResult(result);
    }

    /// <summary>
    /// Returns the ChannelReader for SSE streaming. Called by the SSE endpoint.
    /// </summary>
    public ChannelReader<BotSseEvent>? GetEventReader() => _channel?.Reader;

    public Task<List<BotSseEvent>> PollEventsAsync(int maxCount)
    {
        var result = new List<BotSseEvent>();
        if (_channel is null || !_isConnected)
            return Task.FromResult(result);

        while (result.Count < maxCount && _channel.Reader.TryRead(out var evt))
            result.Add(evt);

        return Task.FromResult(result);
    }

    private Task WriteEvent(BotSseEvent evt)
    {
        // Add to ring buffer
        _eventBuffer.AddLast(evt);
        while (_eventBuffer.Count > EventBufferCapacity)
            _eventBuffer.RemoveFirst();

        // Write to SSE channel
        _channel?.Writer.TryWrite(evt);
        return Task.CompletedTask;
    }

    private string NextEventId() => Interlocked.Increment(ref _eventCounter).ToString();

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _heartbeatTimer?.Dispose();
        _channel?.Writer.TryComplete();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }
}
