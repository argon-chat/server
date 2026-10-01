namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// The "Streaming on Twitch" activity for one user, under the <c>twitch</c> provider slot: set when
/// EventSub says the stream went online, read again on a slow clock for its title, retracted when
/// it went offline.
/// </summary>
/// <remarks>
/// Every member is one-way, for the same reason as <see cref="ISpotifyPresenceGrain"/>: the
/// callers may be grains this one awaits.
/// </remarks>
[Alias("Argon.Grains.Interfaces.ITwitchPresenceGrain")]
public interface ITwitchPresenceGrain : IGrainWithGuidKey
{
    /// <summary>EventSub: the stream went online. The stream is read for its title and published.</summary>
    [OneWay, Alias(nameof(StreamOnlineAsync))]
    Task StreamOnlineAsync();

    /// <summary>EventSub: the stream went offline.</summary>
    [OneWay, Alias(nameof(StreamOfflineAsync))]
    Task StreamOfflineAsync();

    /// <summary>A session attached or the option was turned on: read the stream once and publish if live.</summary>
    [OneWay, Alias(nameof(WakeAsync))]
    Task WakeAsync();

    /// <summary>The connection is gone or no longer shown as status: retract and stop.</summary>
    [OneWay, Alias(nameof(StopAsync))]
    Task StopAsync();
}
