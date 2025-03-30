namespace Argon.Features.NatsStreaming;

using Env;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Orleans.Runtime;
using System;
using System.Buffers;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using Newtonsoft.Json;
using Orleans.Streams;

public class NatsContext(INatsClient client)
{
    public async Task<IArgonStream<IArgonEvent>> CreateWriteStream(StreamId id)
    {
        var stream = new NatsArgonWriteOnlyStream(id, client.CreateJetStreamContext());
        await stream.EnsureCreatedStream();
        return stream;
    }

    public async Task<IArgonStream<IArgonEvent>> CreateReadStream(StreamId id)
    {
        var stream = new NatsArgonReadOnlyStream(id, client.CreateJetStreamContext());
        await stream.CreateSub();
        return stream;
    }
}


public class NatsArgonWriteOnlyStream(StreamId streamId, INatsJSContext js) : IArgonStream<IArgonEvent>
{
    public async ValueTask Fire(IArgonEvent ev)
        => await OnNextAsync(ev);

    public async Task EnsureCreatedStream(CancellationToken ct = default)
        => await js.CreateOrUpdateStreamAsync(new StreamConfig(streamId.GetNamespace()!, [$"{streamId.GetNamespace()}.>"]), ct);

    public async ValueTask DisposeAsync() { }

    public async Task OnNextAsync(IArgonEvent item, StreamSequenceToken? token = null)
    {
        var result = await js.PublishAsync($"{streamId.GetNamespace()}.{streamId.GetKeyAsString()}", item, new ArgonEventSerializer());
    }

    public Task OnErrorAsync(Exception ex)
        => throw new WriteOnlyStreamException();

    public IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken cancellationToken = new())
        => throw new WriteOnlyStreamException();
}

public class NatsArgonReadOnlyStream(StreamId streamId, INatsJSContext js) : IArgonStream<IArgonEvent>
{
    private INatsJSConsumer _consumer;

    public ValueTask Fire(IArgonEvent ev)
        => throw new NotImplementedException();

    public async Task CreateSub()
    {
        var consumerName = streamId.ToString().Replace('/', '_');
        _consumer = await js.CreateOrUpdateConsumerAsync(streamId.GetNamespace()!, new ConsumerConfig(consumerName)
        {
            FilterSubject = $"{streamId.GetNamespace()}.{streamId.GetKeyAsString()}",
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            AckPolicy     = ConsumerConfigAckPolicy.Explicit
        });
    }

    public async ValueTask DisposeAsync() { }

    public Task OnNextAsync(IArgonEvent item, StreamSequenceToken? token = null)
        => throw new ReadOnlyStreamException();

    public Task OnErrorAsync(Exception ex)
        => throw new ReadOnlyStreamException();

    public async IAsyncEnumerator<IArgonEvent> GetAsyncEnumerator(CancellationToken ct = new())
    {
        await foreach (var msg in _consumer.FetchAsync(new NatsJSFetchOpts { MaxMsgs = 1000 }, new ArgonEventSerializer(), ct))
        {
            if (msg.Data is null)
                continue;
            yield return msg.Data;
            await msg.AckAsync(cancellationToken: ct);
        }
    }
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
                SubPendingChannelFullMode = BoundedChannelFullMode.Wait,
                AuthOpts                  = new NatsAuthOpts(),
                ConnectTimeout            = TimeSpan.FromMinutes(1),
                RequestTimeout            = TimeSpan.FromMinutes(1),
                CommandTimeout            = TimeSpan.FromMinutes(1),
            });
        });

        builder.Services.AddSingleton<INatsJSContext>(q => {
            var client = q.GetRequiredService<INatsClient>();
            return client.CreateJetStreamContext();
        });

        builder.Services.AddSingleton<NatsContext>();
        return builder.Services;
    }
}