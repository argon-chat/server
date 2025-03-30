namespace Argon.Features.Rpc;

using Orleans.Streams;
using System.Threading.Channels;

public sealed class ClientArgonStream<T> : IArgonStream<T> where T : IArgonEvent
{
    private StreamSubscriptionHandle<T> clientHandler { get; set; }
    private Channel<T>                  channel       { get; } = Channel.CreateUnbounded<T>();
    public async Task OnNextAsync(T item, StreamSequenceToken? token = null)
    {
        //if (token is not null)
        //{
        //    item.Sequence = token.SequenceNumber == 0 ? null : token.SequenceNumber;
        //    item.EventId  = token.EventIndex == 0 ? null : token.EventIndex;
        //}

        await channel.Writer.WriteAsync(item);
    }

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
            yield return await channel.Reader.ReadAsync(ct);
    }

    public async ValueTask DisposeAsync()
        => await clientHandler.UnsubscribeAsync();


    internal async ValueTask<IArgonStream<T>> BindClient(IAsyncStream<T> stream)
    {
        clientHandler = await stream.SubscribeAsync(this);
        return this;
    }

    internal async ValueTask<IArgonStream<T>> BindClient(IAsyncStream<T> stream, StreamSequenceToken token)
    {
        clientHandler = await stream.SubscribeAsync(this, token);
        return this;
    }

    public ValueTask Fire(T ev)
        => throw new NotImplementedException($"Client stream cannot be fire event");
}