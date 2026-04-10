namespace Argon.Api.BotApi.Interfaces;

using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;

[BotInterface("IEvents", 1)]
[BotDescription("Subscribe to real-time events via Server-Sent Events (SSE). Receive messages, member changes, voice activity, and more.")]
[StableContract("a2ad75fa17aa104019b95ac85beb1f65ccdf7d704af92b4bb4331866746cc4b4")]
[BotRoute("GET", "/Stream", ResponseType = typeof(BotSseEvent), Description = "Opens a persistent SSE connection. Pass intents as a bitmask to filter events. Supports reconnection via Last-Event-ID header or lastEventId query parameter.")]
[BotError("/Stream", 403, "missing_intents", "No valid intents specified.")]
public sealed class EventsV1(IGrainFactory grains) : IBotInterface
{
    public void MapRoutes(RouteGroupBuilder group)
    {
        group.MapGet("/Stream", async (HttpContext ctx, long? intents, string? lastEventId) =>
        {
            var botUserId        = ctx.GetBotAsUserId();
            var requestedIntents = (BotIntent)(intents ?? (long)BotIntent.AllNonPrivileged);

            ctx.PropagateToOrleans();

            var gateway  = grains.GetGrain<IBotGatewayGrain>(botUserId);
            var spaceIds = await gateway.ConnectAsync(requestedIntents);

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"]    = "keep-alive";

            var writer = ctx.Response.BodyWriter;
            var ct     = ctx.RequestAborted;

            // READY event
            await WriteSseEvent(writer, new BotSseEvent
            {
                Id   = "ready",
                Type = BotEventType.Ready,
                Data = new { intents = (long)requestedIntents, spaceIds }
            }, ct);

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
                            await WriteSseEvent(writer, evt, ct);
                        nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
                    }
                    else if (DateTime.UtcNow >= nextHeartbeat)
                    {
                        // Heartbeat with cursor — client can resume from this position
                        var cursor = await gateway.GetCursor();
                        await WriteSseEvent(writer, new BotSseEvent
                        {
                            Id   = cursor,
                            Type = BotEventType.Heartbeat,
                            Data = new { timestamp = DateTimeOffset.UtcNow }
                        }, ct);
                        nextHeartbeat = DateTime.UtcNow + heartbeatInterval;
                    }

                    await Task.Delay(500, ct);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                await gateway.DisconnectAsync();
            }
        });
    }

    private static async Task WriteSseEvent(System.IO.Pipelines.PipeWriter writer, BotSseEvent evt, CancellationToken ct)
    {
        var data = JsonConvert.SerializeObject(evt, new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All,
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            Converters       = { new MessageEntityConverter() }
        });

        var line = $"id: {evt.Id}\nevent: {evt.Type}\ndata: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await writer.WriteAsync(bytes, ct);
        await writer.FlushAsync(ct);
    }
}
