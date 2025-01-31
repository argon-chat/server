namespace Argon.Features.Rpc;

using Orleans.Streams;

public sealed class ServerArgonStream<T>(IAsyncStream<IArgonEvent> stream, ILogger<IArgonStream<T>> logger) : IArgonStream<T> where T : IArgonEvent
{
    public Task OnNextAsync(T item, StreamSequenceToken? token = null)
        => stream.OnNextAsync(item, token);

    public Task OnCompletedAsync()
        => stream.OnCompletedAsync();
    public Task OnErrorAsync(Exception ex)
        => stream.OnErrorAsync(ex);

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default)
        => throw new NotImplementedException($"Server stream cannot create async enumerator");

    public async ValueTask DisposeAsync() { }

    public async ValueTask Fire(T ev)
    {
        logger.LogInformation("Success write '{eventName}' to orleans stream", ev.GetType().Name);
        await OnNextAsync(ev);
    }
}