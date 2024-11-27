namespace Argon.Api.Features.Rpc;

using Contracts;

public interface IStreamExtension
{
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary);
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
           .GetStream<IArgonEvent>(StreamId.Create(IArgonEvent.Namespace, targetId)));
}

public readonly struct StreamForClusterClientExtension(IClusterClient? client) : IStreamExtension
{
    public ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary)
        => new ClientArgonStream<IArgonEvent>().BindClient(client
           .GetStreamProvider(IArgonEvent.ProviderId)
           .GetStream<IArgonEvent>(StreamId.Create(IArgonEvent.Namespace, primary)));
}