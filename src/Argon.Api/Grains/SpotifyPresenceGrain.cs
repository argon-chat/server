namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Spotify;
using Argon.Grains.Interfaces;
using ion.runtime;
// The Ion enum, not System.Diagnostics.ActivitySource from the global usings.
using ActivitySource = ArgonContracts.ActivitySource;

/// <inheritdoc cref="ISpotifyPresenceGrain"/>
public sealed class SpotifyPresenceGrain(
    SpotifyPlayerClient player,
    IUserPresenceService presenceService,
    IOptions<ConnectionsOptions> options,
    ILogger<SpotifyPresenceGrain> logger) : Grain, ISpotifyPresenceGrain
{
    public const string ProviderSlot = "spotify";

    /// <summary>A seek is a jump of more than this between the expected and the read position.</summary>
    private static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(5);

    private Guid                     UserId  => this.GetPrimaryKey();
    private SpotifyConnectionOptions Spotify => options.Value.Spotify;

    /// <summary>How long a grant read from the connections grain is trusted before it is read again.</summary>
    private static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(5);

    private IGrainTimer?    timer;
    private SpotifyTrack?   published;
    private DateTimeOffset? lastPlayingAt;
    private DateTimeOffset? idleSince;
    private string?         lastHint;
    private int             listeners;
    private ActivityGrant?  grant;
    private DateTimeOffset  grantReadAt;

    public Task WakeAsync()
    {
        // A wake follows a link, an option flip or a fresh session: whatever was cached may be stale.
        grant = null;
        Schedule(TimeSpan.Zero);
        return Task.CompletedTask;
    }

    public Task HintAsync(string titleName)
    {
        // The same title again is the desktop's five-minute re-announce, not a new track.
        if (!string.Equals(titleName, lastHint, StringComparison.Ordinal))
        {
            lastHint = titleName;
            Schedule(TimeSpan.Zero);
        }

        return Task.CompletedTask;
    }

    public Task SessionEndedAsync()
    {
        // Not at once: the session grain removes its presence key just before it says so, and the
        // online check must see the index without it.
        if (timer is not null)
            Schedule(TimeSpan.FromSeconds(2));

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await RetractAsync(alwaysBroadcast: published is not null);
        await TellListenersAsync(null);
        Halt();
    }

    public Task<SpotifyTrack?> GetCurrentAsync() => Task.FromResult(published);

    public Task ListenersChangedAsync(int count)
    {
        var had = listeners > 0;

        listeners = Math.Max(0, count);

        // The first listener wants the tighter cadence at once rather than after the current wait.
        if (!had && listeners > 0 && timer is not null)
            Schedule(TimeSpan.Zero);

        return Task.CompletedTask;
    }

    // ── the clock ────────────────────────────────────────────────────────────────────────────

    private void Schedule(TimeSpan due)
    {
        if (timer is null)
            timer = this.RegisterGrainTimer(TickAsync, new GrainTimerCreationOptions(due, Timeout.InfiniteTimeSpan));
        else
            timer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private void Halt()
    {
        timer?.Dispose();
        timer         = null;
        published     = null;
        lastPlayingAt = null;
        idleSince     = null;
        lastHint      = null;
        listeners     = 0;
        grant         = null;

        DeactivateOnIdle();
    }

    /// <summary>
    /// The token and the listen-along option, read from the connections grain once per
    /// <see cref="GrantLifetime"/> rather than on every tick, and sooner when the token is about to
    /// expire — that read is where the refresh happens.
    /// </summary>
    private async Task<ActivityGrant?> GrantAsync(DateTimeOffset now, CancellationToken ct)
    {
        var stale = grant is null
                 || now - grantReadAt >= GrantLifetime
                 || grant.Token.ExpiresWithin(options.Value.TokenRefreshLead, now);

        if (stale)
        {
            grant       = await GrainFactory.GetGrain<IUserConnectionsGrain>(UserId).GetActivityGrantAsync(ConnectionProvider.SPOTIFY, ct);
            grantReadAt = now;
        }

        return grant;
    }

    /// <summary>The party, if there is one, hears what the host did. One-way; nothing to wait for.</summary>
    private async Task TellListenersAsync(SpotifyTrack? track)
    {
        if (listeners > 0)
            await GrainFactory.GetGrain<IListenAlongGrain>(UserId).OnHostChangedAsync(track);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await PollAsync(ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Spotify poll for {UserId} failed; trying again later", UserId);

            if (timer is not null)
                Schedule(Spotify.IdlePoll);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (!await presenceService.IsUserOnlineAsync(UserId, ct))
        {
            await StopAsync();
            return;
        }

        var current = await GrantAsync(now, ct);

        if (current is null)
        {
            await StopAsync();
            return;
        }

        var playback = await player.GetCurrentlyPlayingAsync(current.Token, now, ct);

        switch (playback.State)
        {
            case SpotifyPlaybackState.Unauthorized:
                logger.LogInformation("Spotify refused the token of {UserId}; the connection is sent for re-authentication", UserId);
                // The details refresh hits the same refusal and marks the row NEEDS_REAUTH, with the notification.
                await GrainFactory.GetGrain<IUserConnectionsGrain>(UserId).RefreshDetailsAsync(ConnectionProvider.SPOTIFY, force: true, ct);
                await StopAsync();
                return;

            case SpotifyPlaybackState.Skipped:
            case SpotifyPlaybackState.RateLimited:
                Schedule(SpotifyPollSchedule.AfterThrottle(playback.RetryAfter));
                return;

            case SpotifyPlaybackState.Error:
                Schedule(Spotify.IdlePoll);
                return;

            case SpotifyPlaybackState.Nothing:
                await NothingPlayingAsync(now);
                return;

            case SpotifyPlaybackState.Playing:
                await PlayingAsync(playback.Track!, current.AllowListenAlong, now);
                return;
        }
    }

    private async Task NothingPlayingAsync(DateTimeOffset now)
    {
        if (published is not null)
        {
            await RetractAsync(alwaysBroadcast: true);
            await TellListenersAsync(null);
            published = null;
        }

        idleSince ??= now;

        if (now - idleSince.Value >= Spotify.IdleStopAfter)
        {
            Halt();
            return;
        }

        Schedule(Spotify.IdlePoll);
    }

    private async Task PlayingAsync(SpotifyTrack read, bool allowListenAlong, DateTimeOffset now)
    {
        idleSince = null;

        var track = read with { listenAlongOpen = allowListenAlong && read.isPlaying };

        if (track.isPlaying)
        {
            lastPlayingAt = now;
        }
        else
        {
            // A paused track stays on the card for a while, then goes; one that was never on the
            // card does not appear paused.
            if (published is null || (lastPlayingAt is { } at && now - at >= Spotify.PausedRetractAfter))
            {
                if (published is not null)
                {
                    await RetractAsync(alwaysBroadcast: true);
                    await TellListenersAsync(null);
                    published = null;
                }

                Schedule(Spotify.PausedPoll);
                return;
            }
        }

        var presence = ToPresence(track);
        var changed  = published is null || Changed(published, track);

        if (changed)
            await GrainFactory.GetGrain<IUserPresenceGrain>(UserId).BroadcastProviderPresenceAsync(presence, ProviderSlot);
        else
            await presenceService.SetProviderActivity(UserId, ProviderSlot, presence); // the lease, silently

        published = track;

        if (changed)
            await TellListenersAsync(track);

        var next = SpotifyPollSchedule.AfterPlayback(track, now, Spotify);

        // A party follows the host through the server, so the host is read often enough for a
        // listener to notice a skip within seconds rather than within the playing cap.
        if (listeners > 0 && next > Spotify.ListenAlongPoll)
            next = Spotify.ListenAlongPoll;

        Schedule(next);
    }

    private Task RetractAsync(bool alwaysBroadcast)
        => GrainFactory.GetGrain<IUserPresenceGrain>(UserId).RemoveProviderPresenceAsync(ProviderSlot, alwaysBroadcast).AsTask();

    /// <summary>A new track, a pause or resume, a change of the listen-along flag, or a seek.</summary>
    public static bool Changed(SpotifyTrack before, SpotifyTrack after)
    {
        if (before.trackId != after.trackId || before.isPlaying != after.isPlaying || before.listenAlongOpen != after.listenAlongOpen)
            return true;

        var expected = before.isPlaying
            ? before.progressMs + (after.observedAt - before.observedAt).TotalMilliseconds
            : before.progressMs;

        return Math.Abs(expected - after.progressMs) > SeekTolerance.TotalMilliseconds;
    }

    /// <summary>
    /// The activity as the wire carries it: LISTEN, "Track - Artist" for clients that know nothing
    /// else, the track's start and end as the two timestamps, and the track itself.
    /// </summary>
    public static UserActivityPresence ToPresence(SpotifyTrack track)
    {
        var start    = track.observedAt.AddMilliseconds(-track.progressMs);
        var end      = start.AddMilliseconds(track.durationMs);
        var artists  = string.Join(", ", track.artists.Values);
        var title    = artists.Length > 0 ? $"{track.title} - {artists}" : track.title;

        return new UserActivityPresence(ActivityPresenceKind.LISTEN, (ulong)Math.Max(0, start.ToUnixTimeSeconds()), title,
            ActivitySource.SPOTIFY, (ulong)Math.Max(0, end.ToUnixTimeSeconds()), track, track.url);
    }
}
