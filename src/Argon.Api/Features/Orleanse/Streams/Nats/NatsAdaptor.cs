namespace Argon.Features.NatsStreaming;

using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Orleans.Providers;
using Orleans.Streams;

public class NatsAdaptor(
    NatsJSContext context,
    string name,
    INatsMessageBodySerializer serializer,
    HashRingBasedStreamQueueMapper mapper,
    ILogger logger)
    : IQueueAdapter
{
    public async Task QueueMessageBatchAsync<T>(StreamId streamId, IEnumerable<T> events, StreamSequenceToken? token,
        Dictionary<string, object> requestContext)
    {
        var queueId = mapper.GetQueueForStream(streamId);
        await CheckStreamExists();

        var message = new MemoryMessageBody(events.Cast<object>(), requestContext);
        var natsJsPubOpts = new NatsJSPubOpts()
        {
            ExpectedLastSubjectSequence = (ulong?)token?.SequenceNumber
        };
        var @namespace = Encoding.UTF8.GetString(streamId.Namespace.Span);
        var key        = Encoding.UTF8.GetString(streamId.Key.Span);
        logger.LogInformation("Publishing message to {Stream} {QueueId} {@namespace} {Key}", Name, queueId, @namespace, key);
        await context.PublishAsync($"{Name}.{queueId}.{@namespace}.{key}", message, opts: natsJsPubOpts,
            serializer: new NatsMemoryMessageBodySerializer(serializer));
    }

    private async Task CheckStreamExists()
    {
        try
        {
            await context.GetStreamAsync(Name);
        }
        catch (Exception err)
        {
            logger.LogInformation("Creating stream {Stream}", Name);
            await context.CreateStreamAsync(new StreamConfig(Name, [$"{Name}.>"])
            {
                Retention       = StreamConfigRetention.Interest,
                Discard         = StreamConfigDiscard.Old,
                Description     = $"Orleans Stream for {Name}",
                DuplicateWindow = TimeSpan.Zero
            });
        }
    }

    public IQueueAdapterReceiver CreateReceiver(QueueId queueId)
        => new NatsQueueAdapterReceiver(Name, context, queueId, serializer, logger);

    public string                  Name         { get; } = name;
    public bool                    IsRewindable => false;
    public StreamProviderDirection Direction    => StreamProviderDirection.ReadWrite;
}