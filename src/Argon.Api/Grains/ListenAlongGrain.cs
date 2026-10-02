namespace Argon.Grains;

using Argon.Core.Features.Transport;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Spotify;
using Argon.Grains.Interfaces;
using ion.runtime;

/// <inheritdoc cref="IListenAlongGrain"/>
public sealed class ListenAlongGrain(
    SpotifyPlayerClient player,
    ListenAlongDirectory directory,
    IOptions<ConnectionsOptions> options,
    AppHubServer appHubServer,
    ILogger<ListenAlongGrain> logger) : Grain, IListenAlongGrain
{
    private sealed class Listener
    {
        public int Failures;
    }

    private readonly Dictionary<Guid, Listener> listeners = new();

    private SpotifyTrack? hostTrack;

    private Guid                     HostId  => this.GetPrimaryKey();
    private SpotifyConnectionOptions Spotify => options.Value.Spotify;

    public async Task<ListenAlongJoin> JoinAsync(Guid listenerId, CancellationToken ct = default)
    {
        if (listenerId == HostId)
            return ListenAlongJoin.Refused(ListenAlongError.NOT_VISIBLE);

        if (!listeners.ContainsKey(listenerId) && listeners.Count >= Spotify.MaxListenersPerHost)
            return ListenAlongJoin.Refused(ListenAlongError.FULL);

        // The host's activity has to be visible to the listener: the same standing a profile lookup needs.
        if (!await GrainFactory.GetGrain<IIdentityDirectoryGrain>(Guid.Empty).CanReachAsync(listenerId, HostId, ct))
            return ListenAlongJoin.Refused(ListenAlongError.NOT_VISIBLE);

        var track = await GrainFactory.GetGrain<ISpotifyPresenceGrain>(HostId).GetCurrentAsync();

        if (track is null || !track.isPlaying)
            return ListenAlongJoin.Refused(ListenAlongError.HOST_NOT_PLAYING);

        if (!track.listenAlongOpen)
            return ListenAlongJoin.Refused(ListenAlongError.HOST_DISALLOWS);

        var grant = await GrainFactory.GetGrain<IUserConnectionsGrain>(listenerId).GetListenerGrantAsync(ct);

        if (grant is null)
            return ListenAlongJoin.Refused(ListenAlongError.SPOTIFY_NOT_LINKED);
        if (!grant.CanControlPlayback)
            return ListenAlongJoin.Refused(ListenAlongError.SCOPE_MISSING);
        if (grant.Premium == false)
            return ListenAlongJoin.Refused(ListenAlongError.PREMIUM_REQUIRED);

        // Joining here leaves any other party first; one pointer per listener.
        if (await directory.GetHostAsync(listenerId, ct) is { } elsewhere && elsewhere != HostId)
            await GrainFactory.GetGrain<IListenAlongGrain>(elsewhere).LeaveAsync(listenerId, ListenAlongEndReason.LEFT, ct);

        var now    = DateTimeOffset.UtcNow;
        var result = await player.PlayAsync(grant.Token, track, PositionNow(track, now), now, ct);

        if (result != SpotifyCommandResult.Ok)
            return ListenAlongJoin.Refused(Map(result));

        var isNew = !listeners.ContainsKey(listenerId);

        listeners[listenerId] = new Listener();
        hostTrack             = track;

        await directory.SetHostAsync(listenerId, HostId, ct);

        if (isNew)
            await GrainFactory.GetGrain<ISpotifyPresenceGrain>(HostId).ListenersChangedAsync(listeners.Count);

        logger.LogInformation("{Listener} is listening along with {Host} ({Count} in the party)", listenerId, HostId, listeners.Count);

        var state = State();

        await AnnounceAsync(state, ct);

        return new ListenAlongJoin(ListenAlongError.NONE, state);
    }

    public async Task LeaveAsync(Guid listenerId, ListenAlongEndReason reason, CancellationToken ct = default)
    {
        if (!listeners.Remove(listenerId))
            return;

        await directory.ClearAsync(listenerId, ct);
        await EndedAsync(listenerId, reason, ct);
        await AfterDepartureAsync(ct);
    }

    public Task<ListenAlongState?> GetStateAsync()
        => Task.FromResult(listeners.Count == 0 ? null : State());

    public async Task OnHostChangedAsync(SpotifyTrack? track)
    {
        if (listeners.Count == 0)
            return;

        if (track is null)
        {
            await EndAllAsync(ListenAlongEndReason.HOST_STOPPED);
            return;
        }

        if (!track.listenAlongOpen && !track.isPlaying && hostTrack is { isPlaying: true })
        {
            // A pause closes the flag too; that is a pause, not the option going off.
        }
        else if (!track.listenAlongOpen && track.isPlaying)
        {
            await EndAllAsync(ListenAlongEndReason.HOST_DISALLOWED);
            return;
        }

        var before = hostTrack;
        var now    = DateTimeOffset.UtcNow;

        hostTrack = track;

        foreach (var (listenerId, listener) in listeners.ToList())
        {
            var grant = await GrainFactory.GetGrain<IUserConnectionsGrain>(listenerId).GetListenerGrantAsync();

            if (grant is null)
            {
                await DropAsync(listenerId, ListenAlongEndReason.PROVIDER_ERROR);
                continue;
            }

            var result = await FollowAsync(grant.Token, before, track, now);

            switch (result)
            {
                case SpotifyCommandResult.Ok:
                    listener.Failures = 0;
                    break;
                case SpotifyCommandResult.NoActiveDevice:
                    await DropAsync(listenerId, ListenAlongEndReason.NO_ACTIVE_DEVICE);
                    break;
                case SpotifyCommandResult.Unauthorized or SpotifyCommandResult.PremiumRequired:
                    await DropAsync(listenerId, ListenAlongEndReason.PROVIDER_ERROR);
                    break;
                default:
                    if (++listener.Failures >= Spotify.ListenerFailuresBeforeDrop)
                        await DropAsync(listenerId, ListenAlongEndReason.PROVIDER_ERROR);
                    break;
            }
        }

        if (listeners.Count == 0)
        {
            await AfterDepartureAsync(CancellationToken.None);
            return;
        }

        await AnnounceAsync(State(), CancellationToken.None);
    }

    /// <summary>The one command that brings a listener from <paramref name="before"/> to <paramref name="after"/>.</summary>
    private Task<SpotifyCommandResult> FollowAsync(ProviderToken token, SpotifyTrack? before, SpotifyTrack after, DateTimeOffset now)
    {
        if (before is null || before.trackId != after.trackId)
            return after.isPlaying
                ? player.PlayAsync(token, after, PositionNow(after, now), now, CancellationToken.None)
                : Task.FromResult(SpotifyCommandResult.Ok);

        if (!after.isPlaying)
            return before.isPlaying ? player.PauseAsync(token, now, CancellationToken.None) : Task.FromResult(SpotifyCommandResult.Ok);

        if (!before.isPlaying)
            return player.ResumeAsync(token, PositionNow(after, now), now, CancellationToken.None);

        // Same track, still playing: the host's poller only reports this when the host seeked.
        return player.SeekAsync(token, PositionNow(after, now), now, CancellationToken.None);
    }

    /// <summary>Where the host is right now, from where they were when the poller looked.</summary>
    public static int PositionNow(SpotifyTrack track, DateTimeOffset now)
    {
        var position = track.isPlaying
            ? track.progressMs + (now - track.observedAt).TotalMilliseconds
            : track.progressMs;

        return (int)Math.Clamp(position, 0, Math.Max(0, track.durationMs));
    }

    private static ListenAlongError Map(SpotifyCommandResult result) => result switch
    {
        SpotifyCommandResult.NoActiveDevice  => ListenAlongError.NO_ACTIVE_DEVICE,
        SpotifyCommandResult.PremiumRequired => ListenAlongError.PREMIUM_REQUIRED,
        SpotifyCommandResult.Unauthorized    => ListenAlongError.SPOTIFY_NOT_LINKED,
        SpotifyCommandResult.RateLimited     => ListenAlongError.RATE_LIMITED,
        _                                    => ListenAlongError.PROVIDER_ERROR
    };

    private ListenAlongState State()
        => new(HostId, new IonArray<Guid>(listeners.Keys.ToList()), hostTrack);

    private async Task DropAsync(Guid listenerId, ListenAlongEndReason reason)
    {
        listeners.Remove(listenerId);
        await directory.ClearAsync(listenerId);
        await EndedAsync(listenerId, reason, CancellationToken.None);
    }

    private async Task EndAllAsync(ListenAlongEndReason reason)
    {
        var gone = listeners.Keys.ToList();

        listeners.Clear();

        foreach (var listenerId in gone)
        {
            await directory.ClearAsync(listenerId);
            await EndedAsync(listenerId, reason, CancellationToken.None);
        }

        await AfterDepartureAsync(CancellationToken.None);
    }

    /// <summary>Everyone who is left hears the new roster; an empty party tells the poller and goes idle.</summary>
    private async Task AfterDepartureAsync(CancellationToken ct)
    {
        if (listeners.Count == 0)
        {
            hostTrack = null;
            await GrainFactory.GetGrain<ISpotifyPresenceGrain>(HostId).ListenersChangedAsync(0);
            await ForUserQuietlyAsync(new ListenAlongChanged(HostId, IonArray<Guid>.Empty, null), HostId, ct);
            DeactivateOnIdle();
            return;
        }

        await AnnounceAsync(State(), ct);
    }

    private async Task AnnounceAsync(ListenAlongState state, CancellationToken ct)
    {
        var @event = new ListenAlongChanged(state.hostUserId, state.listeners, state.track);

        await ForUserQuietlyAsync(@event, HostId, ct);

        foreach (var listenerId in listeners.Keys)
            await ForUserQuietlyAsync(@event, listenerId, ct);
    }

    private Task EndedAsync(Guid listenerId, ListenAlongEndReason reason, CancellationToken ct)
        => ForUserQuietlyAsync(new ListenAlongEnded(HostId, reason), listenerId, ct);

    private async Task ForUserQuietlyAsync<T>(T @event, Guid userId, CancellationToken ct) where T : IArgonEvent
    {
        try
        {
            await appHubServer.ForUser(@event, userId, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "{Event} for {UserId} was not delivered", typeof(T).Name, userId);
        }
    }
}
