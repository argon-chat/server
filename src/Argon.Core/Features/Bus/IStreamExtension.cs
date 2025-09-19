namespace Argon.Api.Features.Bus;

using Argon.Features.NatsStreaming;

public interface IStreamExtension
{
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary);
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary, long sequence, int eventId);
}

public interface IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStream();
    ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId);
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
    public ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStream()
        => CreateServerStreamFor(grain.GetPrimaryKey());

    public async ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId)
        => await grain.GrainContext.ActivationServices
           .GetRequiredService<IStreamManagement>()
           .CreateServerStream(StreamId.Create("@", targetId));
}

public readonly struct StreamForClusterClientExtension(IClusterClient client) : IStreamExtension
{
    public async ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary)
        => await client.ServiceProvider
           .GetRequiredService<NatsContext>()
           .CreateReadStream(StreamId.Create("@", primary));

    public ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary, long sequence, int eventId)
        => CreateClientStream(primary);
}
