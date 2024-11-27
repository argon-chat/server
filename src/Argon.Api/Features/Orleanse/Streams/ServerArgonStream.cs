namespace Argon.Api.Features.Rpc;

using Contracts;
using Orleans.Streams;

public sealed class ServerArgonStream<T>(IAsyncStream<IArgonEvent> stream) : IArgonStream<T> where T : IArgonEvent
{
    public Task OnNextAsync(T item, StreamSequenceToken? token = null)
        => stream.OnNextAsync(item, token);

    public Task OnCompletedAsync()
        => stream.OnCompletedAsync();
    public Task OnErrorAsync(Exception ex)
        => stream.OnErrorAsync(ex);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => throw new NotImplementedException($"Server stream cannot create async enumerator");

    public async ValueTask DisposeAsync() {} // nothing any to dispose

    public async ValueTask Fire(T ev)
        => await OnNextAsync(ev);
}