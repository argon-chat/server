namespace Argon.Api.Features.Bus;

using Argon.Features.NatsStreaming;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

public interface IStreamExtension
{
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary);
    ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary, long sequence, int eventId);


    ValueTask<IAsyncEnumerable<IArgonEvent>> GetOrCreateSubscriptionCoupler(Guid sessionId, Guid primary, CancellationToken ct = default);
    ValueTask AssignSubscribe(Guid sessionId, Guid primary);
}

public interface IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStream();
    ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId);
}

public static class ArgonStreamExtensions
{
    public static IStreamExtension<T> Streams<T>(this T grain) where T : Grain, IGrainWithGuidKey
        => new StreamForGrainExtension<T>(grain);

    public static IStreamExtension Streams(this IClusterClient clusterClient)
        => new StreamForClusterClientExtension(clusterClient);
}

public readonly struct StreamForGrainExtension<T>(T grain) : IStreamExtension<T> where T : Grain, IGrainWithGuidKey
{
    public ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStream()
        => CreateServerStreamFor(grain.GetPrimaryKey());

    public async ValueTask<IDistributedArgonStream<IArgonEvent>> CreateServerStreamFor(Guid targetId)
        => await grain.GrainContext.ActivationServices
           .GetRequiredService<IStreamManagement>()
           .CreateServerStream(StreamId.Create("@", targetId));
}

public readonly struct StreamForClusterClientExtension(IClusterClient client) : IStreamExtension
{
    public async ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary)
        => await client.ServiceProvider
           .GetRequiredService<NatsContext>()
           .CreateReadStream(StreamId.Create("@", primary));

    public ValueTask<IArgonStream<IArgonEvent>> CreateClientStream(Guid primary, long sequence, int eventId)
        => CreateClientStream(primary);

    public async ValueTask<IAsyncEnumerable<IArgonEvent>> GetOrCreateSubscriptionCoupler(Guid sessionId, Guid primary,
        CancellationToken ct = default)
        => client.ServiceProvider.GetRequiredService<SubscriptionController>().StreamForSession(sessionId, primary, ct);

    public ValueTask AssignSubscribe(Guid sessionId, Guid primary)
    {
        client.ServiceProvider.GetRequiredService<SubscriptionController>().SubscribeTopic(sessionId, primary);
        return ValueTask.CompletedTask;
    }
}

public class SubscriptionController(IClusterClient client, ILogger<SubscriptionController> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, SessionContext> sessions = new();

    public async IAsyncEnumerable<IArgonEvent> StreamForSession(
        Guid sessionId,
        Guid userId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (sessions.ContainsKey(sessionId))
            await CleanupSessionAsync(sessionId);

        var ctx = new SessionContext(sessionId, userId, ct);
        sessions[sessionId] = ctx;

        try
        {
            SubscribeTopic(ctx, userId);

            await foreach (var ev in ctx.EventChannel.Reader.ReadAllAsync(ct))
                yield return ev;
        }
        finally
        {
            await CleanupSessionAsync(sessionId);
        }
    }

    public void SubscribeTopic(Guid sessionId, Guid topicId)
    {
        if (sessions.TryGetValue(sessionId, out var ctx))
            SubscribeTopic(ctx, topicId);
    }

    public void SubscribeTopic(SessionContext ctx, Guid topicId)
    {
        if (ctx.Topics.ContainsKey(topicId))
            return;

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ctx.SessionToken);
        
        if (!ctx.Topics.TryAdd(topicId, linkedCts))
        {
            // Another thread already added this topic
            linkedCts.Dispose();
            return;
        }

        _ = RunTopicSubscriptionAsync(ctx, topicId, linkedCts);
    }
    
    private async Task RunTopicSubscriptionAsync(SessionContext ctx, Guid topicId, CancellationTokenSource linkedCts)
    {
        IArgonStream<IArgonEvent>? stream = null;
        try
        {
            // Capture token before any await - if CTS is disposed, we'll get ObjectDisposedException here
            var token = linkedCts.Token;
            
            stream = await client.Streams().CreateClientStream(topicId);
            await foreach (var ev in stream.WithCancellation(token))
            {
                ctx.EventChannel.Writer.TryWrite(ev);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when topic is unsubscribed or session ends
        }
        catch (ObjectDisposedException)
        {
            // CancellationTokenSource was disposed - session was cleaned up
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in topic subscription for session {SessionId}, topic {TopicId}", 
                ctx.SessionId, topicId);
        }
        finally
        {
            if (stream is not null)
            {
                try
                {
                    await stream.DisposeAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Error disposing stream for topic {TopicId}", topicId);
                }
            }
            
            // Only dispose if we successfully removed it (wasn't already cleaned up)
            if (ctx.Topics.TryRemove(topicId, out var cts))
            {
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed by CleanupSessionAsync
                }
            }
        }
    }

    public void UnsubscribeTopic(SessionContext ctx, Guid topicId)
    {
        if (ctx.Topics.TryRemove(topicId, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed
            }
        }
    }

    private async ValueTask CleanupSessionAsync(Guid sessionId)
    {
        if (!sessions.TryRemove(sessionId, out var ctx)) 
            return;

        // Cancel first, then give tasks a moment to notice
        try
        {
            await ctx.SessionCts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }

        // Cancel all topic subscriptions
        foreach (var (_, cts) in ctx.Topics)
        {
            try
            {
                await cts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed by task
            }
        }
        
        // Small delay to let running tasks notice cancellation
        await Task.Delay(50);
        
        // Now dispose remaining CTS (tasks should have finished by now)
        foreach (var (topicId, cts) in ctx.Topics)
        {
            if (ctx.Topics.TryRemove(topicId, out _))
            {
                try
                {
                    cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed by task
                }
            }
        }
        
        try
        {
            ctx.SessionCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed
        }
        
        ctx.EventChannel.Writer.TryComplete();
        
        logger.LogDebug("Cleaned up session {SessionId}", sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        var sessionIds = sessions.Keys.ToArray();
        foreach (var sessionId in sessionIds)
        {
            await CleanupSessionAsync(sessionId);
        }
        
        logger.LogInformation("SubscriptionController disposed, cleaned up {Count} sessions", sessionIds.Length);
    }

    public record SessionContext(Guid SessionId, Guid UserId, CancellationToken SessionToken)
    {
        public Channel<IArgonEvent> EventChannel { get; } = Channel.CreateUnbounded<IArgonEvent>();
        public ConcurrentDictionary<Guid, CancellationTokenSource> Topics { get; } = new();

        public CancellationTokenSource SessionCts { get; } = CancellationTokenSource.CreateLinkedTokenSource(SessionToken);
    }
}
