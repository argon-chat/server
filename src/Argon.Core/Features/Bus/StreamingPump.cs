namespace Argon.Features.Bus;

using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NatsStreaming;
using Services.Ion;

public static class StreamingPumpEx
{
    public static void AddStreamingPump(this IServiceCollection collection)
        => collection.AddSingleton<PumpStreamStorage>();

    public static IPumpingStreamStore Streams(this IIonService ionService)
        => ArgonRequestContext.Current.Scope.GetRequiredService<IPumpingStreamStore>();
}


internal class StreamAdapter(IServiceProvider provider, StreamId streamId, ILogger<StreamAdapter> logger) : IStreamAdapter<IArgonEvent>
{
    public async IAsyncEnumerable<IArgonEvent> BeginStream([EnumeratorCancellation] CancellationToken ct = default)
    {
        var stream = await provider
           .GetRequiredService<NatsContext>()
           .CreateReadStream(streamId, ct);
        
        try
        {
            await foreach (var e in stream.WithCancellation(ct))
                yield return e;
        }
        finally
        {
            try
            {
                await stream.DisposeAsync();
                logger.LogDebug("Disposed read stream for {StreamId}", streamId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error disposing read stream for {StreamId}", streamId);
            }
        }
    }
}

public interface IPumpingStreamStore
{
    IStreamingPump<IArgonEvent> GetStreamFor(StreamId streamId);
}

public class PumpStreamStorage(ILogger<IStreamingPump<IArgonEvent>> logger, IServiceProvider provider) : IPumpingStreamStore
{
    private readonly ConcurrentDictionary<StreamId, IStreamingPump<IArgonEvent>> pumps = new();

    public IStreamingPump<IArgonEvent> GetStreamFor(StreamId streamId)
    {
        return pumps.GetOrAdd(streamId, static (id, state) => 
        {
            var adapterLogger = state.provider.GetRequiredService<ILogger<StreamAdapter>>();
            return new StreamingPump<IArgonEvent>(state.logger, new StreamAdapter(state.provider, id, adapterLogger), id);
        }, (logger, provider));
    }
}

public interface IStreamAdapter<out T>
{
    IAsyncEnumerable<T> BeginStream([EnumeratorCancellation] CancellationToken ct = default);
}

public interface IStreamingPump<out T>
{
    IAsyncEnumerable<T> Subscribe(CancellationToken ct = default);
    
    bool IsIdle { get; }
}

public sealed class StreamingPump<T>(ILogger<IStreamingPump<T>> logger, IStreamAdapter<T> sourceFactory, StreamId streamId) : IStreamingPump<T>
{
    private readonly ConcurrentDictionary<int, Channel<T>> subscribers = new();
    private readonly object _lock = new();

    private int subscriberCount;
    private int nextId;

    private CancellationTokenSource? pumpCts;
    private Task?                    pumpTask;

    public bool IsIdle => subscriberCount == 0;

    public IAsyncEnumerable<T> Subscribe(CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref nextId);

        var channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            SingleWriter                  = true,
            SingleReader                  = true,
            AllowSynchronousContinuations = false
        });

        subscribers.TryAdd(id, channel);

        if (Interlocked.Increment(ref subscriberCount) == 1)
            StartPumpIfNeeded();
        
        logger.LogDebug("Subscriber {SubscriberId} joined stream {StreamId}, total: {Count}", id, streamId, subscriberCount);
        return ReadLoop(id, channel.Reader, ct);
    }

    private async IAsyncEnumerable<T> ReadLoop(
        int id,
        ChannelReader<T> reader,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var item in reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return item;
        }
        finally
        {
            if (subscribers.TryRemove(id, out var ch))
                ch.Writer.TryComplete();
            
            var remaining = Interlocked.Decrement(ref subscriberCount);
            logger.LogDebug("Subscriber {SubscriberId} left stream {StreamId}, remaining: {Count}", id, streamId, remaining);
            
            if (remaining == 0)
                StopPumpIfIdle();
        }
    }

    private void StartPumpIfNeeded()
    {
        lock (_lock)
        {
            if (pumpCts != null)
                return;
            
            logger.LogInformation("Starting pump for stream {StreamId}", streamId);
            var cts = new CancellationTokenSource();
            pumpCts = cts;

            var task = PumpAsync(cts.Token);
            pumpTask = task;

            _ = task.ContinueWith(t =>
            {
                if (t.IsFaulted)
                    logger.LogWarning(t.Exception, "Pump for stream {StreamId} has failed", streamId);
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private void StopPumpIfIdle()
    {
        lock (_lock)
        {
            var cts = pumpCts;
            if (cts == null) 
                return;

            pumpCts = null;
            
            try
            {
                cts.Cancel();
            }
            finally
            {
                cts.Dispose();
                logger.LogInformation("Pump for stream {StreamId} stopped - no active readers", streamId);
            }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in sourceFactory.BeginStream(ct).WithCancellation(ct).ConfigureAwait(false))
            {
                foreach (var ch in subscribers.Values)
                    ch.Writer.TryWrite(item);
            }

            foreach (var ch in subscribers.Values)
                ch.Writer.TryComplete();
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Pump for stream {StreamId} encountered an error", streamId);
            foreach (var ch in subscribers.Values)
                ch.Writer.TryComplete(new OperationCanceledException("pump faulted"));
            throw;
        }
        finally
        {
            lock (_lock)
            {
                pumpTask = null;
            }
        }
    }
}