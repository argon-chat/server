namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// The "Listening to Spotify" activity for one user: polls Spotify while the user is online and
/// shows the connection as status, and publishes what it finds under the <c>spotify</c> provider
/// slot of the presence pipeline.
/// </summary>
/// <remarks>
/// <para>Every mutator is <c>[OneWay]</c> on purpose. The grains that call them —
/// <c>UserSessionGrain</c> on attach and detach, <c>UserGrain</c> on a client's bare LISTEN,
/// <c>UserConnectionsGrain</c> when an option flips — may at that moment be the ones this grain is
/// awaiting (it reads its grant from the connections grain on every tick), and an awaited call in
/// the other direction would be a deadlock. A one-way call is queued behind the current turn and
/// costs the caller nothing.</para>
///
/// <para>The grain stops itself: when the user has no live session, when the connection is gone or
/// the option is off, when the grant is refused, or after
/// <see cref="Argon.Features.Integrations.Connections.SpotifyConnectionOptions.IdleStopAfter"/> of
/// nothing playing. A session attach or a hint wakes it again.</para>
/// </remarks>
[Alias("Argon.Grains.Interfaces.ISpotifyPresenceGrain")]
public interface ISpotifyPresenceGrain : IGrainWithGuidKey
{
    /// <summary>A session attached, or the option was turned on: read now, keep reading while it is worth it.</summary>
    [OneWay, Alias(nameof(WakeAsync))]
    Task WakeAsync();

    /// <summary>
    /// The desktop read a title off the OS media session. One read per track change instead of a
    /// poll every few seconds, for a user whose desktop is the thing playing.
    /// </summary>
    [OneWay, Alias(nameof(HintAsync))]
    Task HintAsync(string titleName);

    /// <summary>A session detached; the next read decides whether anyone is left online.</summary>
    [OneWay, Alias(nameof(SessionEndedAsync))]
    Task SessionEndedAsync();

    /// <summary>The connection is gone or no longer shown as status: retract and stop.</summary>
    [OneWay, Alias(nameof(StopAsync))]
    Task StopAsync();

    /// <summary>What is published right now, for listen-along.</summary>
    [Alias(nameof(GetCurrentAsync))]
    Task<SpotifyTrack?> GetCurrentAsync();

    /// <summary>
    /// How many people are listening along. Above zero the poller reads at the listen-along cadence
    /// and reports every change to <c>IListenAlongGrain</c>; at zero it stops reporting.
    /// </summary>
    [OneWay, Alias(nameof(ListenersChangedAsync))]
    Task ListenersChangedAsync(int count);
}
