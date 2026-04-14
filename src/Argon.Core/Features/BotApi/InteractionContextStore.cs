namespace Argon.Features.BotApi;

using System.Collections.Concurrent;

/// <summary>
/// In-memory TTL cache mapping interaction IDs to invoking user context.
/// Populated when interaction events are published (command/control/select),
/// consumed when the bot responds (Ack/Defer/Modal).
/// Entries expire after <see cref="TtlSeconds"/> seconds via lazy cleanup.
/// </summary>
public sealed class InteractionContextStore
{
    public const int TtlSeconds     = 60;
    private const int PruneInterval = 100;

    private readonly ConcurrentDictionary<Guid, InteractionContext> _store = new();
    private int _registerCount;

    public sealed record InteractionContext(
        Guid     UserId,
        Guid     ChannelId,
        Guid     SpaceId,
        Guid     BotAppId,
        DateTime CreatedAt);

    /// <summary>
    /// Registers an interaction context. Called after publishing an interaction event.
    /// </summary>
    public void Register(Guid interactionId, Guid userId, Guid channelId, Guid spaceId, Guid botAppId)
    {
        _store[interactionId] = new InteractionContext(userId, channelId, spaceId, botAppId, DateTime.UtcNow);

        // Lazy prune: every N registrations, sweep stale entries
        if (Interlocked.Increment(ref _registerCount) % PruneInterval == 0)
            PruneStale();
    }

    /// <summary>
    /// Consumes an interaction context — returns and removes it. Returns null if not found or expired.
    /// </summary>
    public InteractionContext? TryConsume(Guid interactionId)
    {
        if (!_store.TryRemove(interactionId, out var ctx))
            return null;

        if ((DateTime.UtcNow - ctx.CreatedAt).TotalSeconds > TtlSeconds)
            return null;

        return ctx;
    }

    /// <summary>
    /// Peeks at an interaction context without removing it. Returns null if not found or expired.
    /// </summary>
    public InteractionContext? TryPeek(Guid interactionId)
    {
        if (!_store.TryGetValue(interactionId, out var ctx))
            return null;

        if ((DateTime.UtcNow - ctx.CreatedAt).TotalSeconds > TtlSeconds)
        {
            _store.TryRemove(interactionId, out _);
            return null;
        }

        return ctx;
    }

    private void PruneStale()
    {
        var now = DateTime.UtcNow;
        foreach (var (key, ctx) in _store)
        {
            if ((now - ctx.CreatedAt).TotalSeconds > TtlSeconds)
                _store.TryRemove(key, out _);
        }
    }
}
