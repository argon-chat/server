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

            // Resume support
            List<BotSseEvent>? missedEvents = null;
            var resumeId = lastEventId ?? ctx.Request.Headers["Last-Event-ID"].ToString();
            if (!string.IsNullOrEmpty(resumeId))
                missedEvents = await gateway.GetEventsSinceAsync(resumeId);

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers["Cache-Control"] = "no-cache";
            ctx.Response.Headers["Connection"]    = "keep-alive";

            var writer = ctx.Response.BodyWriter;
            var ct     = ctx.RequestAborted;

            // READY event
            await WriteSseEvent(writer, new BotSseEvent
            {
                Id   = "0",
                Type = BotEventType.Ready,
                Data = new { intents = (long)requestedIntents, spaceIds }
            }, ct);

            // Replay missed events
            if (missedEvents is { Count: > 0 })
            {
                foreach (var missed in missedEvents)
                    await WriteSseEvent(writer, missed, ct);

                await WriteSseEvent(writer, new BotSseEvent
                {
                    Id   = "resumed",
                    Type = BotEventType.Resumed,
                    Data = new { replayed = missedEvents.Count }
                }, ct);
            }

            // Stream live events via polling
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var events = await gateway.PollEventsAsync(50);

                    if (events.Count > 0)
                    {
                        foreach (var evt in events)
                            await WriteSseEvent(writer, evt, ct);
                    }
                    else
                    {
                        // No events — short delay before next poll
                        await Task.Delay(100, ct);
                    }
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
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        });

        var line = $"id: {evt.Id}\nevent: {evt.Type}\ndata: {data}\n\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        await writer.WriteAsync(bytes, ct);
        await writer.FlushAsync(ct);
    }
}
