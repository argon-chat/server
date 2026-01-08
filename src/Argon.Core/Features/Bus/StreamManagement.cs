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

public class StreamManagement(IServiceProvider serviceProvider, ILogger<StreamManagement> logger) : IStreamManagement, IDisposable
{
    private readonly ConcurrentDictionary<StreamId, ServerStreamEntry<IArgonEvent>> cache = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId)
        => CreateServerStream(StreamId.Create("@", targetId));

    public async ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStream(StreamId streamId)
    {
        await _semaphore.WaitAsync();
        try
        {
            if (!cache.TryGetValue(streamId, out var entry))
            {
                var stream = await serviceProvider
                   .GetRequiredService<NatsContext>()
                   .CreateWriteStream(streamId);

                entry = new ServerStreamEntry<IArgonEvent>(stream, streamId, OnEntryEmpty, logger);
                cache[streamId] = entry;
                logger.LogDebug("Created new write stream for {StreamId}", streamId);
            }

            entry.Increment();
            return new DistributedArgonStream<IArgonEvent>(entry);
        }
        finally
        {
            _semaphore.Release();
        }
    }
    
    private void OnEntryEmpty(StreamId streamId)
    {
        cache.TryRemove(streamId, out _);
        logger.LogDebug("Removed write stream {StreamId} from cache (no references)", streamId);
    }
    
    public void Dispose()
    {
        _semaphore.Dispose();
    }

    private sealed class DistributedArgonStream<T>(ServerStreamEntry<T> entry) : IDistributedArgonStream<T> where T : IArgonEvent
    {
        private int _disposed;
        
        public IArgonStream<T> Stream => entry.Stream;

        public ValueTask Fire(T ev, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(_disposed == 1, this);
            return Stream.Fire(ev, ct);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;
            
            await entry.DecrementAsync();
        }
    }

    private sealed class ServerStreamEntry<T>(
        IArgonStream<T> stream, 
        StreamId streamId, 
        Action<StreamId> onEmpty,
        ILogger logger) where T : IArgonEvent
    {
        private readonly object _lock = new();
        
        public  IArgonStream<T> Stream { get; } = stream;
        private int             _refCount;

        public void Increment()
        {
            lock (_lock)
            {
                _refCount++;
            }
        }

        public async ValueTask DecrementAsync()
        {
            bool shouldDispose;
            lock (_lock)
            {
                _refCount--;
                shouldDispose = _refCount == 0;
            }

            if (!shouldDispose)
                return;

            try
            {
                await Stream.DisposeAsync();
                logger.LogDebug("Disposed write stream for {StreamId}", streamId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing write stream for {StreamId}", streamId);
            }

            onEmpty(streamId);
        }
    }
}