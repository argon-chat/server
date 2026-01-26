namespace Argon.Core.Features.Logic;

using Api.Features.Bus;
using Argon.Core.Features.Transport;
using Argon.Features.Logic;
using Argon.Services;
using Genbox.SimpleS3.Core.Abstracts.Region;

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
    ILogger<LocalUserSessionDiscoveryService> logger)
    : IUserSessionDiscoveryService
{
    public Task<bool> IsUserOnlineAsync(Guid userId, CancellationToken ct = default)
        => presence.IsUserOnlineAsync(userId, ct);

    public async Task<IReadOnlyList<UserSessionDescriptor>> GetUserSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        var sessions = await presence.GetActiveSessionIdsAsync(userId, ct);

        if (sessions.Count == 0)
            return [];

        var list = new List<UserSessionDescriptor>(sessions.Count);

        list.AddRange(sessions.Select(sid => new UserSessionDescriptor(SessionId: sid, UserId: userId, Region: "ru-3", ServerId: "ru-spb-3")));

        return list;
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