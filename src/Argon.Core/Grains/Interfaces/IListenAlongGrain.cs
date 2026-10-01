namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

public sealed record ListenAlongJoin(ListenAlongError Error, ListenAlongState? State)
{
    public static ListenAlongJoin Refused(ListenAlongError error) => new(error, null);
}

/// <summary>
/// One host's listen-along party: who is listening, and the commands that keep each listener's
/// Spotify on the host's track.
/// </summary>
/// <remarks>
/// <para>Keyed by the host. Server-driven: the listener's token never leaves the silo, and the
/// server issues <c>play</c>, <c>pause</c> and <c>seek</c> to the listener's active device on every
/// change the host's poller reports. Drift between those events is accepted, as Discord accepts it;
/// polling every listener to measure it would double the budget for a second of alignment.</para>
///
/// <para>The listeners are in-memory state of this activation. A silo restart ends the party and
/// the clients re-join on the event, which is the cheap correct answer.</para>
///
/// <para><see cref="OnHostChangedAsync"/> is one-way: it is called from the host's
/// <c>ISpotifyPresenceGrain</c>, which this grain calls on a join, and an awaited call each way is
/// a deadlock.</para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.IListenAlongGrain")]
public interface IListenAlongGrain : IGrainWithGuidKey
{
    [Alias(nameof(JoinAsync))]
    Task<ListenAlongJoin> JoinAsync(Guid listenerId, CancellationToken ct = default);

    [Alias(nameof(LeaveAsync))]
    Task LeaveAsync(Guid listenerId, ListenAlongEndReason reason, CancellationToken ct = default);

    /// <summary>The party, or null when nobody is listening.</summary>
    [Alias(nameof(GetStateAsync))]
    Task<ListenAlongState?> GetStateAsync();

    /// <summary>The host's poller read something new; null means the host stopped.</summary>
    [OneWay, Alias(nameof(OnHostChangedAsync))]
    Task OnHostChangedAsync(SpotifyTrack? track);
}
