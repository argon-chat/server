namespace Argon.Features.Rpc;

using NatsStreaming;

public interface IStreamExtension
{
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary);
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary, long sequence, int eventId);
}

public interface IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    ValueTask<IArgonStream<IArgonEvent>> CreateServerStream();
    ValueTask<IArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId);
}
public static class ArgonStreamExtensions
{
    public static IStreamExtension<T> Streams<T>(this T grain) where T : Grain, IGrainWithGuidKey
        => new StreamForGrainExtension<T>(grain);

    public static IStreamExtension Streams(this IClusterClient clusterClient)
        => new StreamForClusterClientExtension(clusterClient);
}
public readonly struct StreamForGrainExtension<T>(T grain) : IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    public ValueTask<IArgonStream<IArgonEvent>> CreateServerStream()
        => CreateServerStreamFor(grain.GetPrimaryKey());

    public async ValueTask<IArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId)
        => await grain.GrainContext.ActivationServices
           .GetRequiredService<NatsContext>()
           .CreateWriteStream(StreamId.Create(IArgonEvent.Namespace, targetId));
}

public readonly struct StreamForClusterClientExtension(IClusterClient client) : IStreamExtension
{
    public async ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary)
        => await client.ServiceProvider
           .GetRequiredService<NatsContext>()
           .CreateReadStream(StreamId.Create(IArgonEvent.Namespace, primary));

    public ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary, long sequence, int eventId)
        => CreateClientStream(primary);
}