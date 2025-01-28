namespace Argon.Features.Rpc;

using Orleans.Providers.Streams.Common;

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
        => new ServerArgonStream<IArgonEvent>(grain.GetStreamProvider(IArgonEvent.ProviderId)
           .GetStream<IArgonEvent>(StreamId.Create(IArgonEvent.Namespace, targetId)), grain.GrainContext.ActivationServices.GetRequiredService<ILogger<IArgonStream<IArgonEvent>>>());
}

public readonly struct StreamForClusterClientExtension(IClusterClient? client) : IStreamExtension
{
    public ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary)
        => new ClientArgonStream<IArgonEvent>().BindClient(client
           .GetStreamProvider(IArgonEvent.ProviderId)
           .GetStream<IArgonEvent>(StreamId.Create(IArgonEvent.Namespace, primary)));

    public ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary, long sequence, int eventId)
    {
        if (sequence < 0 || eventId < 0)
            return CreateClientStream(primary);
        return new ClientArgonStream<IArgonEvent>().BindClient(client
           .GetStreamProvider(IArgonEvent.ProviderId)
           .GetStream<IArgonEvent>(StreamId.Create(IArgonEvent.Namespace, primary)), new EventSequenceToken(sequence, eventId));
    }
}