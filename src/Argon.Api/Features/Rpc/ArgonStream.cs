namespace Argon.Api.Features.Rpc;

using Orleans.Streams;
using System.Threading.Channels;
using ActualLab.Rpc;
using Contracts;

public interface IArgonStream<T> : 
    IAsyncObserver<T>, IAsyncEnumerable<T>, IAsyncDisposable where T : ArgonEvent<T>, IArgonEvent
{
    public RpcStream<T> AsRpcStream()
        => new(this);

    

    public static ValueTask<IArgonStream<T>> CreateClientStream(IClusterClient cluster, Guid primary)
        => new ArgonStream<T>().Bind(cluster
           .GetStreamProvider(T.ProviderId)
           .GetStream<T>(StreamId.Create(T.Namespace, primary)));
}

public static class ArgonStreamExtensions
{
    public static IStreamExtension<T> Streams<T>(this T grain) where T : Grain, IGrainWithGuidKey
        => new StreamExtension<T>(grain);
}

public interface IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    IAsyncStream<E> CreateServerStream<E>() where E : ArgonEvent<E>, IArgonEvent;
}

public readonly struct StreamExtension<T>(T grain) : IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    public IAsyncStream<E> CreateServerStream<E>() where E : ArgonEvent<E>, IArgonEvent
        => grain.GetStreamProvider(E.ProviderId)
           .GetStream<E>(StreamId.Create(E.Namespace, grain.GetPrimaryKey()));
}


public sealed class ArgonStream<T> : IArgonStream<T> where T : ArgonEvent<T>, IArgonEvent
{
    private StreamSubscriptionHandle<T> handler  { get; set; }
    private Channel<T>                  channel { get; } = Channel.CreateUnbounded<T>();
    public async Task OnNextAsync(T item, StreamSequenceToken? token = null)
        => await channel.Writer.WriteAsync(item);

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

    public async ValueTask DisposeAsync()
        => await handler.UnsubscribeAsync();

    internal async ValueTask<IArgonStream<T>> Bind(IAsyncStream<T> stream)
    {
        handler = await stream.SubscribeAsync(this);
        return this;
    }
}