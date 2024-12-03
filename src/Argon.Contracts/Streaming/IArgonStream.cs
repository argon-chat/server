namespace Argon.Streaming;

using Orleans.Streams;

public interface IArgonStream<T> :
    IAsyncObserver<T>, IAsyncEnumerable<T>, IAsyncDisposable where T : IArgonEvent
{
    ValueTask Fire(T ev);
}