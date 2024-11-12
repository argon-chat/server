namespace Argon.Api.Features;

using System.Runtime.CompilerServices;
using MemoryPack;
using Newtonsoft.Json;
using Orleans.Storage;
using Orleans.Streams;

internal class MemoryPackStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input) 
        => new(MemoryPackSerializer.Serialize(input));

    public T Deserialize<T>(BinaryData input) 
        => MemoryPackSerializer.Deserialize<T>(input) ?? throw new InvalidOperationException();
}

// todo idiots serialization
public class MemoryPackFormatterForPubSub : MemoryPackFormatter<PubSubSubscriptionState>
{
    [ModuleInitializer]
    public static void IAmGay()
    {
        MemoryPackFormatterProvider.Register(new MemoryPackFormatterForPubSub());
    }

    public override void Serialize<TBufferWriter>(ref MemoryPackWriter<TBufferWriter> writer, scoped ref PubSubSubscriptionState? value)
        => writer.WriteString(JsonConvert.SerializeObject(value));

    public override void Deserialize(ref MemoryPackReader reader, scoped ref PubSubSubscriptionState? value)
        => value = JsonConvert.DeserializeObject<PubSubSubscriptionState>(reader.ReadString()!);
}