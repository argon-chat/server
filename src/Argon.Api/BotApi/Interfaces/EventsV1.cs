namespace Argon.Api.BotApi.Interfaces;

using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Services.Ion;

[BotInterface("IEvents", 1)]
[BotDescription("Subscribe to real-time events via Server-Sent Events (SSE). Receive messages, member changes, voice activity, and more.")]
[StableContract("a2ad75fa17aa104019b95ac85beb1f65ccdf7d704af92b4bb4331866746cc4b4")]
[BotRoute("GET", "/Stream", ResponseType = typeof(BotSseEvent), Description = "Opens a persistent SSE connection. Pass intents as a bitmask to filter events. Supports reconnection via Last-Event-ID header or lastEventId query parameter.")]
[BotError("/Stream", 403, "missing_intents", "No valid intents specified.")]
public sealed class EventsV1(IGrainFactory grains) : IBotInterface
{
    public void MapRoutes(RouteGroupBuilder group)
    {
        group.RequireRateLimiting("Bot_IEvents");

        group.MapGet("/Stream", async (HttpContext ctx, long? intents, string? lastEventId) =>
        {
            var botUserId        = ctx.GetBotAsUserId();
            var requestedIntents = (BotIntent)(intents ?? (long)BotIntent.AllNonPrivileged);

            ctx.PropagateToOrleans();

            var gateway    = grains.GetGrain<IBotGatewayGrain>(botUserId);
            var spaceInfos = await gateway.ConnectAsync(requestedIntents);

            async IAsyncEnumerable<SseItem<string>> Stream(
                [EnumeratorCancellation] CancellationToken ct = default)
            {
                BotApiInstrument.SseConnectionsOpened.Add(1);
                BotApiInstrument.IncrementSseConnection();

                // READY event
                yield return ToSseItem(new BotSseEvent
                {
                    Id   = "ready",
                    Type = BotEventType.Ready,
                    Data = new ReadyEventPayload((long)requestedIntents, spaceInfos.ToArray())
                });
                BotApiInstrument.SseEventsDelivered.Add(1,
                    new KeyValuePair<string, object?>("event_type", nameof(BotEventType.Ready)));

                // Stream live events via NATS consumers
                var heartbeatInterval = TimeSpan.FromSeconds(30);
                var nextHeartbeat     = DateTime.UtcNow + heartbeatInterval;

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var events = await gateway.ConsumeEventsAsync(50);

                        if (events.Count > 0)
                        {
                            foreach (var evt in events)
                            {
                                yield return ToSseItem(evt);
                                BotApiInstrument.SseEventsDelivered.Add(1,
                                    new KeyValuePair<string, object?>("event_type", evt.Type.ToString()));
                            }
                            nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
                        }
                        else if (DateTime.UtcNow >= nextHeartbeat)
                        {
                            // Heartbeat with cursor — client can resume from this position
                            var cursor = await gateway.GetCursor();
                            yield return ToSseItem(new BotSseEvent
                            {
                                Id   = cursor,
                                Type = BotEventType.Heartbeat,
                                Data = new HeartbeatEventPayload(DateTimeOffset.UtcNow.ToArgonTimeMillis())
                            });
                            nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
                        }

                        await Task.Delay(500, ct);
                    }
                }
                finally
                {
                    BotApiInstrument.SseConnectionsClosed.Add(1);
                    BotApiInstrument.DecrementSseConnection();
                    await gateway.DisconnectAsync();
                }
            }

            return TypedResults.ServerSentEvents(Stream(ctx.RequestAborted));
        });
    }

    private static readonly JsonSerializerSettings SseSettings = new()
    {
        ContractResolver = new BotSseContractResolver(),
        Formatting       = Formatting.None,
        Converters       = { new IonArrayConverter(), new IonMaybeConverter() }
    };

    private static string ToCamelCase(BotEventType type)
    {
        var s = type.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    private static SseItem<string> ToSseItem(BotSseEvent evt)
    {
        var data = JsonConvert.SerializeObject(evt.Data, SseSettings);
        return new SseItem<string>(data, ToCamelCase(evt.Type))
        {
            EventId = evt.Id
        };
    }
}
