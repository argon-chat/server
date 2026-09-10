namespace Argon.Api.BotApi.Interfaces;

using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using Argon.Services.Ion;

[BotInterface("IEvents", 1)]
[BotDescription("Subscribe to real-time events via Server-Sent Events (SSE). Receive messages, member changes, voice activity, and more.")]
public sealed class EventsV1(IGrainFactory grains) : IBotInterface
{
    public sealed record StreamQuery(
        long?   Intents     = null,
        string? LastEventId = null);

    public void MapRoutes(RouteGroupBuilder group)
    {
        group.RequireRateLimiting("Bot_IEvents");

        // The one route that cannot state its response as a return type: the body is an open stream
        // rather than a value, so the payload type is declared and the result built by hand.
        group.Get<StreamQuery, BotSseEvent>("/Stream")
           .Summary("Opens a persistent SSE connection. Pass intents as a bitmask to filter events. Supports reconnection via Last-Event-ID header or lastEventId query parameter.")
           .Produces("text/event-stream")
           .HandleResult(async (ctx, query) =>
        {
            var botUserId        = ctx.GetBotAsUserId();
            var requestedIntents = (BotIntent)(query.Intents ?? (long)BotIntent.AllNonPrivileged);

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
