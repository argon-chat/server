namespace Argon.Features.NatsStreaming;

using Env;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Orleans.Runtime;
using System;
using System.Buffers;
using System.Threading.Channels;
using Newtonsoft.Json;

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

    public async Task<IArgonStream<IArgonEvent>> CreateReadStream(StreamId id)
    {
        logger.LogInformation("Begin create read stream for '{streamID}'", id);
        var stream = ActivatorUtilities.CreateInstance<NatsArgonReadOnlyStream>(provider, id, client.CreateJetStreamContext());
        try
        {
            await stream.CreateSub();
        }
        catch (Exception e)
        {
            logger.LogCritical(e, "Failed to create read stream for '{streamId}'->'{natsStreamId}'", id, id.ToNatsStreamName());
            throw;
        }

        return stream;
    }
}

public class NatsArgonWriteOnlyStream(StreamId streamId, INatsJSContext js, ILogger<NatsArgonWriteOnlyStream> logger) : IArgonStream<IArgonEvent>
{
    public async ValueTask Fire(IArgonEvent ev, CancellationToken ct = default)
    {
        var result = await js.PublishAsync(streamId.ToNatsStreamName(), ev, new ArgonEventSerializer(), cancellationToken: ct);

        if (result.Error is not null)
            logger.LogCritical("Error when publish message to nats, {errorCode}, {code}, {msg}", result.Error.ErrCode, result.Error.Code,
                result.Error.Description);
    }

    public async Task EnsureCreatedStream(CancellationToken ct = default)
        => await js.CreateOrUpdateStreamAsync(new StreamConfig(streamId.ToNatsStreamName(), [])
        {
            DuplicateWindow = TimeSpan.Zero,
            MaxAge          = TimeSpan.FromMinutes(1),
            AllowDirect     = true,
            MaxBytes        = int.MaxValue / 2,
            Retention       = StreamConfigRetention.Limits,
            Storage         = StreamConfigStorage.File,
            Discard         = StreamConfigDiscard.Old,
            AllowRollupHdrs = false,
        }, ct);

    public async ValueTask DisposeAsync()
    {
    }

    public IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken cancellationToken = new())
        => throw new WriteOnlyStreamException();
}

public class NatsArgonReadOnlyStream(StreamId streamId, INatsJSContext js) : IArgonStream<IArgonEvent>
{
    private readonly Guid _streamListenerId = Guid.NewGuid();


    private INatsJSConsumer _consumer;
    private string          _consumerName => $"{streamId.ToString().Replace('/', '_')}_{_streamListenerId:N}";

    public ValueTask Fire(IArgonEvent ev, CancellationToken ct = default)
        => throw new NotImplementedException();

    public async Task CreateSub()
        => _consumer = await js.CreateOrUpdateConsumerAsync(streamId.ToNatsStreamName(), new ConsumerConfig(_consumerName)
        {
            AckPolicy     = ConsumerConfigAckPolicy.None,
            DeliverPolicy = ConsumerConfigDeliverPolicy.New,
            AckWait       = TimeSpan.Zero,
            MaxAckPending = 3,
            Direct        = false
        });

    public async ValueTask DisposeAsync()
        => await js.DeleteConsumerAsync(streamId.ToNatsStreamName(), _consumerName);

    public async IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken ct = new())
    {
        await foreach (var msg in _consumer.ConsumeAsync(new ArgonEventSerializer(), new NatsJSConsumeOpts()
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

public class ArgonEventSerializer : INatsSerializer<IArgonEvent>
{
    public void Serialize(IBufferWriter<byte> bufferWriter, IArgonEvent value)
    {
        var json = JsonConvert.SerializeObject(value, new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All,
            Formatting       = Formatting.None
        });
        bufferWriter.Write(Encoding.UTF8.GetBytes(json));
    }

    public IArgonEvent? Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.IsSingleSegment)
        {
            var span = buffer.FirstSpan;
            var json = Encoding.UTF8.GetString(span);
            return JsonConvert.DeserializeObject<IArgonEvent>(json, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            });
        }

        using var memoryStream = new MemoryStream();
        foreach (var segment in buffer)
        {
            memoryStream.Write(segment.Span);
        }

        memoryStream.Position = 0;

        using var streamReader = new StreamReader(memoryStream, Encoding.UTF8);
        using var jsonReader   = new JsonTextReader(streamReader);
        return JsonSerializer.CreateDefault(new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All
        }).Deserialize<IArgonEvent>(jsonReader);
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
        return builder.Services;
    }
}