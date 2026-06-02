namespace Argon.Features.Transport;

using EphemeralState;
using NATS.Client.Core;

/// <summary>
/// Cross-DC session discovery via NATS request/reply.
/// Each DC's EntryPoint responds to locate requests for its local sessions.
/// Replaces the single-DC LocalUserSessionDiscoveryService for multi-DC scenarios.
/// </summary>
public sealed class NatsSessionLocator(
    INatsClient natsClient,
    IEphemeralStateStore ephemeralStore,
    IConfiguration configuration,
    ILogger<NatsSessionLocator> logger) : BackgroundService
{
    private readonly string _localDcId = configuration.GetValue<string>("Datacenter:Id") ?? "local";

    public const string LocateSubject = "sessions.locate";
    public const string AnnounceSubject = "sessions.announce";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("NatsSessionLocator starting, DC={DcId}", _localDcId);

        try
        {
            // Listen for locate requests and respond with local session info
            await foreach (var msg in natsClient.SubscribeAsync<SessionLocateRequest>(
                               $"{LocateSubject}.*", cancellationToken: stoppingToken))
            {
                if (msg.Data is null) continue;

                try
                {
                    var routes = await ephemeralStore.GetUserSessionRoutesAsync(msg.Data.UserId, stoppingToken);
                    var localRoutes = routes.Where(r => r.DatacenterId == _localDcId).ToList();

                    if (localRoutes.Count > 0)
                    {
                        await msg.ReplyAsync(new SessionLocateResponse(
                            Found: true,
                            Sessions: localRoutes
                        ), cancellationToken: stoppingToken);
                    }
                    else
                    {
                        await msg.ReplyAsync(new SessionLocateResponse(
                            Found: false,
                            Sessions: []
                        ), cancellationToken: stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error handling session locate for user {UserId}", msg.Data.UserId);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Locate a user across all DCs. Sends a scatter request to all DCs
    /// and returns the first response that has sessions.
    /// </summary>
    public async Task<List<SessionRoute>> LocateUserAsync(Guid userId, CancellationToken ct = default)
    {
        // First check local ephemeral store
        var localRoutes = await ephemeralStore.GetUserSessionRoutesAsync(userId, ct);
        if (localRoutes.Count > 0)
            return localRoutes;

        // Ask other DCs via NATS request
        try
        {
            var response = await natsClient.RequestAsync<SessionLocateRequest, SessionLocateResponse>(
                $"{LocateSubject}.{userId:N}",
                new SessionLocateRequest(userId),
                cancellationToken: ct);

            if (response.Data is { Found: true })
                return response.Data.Sessions;
        }
        catch (NatsNoRespondersException)
        {
            // No DC has this user's session
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to locate user {UserId} across DCs", userId);
        }

        return [];
    }

    /// <summary>
    /// Announce a session connect/disconnect to all DCs.
    /// </summary>
    public async Task AnnounceSessionAsync(SessionRoute route, bool connected)
    {
        try
        {
            await natsClient.PublishAsync($"{AnnounceSubject}.{_localDcId}",
                new SessionAnnouncement(route, connected));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to announce session for user {UserId}", route.UserId);
        }
    }
}

public sealed record SessionLocateRequest(Guid UserId);

public sealed record SessionLocateResponse(
    bool Found,
    List<SessionRoute> Sessions
);

public sealed record SessionAnnouncement(
    SessionRoute Route,
    bool Connected
);
