namespace Argon.Api.Grains;

using Argon.Features.BotApi;
using Argon.Features.Logic;
using Argon.Features.NatsStreaming;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

/// <summary>
/// Gateway grain for bot SSE event streaming via NATS JetStream.
/// Each bot (BotAsUserId) gets one grain instance.
/// Uses durable NATS consumers that persist across reconnects for automatic resume.
/// </summary>
public sealed class BotGatewayGrain(
    INatsJSContext              js,
    BotSseEventSerializer       serializer,
    IUserPresenceService        presenceService,
    ILogger<BotGatewayGrain>    logger) : Grain, IBotGatewayGrain
{
    private static readonly Dictionary<BotEventType, BotIntent> EventIntents = BuildEventIntentMap();

    private static Dictionary<BotEventType, BotIntent> BuildEventIntentMap()
    {
        var map = new Dictionary<BotEventType, BotIntent>();
        foreach (var (_, defAttr, _, _) in BotContractVerifier.DiscoverEventDefinitions())
            map[defAttr.EventType] = defAttr.Intent;
        return map;
    }

    private BotIntent                                       _intents;
    private bool                                            _isConnected;
    private IGrainTimer?                                    _heartbeatTimer;
    private readonly Dictionary<Guid, INatsJSConsumer>      _consumers = new();
    private readonly List<Guid>                             _spaceIds  = new();
    private INatsJSConsumer?                                _directConsumer;

    // Track last consumed NATS sequence per space for cursor/resume
    private readonly Dictionary<Guid, ulong>                _lastSequences = new();
    private ulong                                           _lastDirectSeq;
    private int                                             _presenceTickCount;

    private Guid BotUserId => this.GetPrimaryKey();

    private string BotSessionId => $"bot_{BotUserId:N}";

    public async Task<List<Guid>> ConnectAsync(BotIntent intents)
    {
        _intents     = intents;
        _isConnected = true;

        // Get bot's spaces
        var spaceIds = await GrainFactory.GetGrain<IUserGrain>(BotUserId).GetMyServersIds();
        _spaceIds.Clear();
        _spaceIds.AddRange(spaceIds);

        // Create/reuse durable NATS consumers for each space
        // Consumers are durable — they persist across reconnects, NATS tracks position
        foreach (var spaceId in spaceIds)
            await CreateConsumerForSpace(spaceId);

        // Direct consumer for per-user events (calls, DMs)
        await CreateDirectConsumer();

        // Register a presence session so CallGrain (and other services)
        // can discover this bot via IUserSessionDiscoveryService
        await presenceService.SetSessionOnlineAsync(BotUserId, BotSessionId);
        await presenceService.SetSessionStatusAsync(BotUserId, BotSessionId, UserStatus.Online);

        // Set bot Online in all spaces
        foreach (var spaceId in spaceIds)
            _ = GrainFactory.GetGrain<ISpaceGrain>(spaceId).SetUserStatus(BotUserId, UserStatus.Online);

        _heartbeatTimer = this.RegisterGrainTimer(
            BotPresenceTickAsync,
            new GrainTimerCreationOptions(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)));

        logger.LogInformation("Bot {BotId} connected with intents {Intents}, spaces: {SpaceCount}",
            BotUserId, intents, spaceIds.Count);

        return spaceIds;
    }

    public async Task DisconnectAsync()
    {
        _isConnected = false;
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        // Remove presence session so session discovery no longer finds the bot
        await presenceService.RemoveSessionStatusAsync(BotUserId, BotSessionId);

        // Set bot Offline in all spaces
        foreach (var spaceId in _spaceIds)
            _ = GrainFactory.GetGrain<ISpaceGrain>(spaceId).SetUserStatus(BotUserId, UserStatus.Offline);

        // Don't delete NATS consumers — they're durable.
        // On reconnect, NATS delivers from where we left off.
        // InactiveThreshold auto-cleans if bot never reconnects.
        _consumers.Clear();
        _spaceIds.Clear();
        _directConsumer = null;

        logger.LogInformation("Bot {BotId} disconnected", BotUserId);
    }

    public Task<bool> IsConnectedAsync() => Task.FromResult(_isConnected);

    public async Task SubscribeToSpace(Guid spaceId)
    {
        if (!_isConnected)
            return;

        if (_consumers.ContainsKey(spaceId))
            return;

        _spaceIds.Add(spaceId);
        await CreateConsumerForSpace(spaceId);

        _ = GrainFactory.GetGrain<ISpaceGrain>(spaceId).SetUserStatus(BotUserId, UserStatus.Online);
    }

    public async Task UnsubscribeFromSpace(Guid spaceId)
    {
        if (!_isConnected)
            return;

        _ = GrainFactory.GetGrain<ISpaceGrain>(spaceId).SetUserStatus(BotUserId, UserStatus.Offline);

        _spaceIds.Remove(spaceId);
        _consumers.Remove(spaceId);
        _lastSequences.Remove(spaceId);

        // Delete consumer for uninstalled spaces — bot won't rejoin
        try
        {
            var streamName   = NatsStreamExtensions.ToBotEventSubject(spaceId);
            var consumerName = GetConsumerName(spaceId);
            await js.DeleteConsumerAsync(streamName, consumerName);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to delete consumer for space {SpaceId}", spaceId);
        }
    }

    public async Task<List<BotSseEvent>> ConsumeEventsAsync(int maxCount)
    {
        var result = new List<BotSseEvent>();

        if (!_isConnected || (_consumers.Count == 0 && _directConsumer is null))
            return result;

        // Round-robin across all space consumers
        foreach (var (spaceId, consumer) in _consumers)
        {
            if (result.Count >= maxCount)
                break;

            try
            {
                var batch = consumer.FetchNoWaitAsync<BotSseEvent>(new NatsJSFetchOpts
                {
                    MaxMsgs = maxCount - result.Count
                }, serializer);

                await foreach (var msg in batch)
                {
                    if (msg.Data is null)
                    {
                        await msg.AckAsync();
                        continue;
                    }

                    // Intent filtering — drop events the bot didn't subscribe to
                    if (EventIntents.TryGetValue(msg.Data.Type, out var requiredIntent)
                        && requiredIntent != BotIntent.None
                        && !_intents.HasFlag(requiredIntent))
                    {
                        await msg.AckAsync();
                        continue;
                    }

                    // Use NATS sequence as stable event ID, track cursor
                    var seq = msg.Metadata?.Sequence.Stream ?? 0;
                    _lastSequences[spaceId] = seq;
                    var evt = msg.Data with { Id = $"{spaceId:N}_{seq}" };
                    result.Add(evt);
                    await msg.AckAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to consume events for space {SpaceId}", spaceId);
            }
        }

        // Consume from direct subject (calls, DMs)
        if (_directConsumer is not null && result.Count < maxCount)
        {
            try
            {
                var batch = _directConsumer.FetchNoWaitAsync<BotSseEvent>(new NatsJSFetchOpts
                {
                    MaxMsgs = maxCount - result.Count
                }, serializer);

                await foreach (var msg in batch)
                {
                    if (msg.Data is null)
                    {
                        await msg.AckAsync();
                        continue;
                    }

                    if (EventIntents.TryGetValue(msg.Data.Type, out var requiredIntent)
                        && requiredIntent != BotIntent.None
                        && !_intents.HasFlag(requiredIntent))
                    {
                        await msg.AckAsync();
                        continue;
                    }

                    var seq = msg.Metadata?.Sequence.Stream ?? 0;
                    _lastDirectSeq = seq;
                    var evt = msg.Data with { Id = $"direct_{seq}" };
                    result.Add(evt);
                    await msg.AckAsync();
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to consume direct events for bot {BotId}", BotUserId);
            }
        }

        return result;
    }

    private async Task BotPresenceTickAsync(CancellationToken ct)
    {
        if (!_isConnected)
            return;

        await presenceService.RefreshSessionStatusTtlAsync(BotUserId, BotSessionId, ct);
        await presenceService.HeartbeatAsync(BotUserId, BotSessionId, ct);

        // Every 5th tick (~2.5 min), re-broadcast Online to all spaces
        // This recovers from SpaceGrain reactivations that lose real-time push state
        _presenceTickCount++;
        if (_presenceTickCount % 5 == 0)
        {
            foreach (var spaceId in _spaceIds)
                _ = GrainFactory.GetGrain<ISpaceGrain>(spaceId).SetUserStatus(BotUserId, UserStatus.Online);
        }
    }

    private async Task CreateConsumerForSpace(Guid spaceId)
    {
        var streamName   = NatsStreamExtensions.ToBotEventSubject(spaceId);
        var consumerName = GetConsumerName(spaceId);

        try
        {
            // Ensure stream exists
            try
            {
                await js.CreateOrUpdateStreamAsync(new StreamConfig(streamName, [streamName])
                {
                    DuplicateWindow = TimeSpan.Zero,
                    MaxAge          = TimeSpan.FromMinutes(5),
                    AllowDirect     = true,
                    MaxBytes        = -1,
                    MaxMsgs         = 5000,
                    Retention       = StreamConfigRetention.Limits,
                    Storage         = StreamConfigStorage.Memory,
                    Discard         = StreamConfigDiscard.Old
                });
            }
            catch { /* stream may already exist */ }

            var consumer = await js.CreateOrUpdateConsumerAsync(streamName, new ConsumerConfig(consumerName)
            {
                AckPolicy     = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                AckWait       = TimeSpan.FromSeconds(30),
                MaxAckPending = 100,
                InactiveThreshold = TimeSpan.FromMinutes(10)
            });

            _consumers[spaceId] = consumer;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create NATS consumer for bot {BotId} space {SpaceId}",
                BotUserId, spaceId);
        }
    }

    private string GetConsumerName(Guid spaceId)
        => $"bot_{BotUserId:N}_{spaceId:N}";

    private async Task CreateDirectConsumer()
    {
        var streamName   = NatsStreamExtensions.ToBotDirectSubject(BotUserId);
        var consumerName = $"bot_{BotUserId:N}_direct";

        try
        {
            try
            {
                await js.CreateOrUpdateStreamAsync(new StreamConfig(streamName, [streamName])
                {
                    DuplicateWindow = TimeSpan.Zero,
                    MaxAge          = TimeSpan.FromMinutes(5),
                    AllowDirect     = true,
                    MaxBytes        = -1,
                    MaxMsgs         = 1000,
                    Retention       = StreamConfigRetention.Limits,
                    Storage         = StreamConfigStorage.Memory,
                    Discard         = StreamConfigDiscard.Old
                });
            }
            catch { /* stream may already exist */ }

            _directConsumer = await js.CreateOrUpdateConsumerAsync(streamName, new ConsumerConfig(consumerName)
            {
                AckPolicy         = ConsumerConfigAckPolicy.Explicit,
                DeliverPolicy     = ConsumerConfigDeliverPolicy.New,
                AckWait           = TimeSpan.FromSeconds(30),
                MaxAckPending     = 100,
                InactiveThreshold = TimeSpan.FromMinutes(10)
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create direct NATS consumer for bot {BotId}", BotUserId);
        }
    }

    public Task<string> GetCursor()
    {
        if (_lastSequences.Count == 0)
            return Task.FromResult("0");

        // Encode as "spaceId:seq,spaceId:seq,..." — compact, parseable
        return Task.FromResult(string.Join(',',
            _lastSequences.Select(kv => $"{kv.Key:N}:{kv.Value}")));
    }

    public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        if (_isConnected)
            await DisconnectAsync();

        await base.OnDeactivateAsync(reason, cancellationToken);
    }
}
