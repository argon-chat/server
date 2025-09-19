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
        => ArgonRequestContext.Current.Scope.ServiceProvider.GetRequiredService<IPumpingStreamStore>();
}


internal class StreamAdapter(IServiceProvider provider, StreamId streamId) : IStreamAdapter<IArgonEvent>
{
    public async IAsyncEnumerable<IArgonEvent> BeginStream(CancellationToken ct = default)
    {
        var stream = await provider
           .GetRequiredService<NatsContext>()
           .CreateReadStream(streamId, ct);
        await foreach (var e in stream.WithCancellation(ct))
            yield return e;
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
        if (pumps.TryGetValue(streamId, out var pump))
            return pump;
        pumps.TryAdd(streamId, new StreamingPump<IArgonEvent>(logger, new StreamAdapter(provider, streamId)));
        if (pumps.TryGetValue(streamId, out pump))
            return pump;
        throw new InvalidAsynchronousStateException();
    }
}

public interface IStreamAdapter<out T>
{
    IAsyncEnumerable<T> BeginStream([EnumeratorCancellation] CancellationToken ct = default);
}

public interface IStreamingPump<out T>
{
    IAsyncEnumerable<T> Subscribe(CancellationToken ct = default);
}

public sealed class StreamingPump<T>(ILogger<IStreamingPump<T>> logger, IStreamAdapter<T> sourceFactory) : IStreamingPump<T>
{
    private readonly ConcurrentDictionary<int, Channel<T>> subscribers = new();

    private int subscriberCount;
    private int nextId;

    private CancellationTokenSource? pumpCts;
    private Task?                    pumpTask;

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
        logger.LogInformation("Begin stream reading...");
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
            logger.LogInformation("Stop stream reading...");
            if (Interlocked.Decrement(ref subscriberCount) == 0)
                StopPumpIfIdle();
        }
    }

    private void StartPumpIfNeeded()
    {
        logger.LogInformation("Pump started...");
        var cts      = new CancellationTokenSource();
        var existing = Interlocked.CompareExchange(ref pumpCts, cts, null);
        if (existing != null)
        {
            cts.Dispose();
            return;
        }

        var task = PumpAsync(cts.Token);
        pumpTask = task;

        _ = task.ContinueWith(t =>
        {
            var _ = t.Exception;
            logger.LogWarning(t.Exception, "Pump has failed");
        }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    private void StopPumpIfIdle()
    {
        var cts = Interlocked.Exchange(ref pumpCts, null);
        if (cts == null) return;

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
            logger.LogInformation("Pump has stopped because no active reader");
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
        }
        catch (Exception)
        {
            foreach (var ch in subscribers.Values)
                ch.Writer.TryComplete(new OperationCanceledException("pump faulted"));
            throw;
        }
        finally
        {
            pumpTask = null;
        }
    }
}