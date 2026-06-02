namespace Argon.Features.Transport;

using System.Collections.Concurrent;

/// <summary>
/// Tracks which spaces have locally-connected SignalR clients.
/// Used by CrossDcFanoutService to manage NATS subscriptions.
/// </summary>
public interface ISpaceSubscriptionTracker
{
    void Increment(Guid spaceId);
    void Decrement(Guid spaceId);
    IReadOnlyCollection<Guid> GetActiveSpaces();
    event Action<Guid>? SpaceSubscribed;
    event Action<Guid>? SpaceUnsubscribed;
}

public sealed class SpaceSubscriptionTracker : ISpaceSubscriptionTracker
{
    private readonly ConcurrentDictionary<Guid, int> _refCounts = new();

    public event Action<Guid>? SpaceSubscribed;
    public event Action<Guid>? SpaceUnsubscribed;

    public void Increment(Guid spaceId)
    {
        var newCount = _refCounts.AddOrUpdate(spaceId, 1, (_, c) => c + 1);
        if (newCount == 1)
            SpaceSubscribed?.Invoke(spaceId);
    }

    public void Decrement(Guid spaceId)
    {
        if (!_refCounts.TryGetValue(spaceId, out var current)) return;

        var newCount = current - 1;
        if (newCount <= 0)
        {
            _refCounts.TryRemove(spaceId, out _);
            SpaceUnsubscribed?.Invoke(spaceId);
        }
        else
        {
            _refCounts.TryUpdate(spaceId, newCount, current);
        }
    }

    public IReadOnlyCollection<Guid> GetActiveSpaces()
        => _refCounts.Keys.ToList();
}
