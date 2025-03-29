namespace Argon.Features.NatsStreaming;

using NATS.Client.JetStream;
using Orleans.Providers;
using Orleans.Providers.Streams.Common;
using Orleans.Streams;

[Serializable, GenerateSerializer, Alias("Argon.Features.NatsStreaming.NatsBatchContainer"), MessagePackObject(true)]
public class NatsBatchContainer(
    StreamId streamId,
    NatsJSMsg<MemoryMessageBody> messageData,
    NatsStreamSequenceToken sequenceToken)
    : IBatchContainer
{
    public           NatsJSMsg<MemoryMessageBody> MessageData { get; } = messageData;
    private readonly EventSequenceToken           realToken = new(sequenceToken.SequenceNumber);

    public IEnumerable<Tuple<T, StreamSequenceToken>> GetEvents<T>()
    {
        if (MessageData.Data is null)
            return new List<Tuple<T, StreamSequenceToken>>();

        return MessageData.Data.Events.Cast<T>()
           .Select((e, i) => Tuple.Create<T, StreamSequenceToken>(e, realToken.CreateSequenceTokenForEvent(i)));
    }

    public bool ImportRequestContext() => false;

    public StreamId            StreamId      { get; } = streamId;
    public StreamSequenceToken SequenceToken { get; } = sequenceToken;
}