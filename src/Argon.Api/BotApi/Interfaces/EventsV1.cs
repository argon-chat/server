namespace Argon.Api.BotApi.Interfaces;

using Argon.Api.Grains;
using Argon.Features.BotApi;
using Argon.Features.BotApi.Contracts;
using System.Threading.Channels;

[BotInterface("IEvents", 1)]
[BotDescription("Subscribe to real-time events via Server-Sent Events (SSE). Receive messages, member changes, voice activity, and more.")]
[StableContract("35629fed73f8ce99daf60846978838db329a2caf9bb3bfe661449a755941aba4")]
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

            // Get the channel reader from the grain
            var grainRef = (BotGatewayGrain)grains.GetGrain<IBotGatewayGrain>(botUserId);

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

            // Stream live events
            var reader = grainRef.GetEventReader();
            if (reader is null)
                return;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await reader.WaitToReadAsync(ct))
                        break;

                    while (reader.TryRead(out var evt))
                        await WriteSseEvent(writer, evt, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ChannelClosedException) { }
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
