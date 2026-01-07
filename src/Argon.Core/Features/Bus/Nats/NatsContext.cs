namespace Argon.Features.NatsStreaming;

using Api.Features.Bus;
using Bus;
using Env;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Orleans.Runtime;
using System.Buffers;
using System.Threading.Channels;
using Services.Ion;

public class NatsContext(INatsClient client, ILogger<NatsContext> logger, IServiceProvider provider)
{
    public async Task<IArgonStream<IArgonEvent>> CreateWriteStream(StreamId id)
    {
        logger.LogInformation("Begin create write stream for '{streamID}'", id);

        var stream = ActivatorUtilities.CreateInstance<NatsArgonWriteOnlyStream>(provider, id, client.CreateJetStreamContext());

        try
        {
            await stream.EnsureCreatedStream();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to create write stream for '{streamId}'->'{natsStreamId}'", id, id.ToNatsStreamName());
            throw;
        }

        return stream;
    }

    public async Task<IArgonStream<IArgonEvent>> CreateReadStream(StreamId id, CancellationToken ct = default)
    {
        logger.LogInformation("Begin create read stream for '{streamID}'", id);
        var stream = ActivatorUtilities.CreateInstance<NatsArgonReadOnlyStream>(provider, id, client.CreateJetStreamContext());
        try
        {
            await stream.CreateSub(ct);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to create read stream for '{streamId}'->'{natsStreamId}'", id, id.ToNatsStreamName());
            throw;
        }

        return stream;
    }
}

public class NatsArgonWriteOnlyStream(StreamId streamId, INatsJSContext js, ILogger<NatsArgonWriteOnlyStream> logger, ILogger<ArgonEventSerializer> serializerLogger) : IArgonStream<IArgonEvent>
{
    public async ValueTask Fire(IArgonEvent ev, CancellationToken ct = default)
    {
        var result = await js.PublishAsync(streamId.ToNatsStreamName(), ev, new ArgonEventSerializer(serializerLogger), cancellationToken: ct);

        if (result.Error is not null)
            logger.LogCritical("Error when publish message to nats, {errorCode}, {code}, {msg}", result.Error.ErrCode, result.Error.Code,
                result.Error.Description);
    }

    public async Task EnsureCreatedStream(CancellationToken ct = default)
        => await js.CreateOrUpdateStreamAsync(new StreamConfig(streamId.ToNatsStreamName(), [])
        {
            DuplicateWindow = TimeSpan.Zero,
            MaxAge          = TimeSpan.FromSeconds(30),
            AllowDirect     = true,
            MaxBytes        = -1,
            MaxMsgs         = 1000,
            Retention       = StreamConfigRetention.Interest,
            Storage         = StreamConfigStorage.Memory,
            Discard         = StreamConfigDiscard.Old
        }, ct);

    public async ValueTask DisposeAsync()
    {
    }

    public IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken cancellationToken = new())
        => throw new WriteOnlyStreamException();
}

public class NatsArgonReadOnlyStream(
    StreamId streamId, 
    INatsJSContext js,
    ILogger<ArgonEventSerializer> serializerLogger) : IArgonStream<IArgonEvent>
{
    private readonly Guid _streamListenerId = Guid.NewGuid();

    private INatsJSConsumer _consumer;
    private string          _consumerName => $"{streamId.ToString().Replace('/', '_')}_{_streamListenerId:N}";

    public ValueTask Fire(IArgonEvent ev, CancellationToken ct = default)
        => throw new NotImplementedException();

    public async Task CreateSub(CancellationToken ct = default)
        => _consumer = await js.CreateOrUpdateConsumerAsync(streamId.ToNatsStreamName(), new ConsumerConfig(_consumerName)
        {
            AckPolicy     = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.New,
            AckWait       = TimeSpan.FromSeconds(1),
            MaxAckPending = 3,
            Direct        = false
        }, ct);

    public async ValueTask DisposeAsync()
        => await js.DeleteConsumerAsync(streamId.ToNatsStreamName(), _consumerName);

    public async IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken ct = new())
    {
        await foreach (var msg in _consumer.ConsumeAsync(new ArgonEventSerializer(serializerLogger), new NatsJSConsumeOpts()
        {
            MaxMsgs = 30
        }, ct))
        {
            if (msg.Data is null)
                continue;
            yield return msg.Data;
            await msg.AckAsync(cancellationToken: ct);
        }
    }
}

public static class NatsStreamExtensions
{
    public static string ToNatsStreamName(this StreamId streamId)
        => $"{streamId.GetNamespace()}.{streamId.GetKeyAsString()}".Replace(".", "_").Replace(" ", "").Replace('/', '_');
}

public class ArgonEventSerializer(ILogger<ArgonEventSerializer> logger) : INatsSerializer<IArgonEvent>
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting = Formatting.None,
        Converters =
        [
            new MessageEntityConverter(),
            new IonArrayConverter(),
            new IonMaybeConverter()
        ]
    };

    public void Serialize(IBufferWriter<byte> bufferWriter, IArgonEvent value)
    {
        if (value is MessageSent msgSent)
        {
            logger.LogInformation(
                "Serializing MessageSent: MessageId={MessageId}, EntitiesSize={EntitiesSize}",
                msgSent.message.messageId, msgSent.message.entities.Size);
            
            if (msgSent.message.entities.Size > 0)
            {
                var entityTypes = msgSent.message.entities.Values.Select((e, i) => $"[{i}]={e?.GetType().Name ?? "null"}");
                logger.LogInformation("MessageSent entities before JSON serialization: {EntityTypes}", string.Join(", ", entityTypes));
            }
        }

        var json = JsonConvert.SerializeObject(value, Settings);

        if (value is MessageSent msgSent2)
        {
            logger.LogInformation("MessageSent JSON length: {JsonLength}", json.Length);
            
            // Check if entities are in JSON
            var containsEntities = json.Contains("\"entities\"") || json.Contains("Entities");
            logger.LogInformation("JSON contains entities field: {ContainsEntities}", containsEntities);
        }

        bufferWriter.Write(Encoding.UTF8.GetBytes(json));
    }

    public IArgonEvent? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            var span = buffer.FirstSpan;
            var json = Encoding.UTF8.GetString(span);
            
            var result = JsonConvert.DeserializeObject<IArgonEvent>(json, Settings);
            
            if (result is MessageSent msgSent)
            {
                logger.LogInformation(
                    "Deserialized MessageSent: MessageId={MessageId}, EntitiesSize={EntitiesSize}",
                    msgSent.message.messageId, msgSent.message.entities.Size);
                
                if (msgSent.message.entities.Size > 0)
                {
                    var entityTypes = msgSent.message.entities.Values.Select((e, i) => $"[{i}]={e?.GetType().Name ?? "null"}");
                    logger.LogInformation("MessageSent entities after JSON deserialization: {EntityTypes}", string.Join(", ", entityTypes));
                }
                else
                {
                    logger.LogWarning("MessageSent entities are empty after deserialization! JSON: {Json}", json.Length > 500 ? json.Substring(0, 500) : json);
                }
            }
            
            return result;
        }

        using var memoryStream = new MemoryStream();
        foreach (var segment in buffer)
        {
            memoryStream.Write(segment.Span);
        }

        memoryStream.Position = 0;

        using var streamReader = new StreamReader(memoryStream, Encoding.UTF8);
        using var jsonReader   = new JsonTextReader(streamReader);
        var deserializedResult = JsonSerializer.CreateDefault(Settings).Deserialize<IArgonEvent>(jsonReader);
        
        if (deserializedResult is MessageSent msgSentMulti)
        {
            logger.LogDebug(
                "Deserialized MessageSent (multi-segment): MessageId={MessageId}, EntitiesSize={EntitiesSize}",
                msgSentMulti.message.messageId, msgSentMulti.message.entities.Size);
        }
        
        return deserializedResult;
    }

    public INatsSerializer<IArgonEvent> CombineWith(INatsSerializer<IArgonEvent> next)
        => this;
}

public class WriteOnlyStreamException : Exception;

public class ReadOnlyStreamException : Exception;

public static class NatsExtensions
{
    public static IServiceCollection AddNatsCtx(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<ArgonEventSerializer>();

        builder.Services.AddSingleton<INatsClient>(q =>
        {
            var client = q.GetRequiredService<IHostEnvironment>().DetermineClientSpace();

            return new NatsConnection(new NatsOpts()
            {
                Name                      = $"Argon {client}",
                Url                       = q.GetRequiredService<IConfiguration>().GetConnectionString("nats")!,
                SerializerRegistry        = NatsClientDefaultSerializerRegistry.Default,
                SubPendingChannelFullMode = BoundedChannelFullMode.DropOldest,
                AuthOpts                  = new NatsAuthOpts(),
                ConnectTimeout            = TimeSpan.FromMinutes(1),
                RequestTimeout            = TimeSpan.FromMinutes(1),
                CommandTimeout            = TimeSpan.FromMinutes(1),
            });
        });

        builder.Services.AddSingleton<INatsJSContext>(q =>
        {
            var client = q.GetRequiredService<INatsClient>();
            return client.CreateJetStreamContext();
        });

        builder.Services.AddSingleton<NatsContext>();

        builder.Services.AddSingleton<IStreamManagement, StreamManagement>();
        builder.Services.AddStreamingPump();
        return builder.Services;
    }
}