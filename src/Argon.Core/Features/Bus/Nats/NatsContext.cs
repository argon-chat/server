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
        logger.LogDebug("Creating write stream for '{StreamId}'", id);

        var stream = ActivatorUtilities.CreateInstance<NatsArgonWriteOnlyStream>(provider, id, client.CreateJetStreamContext());

        try
        {
            await stream.EnsureCreatedStream();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to create write stream for '{StreamId}'->'{NatsStreamId}'", id, id.ToNatsStreamName());
            throw;
        }

        return stream;
    }

    public async Task<IArgonStream<IArgonEvent>> CreateReadStream(StreamId id, CancellationToken ct = default)
    {
        logger.LogDebug("Creating read stream for '{StreamId}'", id);
        var stream = ActivatorUtilities.CreateInstance<NatsArgonReadOnlyStream>(provider, id, client.CreateJetStreamContext());
        try
        {
            await stream.CreateSub(ct);
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to create read stream for '{StreamId}'->'{NatsStreamId}'", id, id.ToNatsStreamName());
            throw;
        }

        return stream;
    }
}

public class NatsArgonWriteOnlyStream(
    StreamId streamId, 
    INatsJSContext js, 
    ILogger<NatsArgonWriteOnlyStream> logger, 
    ArgonEventSerializer serializer) : IArgonStream<IArgonEvent>
{
    public async ValueTask Fire(IArgonEvent ev, CancellationToken ct = default)
    {
        var result = await js.PublishAsync(streamId.ToNatsStreamName(), ev, serializer, cancellationToken: ct);

        if (result.Error is not null)
            logger.LogError("Failed to publish to NATS stream {StreamId}: {ErrorCode} {Code} {Description}", 
                streamId, result.Error.ErrCode, result.Error.Code, result.Error.Description);
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

    public ValueTask DisposeAsync()
    {
        logger.LogDebug("Disposed write stream for {StreamId}", streamId);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => throw new WriteOnlyStreamException();
}

public class NatsArgonReadOnlyStream(
    StreamId streamId, 
    INatsJSContext js,
    ILogger<NatsArgonReadOnlyStream> logger,
    ArgonEventSerializer serializer) : IArgonStream<IArgonEvent>
{
    private readonly Guid _streamListenerId = Guid.NewGuid();
    private readonly string _consumerName = $"{streamId.ToString().Replace('/', '_')}_{Guid.NewGuid():N}";

    private INatsJSConsumer? _consumer;
    private int _disposed;

    public ValueTask Fire(IArgonEvent ev, CancellationToken ct = default)
        => throw new ReadOnlyStreamException();

    public async Task CreateSub(CancellationToken ct = default)
    {
        _consumer = await js.CreateOrUpdateConsumerAsync(streamId.ToNatsStreamName(), new ConsumerConfig(_consumerName)
        {
            AckPolicy     = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.New,
            AckWait       = TimeSpan.FromSeconds(1),
            MaxAckPending = 3,
            Direct        = false
        }, ct);
        
        logger.LogDebug("Created consumer {ConsumerName} for stream {StreamId}", _consumerName, streamId);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        
        if (_consumer is null)
        {
            logger.LogDebug("Disposing read stream {StreamId} - consumer was never created", streamId);
            return;
        }
        
        try
        {
            await js.DeleteConsumerAsync(streamId.ToNatsStreamName(), _consumerName);
            logger.LogDebug("Deleted consumer {ConsumerName} for stream {StreamId}", _consumerName, streamId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete consumer {ConsumerName} for stream {StreamId}", _consumerName, streamId);
        }
    }

    public async IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed == 1, this);
        
        if (_consumer is null)
            throw new InvalidOperationException("Consumer not created. Call CreateSub first.");
        
        await foreach (var msg in _consumer.ConsumeAsync(serializer, new NatsJSConsumeOpts
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
        try
        {
            var json = JsonConvert.SerializeObject(value, Settings);
            var byteCount = Encoding.UTF8.GetByteCount(json);
            var span = bufferWriter.GetSpan(byteCount);
            Encoding.UTF8.GetBytes(json, span);
            bufferWriter.Advance(byteCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to serialize event: {EventType}", value.GetType().Name);
            throw;
        }
    }

    public IArgonEvent? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        try
        {
            if (buffer.IsSingleSegment)
            {
                var json = Encoding.UTF8.GetString(buffer.FirstSpan);
                return JsonConvert.DeserializeObject<IArgonEvent>(json, Settings);
            }

            using var memoryStream = new MemoryStream((int)buffer.Length);
            foreach (var segment in buffer)
            {
                memoryStream.Write(segment.Span);
            }

            memoryStream.Position = 0;

            using var streamReader = new StreamReader(memoryStream, Encoding.UTF8);
            using var jsonReader = new JsonTextReader(streamReader);
            return JsonSerializer.CreateDefault(Settings).Deserialize<IArgonEvent>(jsonReader);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to deserialize event from buffer of length {BufferLength}", buffer.Length);
            throw;
        }
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

            return new NatsConnection(new NatsOpts
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