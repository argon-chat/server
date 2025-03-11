namespace Argon.Features.NatsStreaming;

using System.Buffers;
using Orleans.Providers;
using Orleans.Serialization;

[Serializable, GenerateSerializer, Immutable,SerializationCallbacks(typeof(OnDeserializedCallbacks)), Alias("DefaultNatsMessageBodySerializer")]
public sealed class DefaultNatsMessageBodySerializer(Serializer<MemoryMessageBody> serializer) : INatsMessageBodySerializer, IOnDeserialized
{
    [NonSerialized]
    private Serializer<MemoryMessageBody> serializer = serializer;

    public void Serialize(MemoryMessageBody body, IBufferWriter<byte> bufferWriter)
        => serializer.Serialize(body, bufferWriter);

    public MemoryMessageBody Deserialize(ReadOnlySequence<byte> bodyBytes)
        => serializer.Deserialize(bodyBytes);

    void IOnDeserialized.OnDeserialized(DeserializationContext context)
        => this.serializer = context.ServiceProvider.GetRequiredService<Serializer<MemoryMessageBody>>();
}