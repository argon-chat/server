namespace Argon.Core.Features.Logic;

using Api.Features.Bus;
using Argon.Core.Features.Transport;
using Argon.Features.EphemeralState;
using Argon.Features.Logic;
using Argon.Features.Transport;
using Argon.Services;

public interface IUserSessionDiscoveryService
{
    Task<bool>                                 IsUserOnlineAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSessionDescriptor>> GetUserSessionsAsync(Guid userId, CancellationToken ct = default);
}

public sealed record UserSessionDescriptor(
    string SessionId,
    Guid UserId,
    string Region,
    string ServerId
);

public interface IUserSessionNotifier
{
    Task NotifySessionsAsync<T>(
        IReadOnlyList<UserSessionDescriptor> sessions,
        T payload,
        CancellationToken ct = default) where T : IArgonEvent;
}

public sealed class LocalUserSessionDiscoveryService(
    IUserPresenceService presence,
        IEphemeralStateStore ephemeralStore,
    NatsSessionLocator sessionLocator,
    IConfiguration configuration,
    ILogger<LocalUserSessionDiscoveryService> logger)
    : IUserSessionDiscoveryService
{
    private readonly string _localDcId = configuration.GetValue<string>("Datacenter:Id") ?? "local";

    public Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default)
        => presence.IsUserOnlineAsync(userId, ct);

    public async Task<IReadOnlyList<UserSessionDescriptor>> GetUserSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        // First check local sessions via ephemeral store
        var localRoutes = await ephemeralStore.GetUserSessionRoutesAsync(userId, ct);
        if (localRoutes.Count > 0)
        {
            return localRoutes.Select(r => new UserSessionDescriptor(
                SessionId: r.SessionId,
                UserId: r.UserId,
                Region: r.DatacenterId,
                ServerId: r.EntryPointId
            )).ToList();
        }

        // Cross-DC lookup via NATS request/reply
        var remoteRoutes = await sessionLocator.LocateUserAsync(userId, ct);
        if (remoteRoutes.Count > 0)
        {
            return remoteRoutes.Select(r => new UserSessionDescriptor(
                SessionId: r.SessionId,
                UserId: r.UserId,
                Region: r.DatacenterId,
                ServerId: r.EntryPointId
            )).ToList();
        }

        // Fallback to legacy presence scan
        var sessions = await presence.GetActiveSessionIdsAsync(userId, ct);
        if (sessions.Count == 0) return [];

        return sessions.Select(sid => new UserSessionDescriptor(
            SessionId: sid, UserId: userId, Region: _localDcId, ServerId: Environment.MachineName
        )).ToList();
    }
}

public sealed class UserStreamNotifier(
    IServiceProvider serviceProvider,
    ILogger<UserStreamNotifier> logger) : IUserSessionNotifier
{
    public async Task NotifySessionsAsync<T>(
        IReadOnlyList<UserSessionDescriptor> sessions,
        T payload,
        CancellationToken ct = default) where T : IArgonEvent
    {
        if (sessions.Count == 0)
            return;
        await using var scope = serviceProvider.CreateAsyncScope();

        var hubServer = scope.ServiceProvider.GetRequiredService<AppHubServer>();
        var userId    = sessions[0].UserId;

        try
        {
            await hubServer.ForUser(payload, userId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish event for user {UserId}", userId);
        }
    }
}