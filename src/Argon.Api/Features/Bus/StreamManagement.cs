namespace Argon.Api.Features.Bus;

using Argon.Features.NatsStreaming;

public interface IStreamManagement
{
    ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId);
    ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStream(StreamId steamId);
}

public interface IDistributedArgonStream<T> : IAsyncDisposable where T : IArgonEvent
{
    IArgonStream<T> Stream { get; }

    ValueTask Fire(T ev, CancellationToken ct = default);
}

public class StreamManagement(IServiceProvider serviceProvider) : IStreamManagement
{
    private readonly ConcurrentDictionary<StreamId, ServerStreamEntry<IArgonEvent>> cache = new();

    private readonly AsyncLock guarder = new();

    public ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId)
        => CreateServerStream(StreamId.Create(IArgonEvent.Namespace, targetId));

    public async ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStream(StreamId streamId)
    {
        using (await guarder.WaitAsync())
        {
            if (!cache.TryGetValue(streamId, out var entry))
            {
                var stream = await serviceProvider
                   .GetRequiredService<NatsContext>()
                   .CreateWriteStream(streamId);

                entry           = new ServerStreamEntry<IArgonEvent>(stream, () => cache.TryRemove(streamId, out _));
                cache[streamId] = entry;
            }

            entry.Increment();
            return new DistributedArgonStream<IArgonEvent>(entry);
        }
    }

    private sealed class DistributedArgonStream<T>(ServerStreamEntry<T> entry) : IDistributedArgonStream<T> where T : IArgonEvent
    {
        public IArgonStream<T> Stream => entry.Stream;

        public ValueTask Fire(T ev, CancellationToken ct = default)
            => Stream.Fire(ev, ct);

        public ValueTask DisposeAsync()
            => entry.DecrementAsync();
    }

    private sealed class ServerStreamEntry<T>(IArgonStream<T> stream, Action onEmpty) where T : IArgonEvent
    {
        public  IArgonStream<T> Stream { get; } = stream;
        private int             _refCount;

        public void Increment() => Interlocked.Increment(ref _refCount);

        public async ValueTask DecrementAsync()
        {
            if (Interlocked.Decrement(ref _refCount) != 0)
                return;

            await Stream.DisposeAsync();

            onEmpty();
        }
    }

    private sealed class AsyncLock
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public async Task<IDisposable> WaitAsync()
        {
            await _semaphore.WaitAsync();
            return new Releaser(_semaphore);
        }

        private sealed class Releaser(SemaphoreSlim semaphore) : IDisposable
        {
            public void Dispose() => semaphore.Release();
        }
    }
}