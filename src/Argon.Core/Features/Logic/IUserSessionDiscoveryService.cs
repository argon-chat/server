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
/// <para>Everything after <see cref="ServerId"/> exists for the devices screen, which has to say
/// <em>which</em> session it is offering to end — a list of opaque sids is not something anyone can
/// make a safety decision from. It comes from the per-session record <c>IUserPresenceService</c>
/// writes when a session first connects (see <c>TouchSessionMetaAsync</c>), so a session that
/// predates that write, or whose record lapsed, degrades to the same anonymous row the fan-out
/// callers already tolerated rather than dropping out of the list.</para>
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
    DateTime? LastSeenAt = null,
    string? AppId = null,
    string? AppName = null,
    ClientPlatform Platform = ClientPlatform.UNKNOWN,
    string? OsName = null,
    string? AppVersion = null,
    string? DeviceName = null,
    string? Ip = null,
    string? City = null,
    DateTime? StartedAt = null
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
                LastSeenAt: meta?.LastSeenAt,
                AppId: meta?.AppId,
                AppName: meta?.AppName,
                Platform: meta?.Platform ?? ClientPlatform.UNKNOWN,
                OsName: meta?.OsName,
                AppVersion: meta?.AppVersion,
                DeviceName: meta?.DeviceName,
                Ip: meta?.Ip,
                City: meta?.City,
                StartedAt: meta?.StartedAt));
        }

        return list;
    }
}

public sealed class UserStreamNotifier(
    IServiceProvider serviceProvider,
    ILogger<UserStreamNotifier> logger) : IUserSessionNotifier
{
    /// <summary>
    /// How many recipients this notifier is willing to publish to at once.
    /// </summary>
    private const int MaxConcurrentRecipients = 8;

    /// <summary>
    /// Delivers one event to every user the session list names — all of them, not the first one.
    /// </summary>
    /// <remarks>
    /// <para>Defect S13, pinned by
    /// <c>PresenceFriendsTests.A_status_change_reaches_every_online_friend</c>. This method used to
    /// read <c>sessions[0].UserId</c> and send exactly once. That is invisible for the callers this
    /// was written for — <c>CallGrain</c>, <c>FriendsGrain.NotifyAsync</c>,
    /// <c>PushFriendPresenceAsync</c> and the security-details fan-out all hand over one user's own
    /// sessions, where the first element is the only answer there is. But
    /// <c>UserGrain.BroadcastStatusToFriendsAsync</c> flattens the sessions of <em>every</em> friend
    /// into one list, so a status change reached exactly one friend, chosen by whatever order the
    /// friends query happened to return, and every other friend kept a stale status until that user
    /// moved again. <c>AppHubServer.ForUser</c> also writes the replay entry only for the user it
    /// sends to, so the skipped friends could not recover the event through <c>Resume()</c> either.</para>
    ///
    /// <para>The fan-out belongs here rather than at the friends call site: the signature takes a
    /// list of sessions belonging to arbitrary users and every caller reads it as a fan-out. The
    /// send is per user (<c>ForUser</c> already reaches all of that user's connections), hence the
    /// <c>Distinct()</c> — a two-device friend must not be notified twice. The try/catch is per
    /// recipient on purpose: one unreachable user must not silence the rest of the list.</para>
    ///
    /// <para>Concurrent rather than sequential, and that is the second half of S13. Every recipient
    /// costs a replay-stream append, a SignalR backplane publish and a NATS publish, and the whole
    /// chain runs inside <c>UserSessionGrain</c>'s heartbeat turn (heartbeat →
    /// <c>AggregateAndBroadcastStatusAsync</c> → <c>BroadcastStatusToFriendsAsync</c>) on a grain
    /// that is not <c>[Reentrant]</c>. Awaiting N of those one after another held the session grain
    /// for the length of the slowest chain — attaches, detaches and the other window's heartbeats
    /// all queue behind it — and, worse, made the fan-out abandonable half way: the hysteresis
    /// record is written by <c>MarkBroadcastIfChangedAsync</c> <em>before</em> the first send, so a
    /// call cancelled at recipient 250 of 300 leaves the remaining 50 holding the old status with
    /// nothing that will ever re-emit it. Overlapping the sends shortens that window by the
    /// concurrency factor; the per-recipient catch keeps a single failure local.</para>
    ///
    /// <para>Bounded, not unbounded: one user's friends list is the fan-out width, so an unbounded
    /// <c>WhenAll</c> would put an arbitrary number of simultaneous Redis and backplane writes
    /// behind one heartbeat. <see cref="MaxConcurrentRecipients"/> is the compromise — enough to
    /// turn a serial p99 into a batched one, small enough that a popular account cannot flood the
    /// connection multiplexer on its own. The gate is deliberately not given <paramref name="ct"/>:
    /// it is held only for the duration of one publish, and cancellation is meant to surface from
    /// the publish itself, where the existing catch turns it into one log line per recipient instead
    /// of an exception escaping into the grain call that asked for the fan-out.</para>
    /// </remarks>
    public async Task NotifySessionsAsync<T>(
        IReadOnlyList<UserSessionDescriptor> sessions,
        T payload,
        CancellationToken ct = default) where T : IArgonEvent
    {
        if (sessions.Count == 0)
            return;

        var recipients = sessions.Select(x => x.UserId).Distinct().ToList();

        await using var scope = serviceProvider.CreateAsyncScope();

        var hubServer = scope.ServiceProvider.GetRequiredService<AppHubServer>();

        // The overwhelming majority of calls are one user's own sessions (CallGrain,
        // FriendsGrain.NotifyAsync, PushFriendPresenceAsync, the security-details fan-out): no gate,
        // no task array, no allocation for them.
        if (recipients.Count == 1)
        {
            await PublishAsync(hubServer, payload, recipients[0], ct);
            return;
        }

        using var gate = new SemaphoreSlim(MaxConcurrentRecipients, MaxConcurrentRecipients);

        await Task.WhenAll(recipients.Select(async userId =>
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                await PublishAsync(hubServer, payload, userId, ct);
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    /// <summary>
    /// One recipient's publish, which never throws — see the remarks on
    /// <see cref="NotifySessionsAsync{T}"/> for why the failure of one must not end the fan-out.
    /// </summary>
    private async Task PublishAsync<T>(AppHubServer hubServer, T payload, Guid userId, CancellationToken ct)
        where T : IArgonEvent
    {
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
