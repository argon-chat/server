namespace Argon.Streaming;

public interface IArgonStream<T> :
    IAsyncEnumerable<T>, IAsyncDisposable where T : IArgonEvent
{
    ValueTask Fire(T ev, CancellationToken ct = default);
}