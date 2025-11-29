namespace Argon.Core.Features.Logic;

using Api.Features.Bus;
using Argon.Features.Logic;
using Argon.Services;
using Genbox.SimpleS3.Core.Abstracts.Region;

public interface IUserSessionDiscoveryService
{
    Task<bool>                                 IsUserOnlineAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSessionDescriptor>> GetUserSessionsAsync(Guid userId, CancellationToken ct = default);
}

public sealed record UserSessionDescriptor(
    Guid SessionId,
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
    IStreamManagement streams,
    ILogger<UserStreamNotifier> logger) : IUserSessionNotifier
{
    public async Task NotifySessionsAsync<T>(
        IReadOnlyList<UserSessionDescriptor> sessions,
        T payload,
        CancellationToken ct = default) where T : IArgonEvent
    {
        if (sessions.Count == 0)
            return;

        var userId = sessions[0].UserId;

        var stream = await streams.CreateServerStreamFor(userId);

        try
        {
            await stream.Fire(payload, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish event for user {UserId}", userId);
        }
        finally
        {
            await stream.DisposeAsync();
        }
    }
}