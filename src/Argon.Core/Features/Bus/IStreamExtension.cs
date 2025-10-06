namespace Argon.Api.Features.Bus;

using Argon.Features.NatsStreaming;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Consul;

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

public class SubscriptionController(IClusterClient client)
{
    private readonly ConcurrentDictionary<Guid, SessionContext> sessions = new();

    public async IAsyncEnumerable<IArgonEvent> StreamForSession(
        Guid sessionId,
        Guid userId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (sessions.ContainsKey(sessionId))
            CleanupSession(sessionId);

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
            CleanupSession(sessionId);
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
        ctx.Topics[topicId] = linkedCts;

        _ = Task.Run(async () => {
            try
            {
                var stream = await client.Streams().CreateClientStream(topicId);
                await foreach (var ev in stream.WithCancellation(linkedCts.Token))
                {
                    ctx.EventChannel.Writer.TryWrite(ev);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                ctx.Topics.TryRemove(topicId, out _);
            }
        }, linkedCts.Token);
    }

    public void UnsubscribeTopic(SessionContext ctx, Guid topicId)
    {
        if (ctx.Topics.TryRemove(topicId, out var cts))
            cts.Cancel();
    }

    private void CleanupSession(Guid sessionId)
    {
        if (!sessions.TryRemove(sessionId, out var ctx)) return;

        ctx.SessionCts.Cancel();

        foreach (var cts in ctx.Topics.Values)
            cts.Cancel();

        ctx.EventChannel.Writer.TryComplete();
    }

    public record SessionContext(Guid SessionId, Guid UserId, CancellationToken SessionToken)
    {
        public Channel<IArgonEvent> EventChannel { get; } = Channel.CreateUnbounded<IArgonEvent>();
        public ConcurrentDictionary<Guid, CancellationTokenSource> Topics { get; } = new();

        public CancellationTokenSource SessionCts { get; } = CancellationTokenSource.CreateLinkedTokenSource(SessionToken);
    }
}
