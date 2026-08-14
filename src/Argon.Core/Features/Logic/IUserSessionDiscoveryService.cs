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

/// <summary>
/// One live session of one user.
/// </summary>
/// <remarks>
/// <para><see cref="ClientName"/>, <see cref="ClientRegion"/> and <see cref="LastSeenAt"/> were
/// added for the devices screen, which has to say <em>which</em> session it is offering to end — a
/// list of opaque sids is not something anyone can make a safety decision from. They come from the
/// per-session record <c>IUserPresenceService</c> writes at session start (see
/// <c>TouchSessionMetaAsync</c>), so a session that predates that write, or whose record lapsed,
/// degrades to the same anonymous row the fan-out callers already tolerated rather than dropping
/// out of the list.</para>
///
/// <para><see cref="ClientRegion"/> is separate from <see cref="Region"/> on purpose and they are
/// not interchangeable: <see cref="Region"/>/<see cref="ServerId"/> say where the session is being
/// <em>served</em> from and are what the notifier routes on, while <see cref="ClientRegion"/> is
/// the country the request came from — the only one of the two that answers "was this me?".</para>
/// </remarks>
public sealed record UserSessionDescriptor(
    string SessionId,
    Guid UserId,
    string Region,
    string ServerId,
    string? ClientName = null,
    string? ClientRegion = null,
    DateTime? LastSeenAt = null
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

        foreach (var sid in sessions)
        {
            var meta = await presence.GetSessionMetaAsync(userId, sid, ct);

            list.Add(new UserSessionDescriptor(
                SessionId: sid,
                UserId: userId,
                Region: "ru-3",
                ServerId: "ru-spb-3",
                ClientName: meta?.ClientName,
                ClientRegion: meta?.Region,
                LastSeenAt: meta?.LastSeenAt));
        }

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