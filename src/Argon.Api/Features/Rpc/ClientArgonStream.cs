namespace Argon.Api.Features.Rpc;

using System.Threading.Channels;
using ActualLab.Rpc;
using Contracts;
using global::Orleans.Streams;

public interface IArgonStream<T> : IAsyncObserver<T>, IAsyncEnumerable<T>, IAsyncDisposable
{
    public RpcStream<T> AsRpcStream() => new(this);
    ValueTask           Fire(T ev);
}

public static class ArgonStreamExtensions
{
    public static IStreamExtension<T> Streams<T>(this T grain) where T : Grain, IGrainWithGuidKey => new StreamForGrainExtension<T>(grain);

    public static IStreamExtension Streams(this IClusterClient clusterClient) => new StreamForClusterClientExtension(clusterClient);
}

public interface IStreamExtension
{
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary);
}

public interface IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    ValueTask<IArgonStream<IArgonEvent>> CreateServerStream();
}

public readonly struct StreamForGrainExtension<T>(T grain) : IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    public async ValueTask<IArgonStream<IArgonEvent>> CreateServerStream() => new ServerArgonStream<IArgonEvent>(grain
       .GetStreamProvider(IArgonEvent.ProviderId).GetStream<IArgonEvent>(StreamId.Create(IArgonEvent.Namespace, grain.GetPrimaryKey())));
}

public readonly struct StreamForClusterClientExtension(IClusterClient? client) : IStreamExtension
{
    public ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary) =>
        new ClientArgonStream<IArgonEvent>().BindClient(client.GetStreamProvider(IArgonEvent.ProviderId)
           .GetStream<IArgonEvent>(StreamId.Create(IArgonEvent.Namespace, primary)));
}

public sealed class ClientArgonStream<T> : IArgonStream<T>
{
    private StreamSubscriptionHandle<T> clientHandler { get; set; }
    private Channel<T>                  channel       { get; } = Channel.CreateUnbounded<T>();

    public async Task OnNextAsync(T item, StreamSequenceToken? token = null) => await channel.Writer.WriteAsync(item);

    public Task OnCompletedAsync()
    {
        channel.Writer.Complete();
        return Task.CompletedTask;
    }

    public Task OnErrorAsync(Exception ex)
    {
        channel.Writer.Complete(ex);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
    {
        while (await channel.Reader.WaitToReadAsync(ct))
        {
            while (channel.Reader.TryRead(out var serverEvent))
                yield return serverEvent;
        }
    }

    public async ValueTask DisposeAsync() => await clientHandler.UnsubscribeAsync();

    public ValueTask Fire(T ev) => throw new NotImplementedException("Client stream cannot be fire event");


    internal async ValueTask<IArgonStream<T>> BindClient(IAsyncStream<T> stream)
    {
        clientHandler = await stream.SubscribeAsync(this);
        return this;
    }
}

public sealed class ServerArgonStream<T>(IAsyncStream<IArgonEvent> stream) : IArgonStream<T> where T : IArgonEvent
{
    public Task OnNextAsync(T item, StreamSequenceToken? token = null) => stream.OnNextAsync(item, token);

    public Task OnCompletedAsync() => stream.OnCompletedAsync();

    public Task OnErrorAsync(Exception ex) => stream.OnErrorAsync(ex);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default) =>
        throw new NotImplementedException("Server stream cannot create async enumerator");

    public async ValueTask DisposeAsync() { } // nothing any to dispose

    public async ValueTask Fire(T ev) => await OnNextAsync(ev);
}