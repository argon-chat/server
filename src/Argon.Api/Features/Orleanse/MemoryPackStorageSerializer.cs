namespace Argon.Api.Features;

using Orleans.Storage;

internal class MemoryPackStorageSerializer : IGrainStorageSerializer
{
    public BinaryData Serialize<T>(T input) 
        => new(MemoryPackSerializer.Serialize(input));

    public T Deserialize<T>(BinaryData input) 
        => MemoryPackSerializer.Deserialize<T>(input) ?? throw new InvalidOperationException();
}