namespace Argon.Features.BotApi;

using Argon.Features.NatsStreaming;
using Argon.Services.Ion;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Buffers;
using System.Collections.Concurrent;

/// <summary>
/// Publishes bot events to NATS JetStream.
/// One publish per event per space — bots consume independently via their own consumers.
/// </summary>
public sealed class BotEventPublisher(
    INatsJSContext              js,
    BotSseEventSerializer      serializer,
    ILogger<BotEventPublisher>  logger)
{
    private readonly ConcurrentDictionary<Guid, bool> _ensuredStreams = new();

    /// <summary>
    /// Publish a domain event to the bot event stream for a space.
    /// Maps the event to BotEventType, creates the NATS stream if needed, and publishes.
    /// </summary>
    public async ValueTask PublishIfMappedAsync<T>(T @event, Guid spaceId) where T : IArgonEvent
    {
        var mapping = BotEventMapping.TryMap(@event.UnionKey);
        if (mapping is null)
            return;

        var (eventType, requiredIntent) = mapping.Value;

        var botEvent = new BotSseEvent
        {
            Id        = Guid.NewGuid().ToString("N"),
            Type      = eventType,
            SpaceId   = spaceId,
            Data      = @event
        };

        await EnsureStreamAsync(spaceId);

        try
        {
            var subject = NatsStreamExtensions.ToBotEventSubject(spaceId);
            var ack = await js.PublishAsync(subject, botEvent, serializer);

            if (ack.Error is not null)
                logger.LogError("Failed to publish bot event to NATS {Subject}: {Error}",
                    subject, ack.Error.Description);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish bot event {EventType} for space {SpaceId}",
                eventType, spaceId);
        }
    }

    private async ValueTask EnsureStreamAsync(Guid spaceId)
    {
        if (_ensuredStreams.ContainsKey(spaceId))
            return;

        var streamName = NatsStreamExtensions.ToBotEventSubject(spaceId);

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

            _ensuredStreams.TryAdd(spaceId, true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to ensure NATS stream for space {SpaceId}", spaceId);
        }
    }
}

/// <summary>
/// NATS serializer for <see cref="BotSseEvent"/>.
/// Uses Newtonsoft.Json with TypeNameHandling.All for the Data payload (IArgonEvent).
/// </summary>
public sealed class BotSseEventSerializer : INatsSerializer<BotSseEvent>
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting       = Formatting.None,
        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
        Converters       = { new MessageEntityConverter(), new IonArrayConverter(), new IonMaybeConverter() }
    };

    public void Serialize(IBufferWriter<byte> bufferWriter, BotSseEvent value)
    {
        var json      = JsonConvert.SerializeObject(value, Settings);
        var byteCount = Encoding.UTF8.GetByteCount(json);
        var span      = bufferWriter.GetSpan(byteCount);
        Encoding.UTF8.GetBytes(json, span);
        bufferWriter.Advance(byteCount);
    }

    public BotSseEvent? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            var json = Encoding.UTF8.GetString(buffer.FirstSpan);
            return JsonConvert.DeserializeObject<BotSseEvent>(json, Settings);
        }

        using var ms = new MemoryStream((int)buffer.Length);
        foreach (var segment in buffer)
            ms.Write(segment.Span);

        ms.Position = 0;
        using var reader     = new StreamReader(ms, Encoding.UTF8);
        using var jsonReader = new JsonTextReader(reader);
        return JsonSerializer.CreateDefault(Settings).Deserialize<BotSseEvent>(jsonReader);
    }

    public INatsSerializer<BotSseEvent> CombineWith(INatsSerializer<BotSseEvent> next) => this;
}
