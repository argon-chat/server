namespace Argon.Features.NatsStreaming;

using System.Buffers;
using NATS.Client.Core;
using Orleans.Providers;

public class NatsMemoryMessageBodySerializer(INatsMessageBodySerializer serializer) : INatsSerializer<MemoryMessageBody>
{
    public void Serialize(IBufferWriter<byte> bufferWriter, MemoryMessageBody value)
        => serializer.Serialize(value, bufferWriter);

    public MemoryMessageBody? Deserialize(in ReadOnlySequence<byte> buffer)
        => serializer.Deserialize(buffer);

    public INatsSerializer<MemoryMessageBody> CombineWith(INatsSerializer<MemoryMessageBody> next)
        => this;
}