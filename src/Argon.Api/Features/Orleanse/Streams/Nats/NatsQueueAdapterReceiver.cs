namespace Argon.Features.NatsStreaming;

using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Orleans.Streams;

public class NatsQueueAdapterReceiver(
    string name,
    NatsJSContext context,
    QueueId queueId,
    INatsMessageBodySerializer serializer,
    ILogger logger)
    : IQueueAdapterReceiver
{
    private INatsJSConsumer? _consumer;

    public async Task Initialize(TimeSpan timeout)
    {
        await CheckStreamExists();
        await EnsureConsumerExists();
    }

    private async Task EnsureConsumerExists()
    {
        try
        {
            _consumer = await context.GetConsumerAsync(name, queueId.ToString());
        }
        catch (Exception err)
        {
            logger.LogInformation("Creating consumer {ConsumerName}", name);
            _consumer = await context.CreateOrUpdateConsumerAsync(name,
                new ConsumerConfig(queueId.ToString())
                {
                    DurableName   = queueId.ToString(),
                    FilterSubject = $"{name}.{queueId}.>",
                    DeliverPolicy = ConsumerConfigDeliverPolicy.New,
                    AckPolicy     = ConsumerConfigAckPolicy.Explicit,
                    Description   = $"Consumer for '{name}.{queueId}.>'"
                });
        }
    }

    private async Task CheckStreamExists()
    {
        try
        {
            await context.GetStreamAsync(name);
        }
        catch (Exception err)
        {
            logger.LogInformation("Creating stream {StreamName}", name);
            await context.CreateStreamAsync(new StreamConfig(name, [$"{name}.>"]));
        }
    }

    public async Task<IList<IBatchContainer>> GetQueueMessagesAsync(int maxCount)
    {
        if (_consumer == null) return new List<IBatchContainer>();

        var messages = new List<IBatchContainer>();
        await foreach (var message in _consumer.FetchNoWaitAsync(new NatsJSFetchOpts()
        {
            MaxMsgs = maxCount,
            Expires = TimeSpan.FromSeconds(1)
        }, new NatsMemoryMessageBodySerializer(serializer)))
        {
            logger.LogDebug("Received message {Subject}", message.Subject);
            var rawStreamId  = message.Subject.Split('.');
            var rawNamespace = Encoding.UTF8.GetBytes(rawStreamId[2]);
            var rawKey       = Encoding.UTF8.GetBytes(rawStreamId[3]);
            var batch = new NatsBatchContainer(StreamId.Create(rawNamespace, rawKey), message,
                new NatsStreamSequenceToken(message.Metadata.Value.Sequence), serializer);
            messages.Add(batch);
        }

        return messages;
    }

    public async Task MessagesDeliveredAsync(IList<IBatchContainer> messages)
    {
        foreach (var batch in messages)
        {
            if (batch is not NatsBatchContainer natsBatchContainer) continue;
            logger.LogDebug("Asked message {StreamId}, {SequenceToken}", natsBatchContainer.StreamId, natsBatchContainer.SequenceToken);
            await natsBatchContainer.MessageData.AckAsync();
        }
    }

    public Task Shutdown(TimeSpan timeout)
        => Task.CompletedTask;
}