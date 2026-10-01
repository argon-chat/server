namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Twitch;
using Argon.Grains.Interfaces;
// The Ion enum, not System.Diagnostics.ActivitySource from the global usings.
using ActivitySource = ArgonContracts.ActivitySource;

/// <inheritdoc cref="ITwitchPresenceGrain"/>
public sealed class TwitchPresenceGrain(
    TwitchEventSubClient twitch,
    IUserPresenceService presenceService,
    IOptions<ConnectionsOptions> options,
    ILogger<TwitchPresenceGrain> logger) : Grain, ITwitchPresenceGrain
{
    public const string ProviderSlot = "twitch";

    /// <summary>The slot's TTL is ten minutes; the lease is pushed back well inside it.</summary>
    private static readonly TimeSpan LeasePeriod = TimeSpan.FromMinutes(4);

    private Guid UserId => this.GetPrimaryKey();

    private IGrainTimer?   timer;
    private TwitchStream?  published;
    private DateTimeOffset lastRead;

    public async Task StreamOnlineAsync() => await ReadAndPublishAsync(force: true);

    public async Task StreamOfflineAsync() => await StopAsync();

    public async Task WakeAsync()
    {
        // A session attached: nothing to do unless the stream is live, and a live one is already
        // on a clock. Reading once here is what makes a stream that started while nobody was
        // signed in to Argon show up when they sign in.
        if (timer is null)
            await ReadAndPublishAsync(force: true);
    }

    public async Task StopAsync()
    {
        var had = published is not null;

        published = null;
        timer?.Dispose();
        timer = null;

        await GrainFactory.GetGrain<IUserPresenceGrain>(UserId).RemoveProviderPresenceAsync(ProviderSlot, alwaysBroadcast: had);

        DeactivateOnIdle();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await ReadAndPublishAsync(force: false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Twitch stream read for {UserId} failed; trying again on the next tick", UserId);
        }
    }

    /// <summary>
    /// Reads the stream when due and publishes what it finds; between reads only the lease moves.
    /// </summary>
    private async Task ReadAndPublishAsync(bool force)
    {
        var now = DateTimeOffset.UtcNow;

        var grant = await GrainFactory.GetGrain<IUserConnectionsGrain>(UserId).GetActivityGrantAsync(ConnectionProvider.TWITCH);

        if (grant is null || !options.Value.Twitch.StreamingStatusEnabled)
        {
            await StopAsync();
            return;
        }

        var due = force || published is null || now - lastRead >= options.Value.Twitch.StreamRefreshEvery;

        if (due)
        {
            var broadcasterId = await BroadcasterIdAsync();

            if (broadcasterId is null)
            {
                await StopAsync();
                return;
            }

            var stream = await twitch.GetStreamAsync(broadcasterId, CancellationToken.None);

            lastRead = now;

            if (stream is null)
            {
                await StopAsync();
                return;
            }

            var changed = published is null || published.Title != stream.Title || published.Game != stream.Game;

            published = stream;

            if (changed)
                await GrainFactory.GetGrain<IUserPresenceGrain>(UserId).BroadcastProviderPresenceAsync(ToPresence(stream), ProviderSlot);
            else
                await presenceService.SetProviderActivity(UserId, ProviderSlot, ToPresence(stream));
        }
        else if (published is not null)
        {
            await presenceService.SetProviderActivity(UserId, ProviderSlot, ToPresence(published));
        }

        timer ??= this.RegisterGrainTimer(TickAsync, new GrainTimerCreationOptions(LeasePeriod, LeasePeriod));
    }

    private async Task<string?> BroadcasterIdAsync()
    {
        var mine = await GrainFactory.GetGrain<IUserConnectionsGrain>(UserId).GetMineAsync();

        return mine.FirstOrDefault(c => c.provider == ConnectionProvider.TWITCH)?.externalId;
    }

    /// <summary>STREAMING, the title as the line, the game appended when there is one, the channel as the link.</summary>
    public static UserActivityPresence ToPresence(TwitchStream stream)
    {
        var line = stream.Game.Length > 0 && stream.Title.Length > 0 ? $"{stream.Title} · {stream.Game}"
                 : stream.Title.Length > 0 ? stream.Title
                 : stream.Game.Length > 0 ? stream.Game
                 : stream.DisplayName;

        return new UserActivityPresence(ActivityPresenceKind.STREAMING, (ulong)Math.Max(0, stream.StartedAt.ToUnixTimeSeconds()), line,
            ActivitySource.TWITCH, null, null, stream.Url);
    }
}
