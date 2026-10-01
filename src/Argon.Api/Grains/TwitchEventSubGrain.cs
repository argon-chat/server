namespace Argon.Grains;

using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Twitch;
using Argon.Grains.Interfaces;
using Argon.Services;
using Orleans.Concurrency;

/// <inheritdoc cref="ITwitchEventSubGrain"/>
[StatelessWorker]
public sealed class TwitchEventSubGrain(
    IDbContextFactory<ApplicationDbContext> context,
    TwitchEventSubClient twitch,
    IArgonCacheDatabase cache,
    IOptions<ConnectionsOptions> options,
    ILogger<TwitchEventSubGrain> logger) : Grain, ITwitchEventSubGrain
{
    /// <summary>Twitch retries a notification it got no 2xx for; a message id is remembered this long.</summary>
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromMinutes(10);

    public async Task<EventSubAnswer> HandleAsync(TwitchEventSubMessage message, CancellationToken ct = default)
    {
        var secret = options.Value.Twitch.EventSubSecret;

        if (!TwitchEventSub.VerifySignature(secret, message, DateTimeOffset.UtcNow))
        {
            logger.LogWarning("An EventSub message ({Type}) did not verify; refused", message.MessageType);
            return EventSubAnswer.Forbidden;
        }

        switch (message.MessageType)
        {
            case "webhook_callback_verification":
                return TwitchEventSub.Challenge(message.Body) is { } challenge
                    ? new EventSubAnswer(200, challenge)
                    : new EventSubAnswer(400, null);

            case "revocation":
                logger.LogInformation("Twitch revoked an EventSub subscription ({SubscriptionType})", message.SubscriptionType);
                return EventSubAnswer.Accepted;

            case "notification":
                break;

            default:
                return EventSubAnswer.Accepted;
        }

        // The same message again — a retry after a slow answer — must not flip the stream twice.
        if (await cache.StringSetAndGetPreviousAsync($"conn:twitch:msg:{message.MessageId}", "1", DedupeWindow, ct) is not null)
            return EventSubAnswer.Accepted;

        if (TwitchEventSub.Notification(message.Body) is not var (type, broadcasterId))
            return EventSubAnswer.Accepted;

        var userId = await UserOfAsync(broadcasterId, ct);

        if (userId is null)
        {
            // Nobody has this channel linked any more; the subscriptions are stale, drop them.
            logger.LogInformation("EventSub for Twitch {Broadcaster} with no linked account; removing its subscriptions", broadcasterId);
            await RemoveQuietlyAsync(broadcasterId, ct);
            return EventSubAnswer.Accepted;
        }

        var presence = GrainFactory.GetGrain<ITwitchPresenceGrain>(userId.Value);

        switch (type)
        {
            case "stream.online":
                await presence.StreamOnlineAsync();
                break;
            case "stream.offline":
                await presence.StreamOfflineAsync();
                break;
        }

        return EventSubAnswer.Accepted;
    }

    public async Task EnsureSubscriptionsAsync(string broadcasterId, CancellationToken ct = default)
    {
        if (!options.Value.Twitch.StreamingStatusEnabled)
            return;

        await twitch.EnsureSubscriptionsAsync(broadcasterId, ct);
    }

    public async Task RemoveSubscriptionsAsync(string broadcasterId, CancellationToken ct = default)
    {
        if (!options.Value.Twitch.IsConfigured)
            return;

        await twitch.RemoveSubscriptionsAsync(broadcasterId, ct);
    }

    private async Task<Guid?> UserOfAsync(string broadcasterId, CancellationToken ct)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var row = await ctx.UserConnections.AsNoTracking()
           .Where(x => x.Provider == ConnectionProvider.TWITCH && x.ExternalId == broadcasterId
                    && x.Status == ConnectionStatus.ACTIVE && x.DisplayAsStatus)
           .Select(x => new { x.UserId })
           .FirstOrDefaultAsync(ct);

        return row?.UserId;
    }

    private async Task RemoveQuietlyAsync(string broadcasterId, CancellationToken ct)
    {
        try
        {
            await RemoveSubscriptionsAsync(broadcasterId, ct);
        }
        catch (Exception e) when (e is ProviderCallException or HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(e, "Stale Twitch subscriptions for {Broadcaster} could not be removed now", broadcasterId);
        }
    }
}
