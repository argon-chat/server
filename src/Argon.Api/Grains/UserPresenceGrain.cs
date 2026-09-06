namespace Argon.Grains;

using Argon.Core.Features.Logic;
using Argon.Core.Features.Transport;
using Features.Logic;
using ion.runtime;
using Orleans;

/// <inheritdoc cref="IUserPresenceGrain"/>
/// <remarks>
/// <para>Read <see cref="IUserPresenceGrain"/> first: the interface carries the argument for why this
/// exists and why it is a single activation per user. What is worth repeating at the implementation
/// is the one invariant every method here keeps — <b>the aggregate is recomputed inside the same turn
/// that publishes it</b>. A fan-out that read the aggregate in an earlier turn could be the last one
/// to reach the hub while describing a state two turns old, which is the event-order half of the bug
/// this grain replaced; recomputing at the top of the turn makes "last to publish" and "saw the most
/// recent state" the same activation by construction.</para>
///
/// <para>No <c>[StatelessWorker]</c>, no <c>[Reentrant]</c>, and no grain calls out of any turn. The
/// first two would each give back the concurrency this is here to remove; the third would deadlock
/// against <c>SpaceGrain.UserJoined</c>, which awaits this grain from inside its own turn. Everything
/// it publishes goes straight to <c>AppHubServer</c>, which is precisely what the <c>SpaceGrain</c>
/// methods it used to call do with the same event.</para>
/// </remarks>
public class UserPresenceGrain(
    IDbContextFactory<ApplicationDbContext> context,
    IUserPresenceService presenceService,
    ILogger<IUserPresenceGrain> logger,
    IUserSessionDiscoveryService sessionDiscovery,
    IUserSessionNotifier notifier,
    AppHubServer appHubServer) : Grain, IUserPresenceGrain
{
    /// <summary>
    /// How many friend-scoped session lookups are in flight at once.
    /// </summary>
    /// <remarks>
    /// The friends fan-out sits on a path a session grain is awaiting — a status transition — so it
    /// may not be sequential; and it is per user, so it may not be unbounded, or one very sociable
    /// account becomes a thundering herd of its own.
    /// </remarks>
    private const int FriendFanOutConcurrency = 16;

    /// <inheritdoc cref="IUserPresenceGrain.AggregateAndBroadcastStatusAsync(CancellationToken)"/>
    public ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default)
        => AggregateAndBroadcastStatusAsync([], ct);

    /// <inheritdoc cref="IUserPresenceGrain.AggregateAndBroadcastStatusAsync(Guid[],CancellationToken)"/>
    public async ValueTask AggregateAndBroadcastStatusAsync(Guid[] seedSpaces, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        // The fold, here rather than at the caller, and re-run rather than read: this turn is about to
        // publish, so the value it publishes has to be the one the index says right now. A caller that
        // wrote its session's status a moment ago is included by construction, and so is every other
        // session that wrote theirs while this call was queued.
        var aggregatedStatus = await presenceService.RecalculateAggregatedStatusAsync(userId, ct);

        logger.LogDebug("Aggregated status for user {userId}: {status}", userId, aggregatedStatus);

        // A seed with nothing to seed. Offline is announced as silence on a join — the space has never
        // heard of this member, so there is no stale value to correct — and the hysteresis record is
        // deliberately left alone: writing Offline into it here is what raced the fan-out below when
        // this read lived in SpaceGrain.UserJoined.
        if (seedSpaces.Length > 0 && aggregatedStatus is UserStatus.Offline)
            return;

        // Hysteresis: only fan out when the aggregate actually changed since our last broadcast.
        // Connects/heartbeats/transient reconnects that re-compute the same status now cost nothing
        // (no per-space publish, no replay-stream append). All status broadcast paths funnel through
        // here so the last-broadcast record stays consistent.
        if (!await presenceService.MarkBroadcastIfChangedAsync(userId, aggregatedStatus, ct))
        {
            // Except for the seeds, which are not a transition at all: this space has been told
            // nothing about this member and cannot be caught up by a record saying everyone already
            // knows. Nothing else is announced and the record is not touched, so a join costs one
            // event in one space rather than a fan-out to every space the user is in.
            await AnnounceToSpacesAsync(seedSpaces, userId, aggregatedStatus, ct);
            return;
        }

        var servers = await GetMyServersIdsAsync(ct);

        await AnnounceToSpacesAsync(servers, userId, aggregatedStatus, ct);

        await BroadcastStatusToFriendsAsync(userId, aggregatedStatus, ct);
    }

    /// <inheritdoc cref="IUserPresenceGrain.ReassertSessionStatusAsync"/>
    public async ValueTask ReassertSessionStatusAsync(string sessionId, UserStatus status, CancellationToken ct = default)
    {
        var userId = this.GetPrimaryKey();

        var before = await presenceService.GetAggregatedStatusAsync(userId, ct);

        await presenceService.SetSessionStatusAsync(userId, sessionId, status, ct);

        var after = await presenceService.RecalculateAggregatedStatusAsync(userId, ct);

        if (before == after)
            return;

        logger.LogInformation(
            "Session {sid} of user {userId} re-asserted {status} on re-attach; the aggregate moved {before} -> " +
            "{after}, so observers who read the lapse are corrected", sessionId, userId, status, before, after);

        await presenceService.ForgetLastBroadcastAsync(userId, ct);
        await AggregateAndBroadcastStatusAsync(ct);
    }

    /// <inheritdoc cref="IUserPresenceGrain.RecalculateAggregatedStatusAsync"/>
    public async ValueTask<UserStatus> RecalculateAggregatedStatusAsync(CancellationToken ct = default)
        => await presenceService.RecalculateAggregatedStatusAsync(this.GetPrimaryKey(), ct);

    /// <inheritdoc cref="IUserPresenceGrain.BroadcastPresenceAsync"/>
    public async ValueTask BroadcastPresenceAsync(UserActivityPresence presence, string sessionId)
    {
        var userId = this.GetPrimaryKey();

        // Store this session's activity (per-session, so other devices aren't clobbered), then broadcast
        // the representative activity across all the user's sessions. The wire still carries one activity
        // ("last"); the full per-session set lives server-side for when the contract grows.
        await presenceService.BroadcastActivityPresence(presence, userId, sessionId);

        // The fallback is load-bearing: the representative is folded over the live-session index, and a
        // device that announces between its hub handshake and its attach is not in that index yet — so
        // the fold answers nothing for an announcement that has just been stored.
        var representative = await presenceService.GetUsersActivityPresence(userId) ?? presence;

        await Task.WhenAll((await GetMyServersIdsAsync()).Select(spaceId =>
            appHubServer.BroadcastSpace(new OnUserPresenceActivityChanged(spaceId, userId, representative), spaceId)));
    }

    /// <inheritdoc cref="IUserPresenceGrain.RemoveBroadcastPresenceAsync"/>
    public async ValueTask RemoveBroadcastPresenceAsync(string sessionId, bool alwaysBroadcast)
    {
        var userId      = this.GetPrimaryKey();
        var hadActivity = await presenceService.RemoveActivityPresence(userId, sessionId);

        // Skip the fan-out only on the session-ended path when this session had no activity. The
        // explicit user-cleared path — and the session tick's own sweep, which only asks for this once
        // it has established that there is something to retract — must still broadcast even if the key
        // has already lapsed by TTL, or observers keep showing a stale activity indefinitely.
        if (!hadActivity && !alwaysBroadcast)
            return;

        logger.LogInformation("Clearing activity presence for {userId} session {sessionId} (hadActivity={hadActivity})",
            userId, sessionId, hadActivity);

        // Another device may still have an activity — fall back to it; otherwise clear.
        var representative = await presenceService.GetUsersActivityPresence(userId);

        await Task.WhenAll((await GetMyServersIdsAsync()).Select(spaceId =>
            representative is not null
                ? appHubServer.BroadcastSpace(new OnUserPresenceActivityChanged(spaceId, userId, representative), spaceId)
                : appHubServer.BroadcastSpace(new OnUserPresenceActivityRemoved(spaceId, userId), spaceId)));
    }

    /// <summary>Announces the status to each space's group, directly.</summary>
    /// <remarks>
    /// <para><b>Not through <c>ISpaceGrain.SetUserStatus</c>, and it cannot be.</b> That method is one
    /// line — <c>Fire(new UserChangedStatus(...))</c>, i.e. this same <c>AppHubServer</c> publish — so
    /// the room cannot tell the difference; what a grain call would add is a cycle. <c>SpaceGrain</c>
    /// awaits this grain from inside its own turn on every join (<c>UserJoined</c>), and this grain is
    /// a single activation per user: a fan-out that called back into a space currently waiting on us
    /// deadlocks until Orleans' call timeout takes the join down with it. <c>UserGrain</c> could call
    /// space grains only because it was a <c>[StatelessWorker]</c> and a second activation always
    /// answered the join.</para>
    ///
    /// <para>Concurrent across spaces and still ordered within one, which is the only ordering that
    /// was ever the point: a space hears about this user once per turn, and the turn does not end
    /// until every publish in it has, so a later turn's event cannot overtake an earlier turn's in the
    /// same group. Serialising the loop as well would buy nothing and would hold the activation — and
    /// therefore every other session of this user — for the length of the slowest publish times the
    /// number of spaces the account is in. If <c>SetUserStatus</c> ever grows a second responsibility,
    /// this is the line that has to grow with it.</para>
    /// </remarks>
    private Task AnnounceToSpacesAsync(IEnumerable<Guid> spaceIds, Guid userId, UserStatus status, CancellationToken ct)
        => Task.WhenAll(spaceIds.Distinct().Select(spaceId => appHubServer.BroadcastSpace(
            new UserChangedStatus(spaceId, userId, status, new IonArray<string>([""])), spaceId, ct)));

    /// <summary>
    /// UserChangedStatus is only ever fired to the members of a space — so a friend you share no space
    /// with never learned that you came online, and their friends list sat on whatever it last
    /// happened to cache (for someone just added: offline, forever).
    /// </summary>
    /// <remarks>
    /// <para>Only reached when the aggregate actually changed — the hysteresis check above already
    /// swallowed heartbeats and reconnects — so this costs one friend-id query and one notify per
    /// real transition. A friend who is also a space member receives the event twice; deduplicating
    /// would cost a membership join on every transition, and the client keys the update on the user
    /// id alone, so the second one is a no-op.</para>
    ///
    /// <para>The session lookups are bounded rather than one <c>Task.WhenAll</c> over every friend:
    /// each one is a Redis round trip per session of that friend, this sits on the path a session
    /// grain awaits, and an account with a thousand friends should not open a thousand of them at
    /// once. Ordering per recipient is unaffected — the lookups only decide who to address, and the
    /// single notify below is what actually sends, one publish per distinct user.</para>
    /// </remarks>
    private async Task BroadcastStatusToFriendsAsync(Guid userId, UserStatus status, CancellationToken ct)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        var friendIds = await ctx.Friends
           .AsNoTracking()
           .Where(x => x.UserId == userId)
           .Select(x => x.FriendId)
           .ToListAsync(ct);

        if (friendIds.Count == 0)
            return;

        var sessions = new List<UserSessionDescriptor>();

        foreach (var chunk in friendIds.Chunk(FriendFanOutConcurrency))
        {
            var perFriend = await Task.WhenAll(
                chunk.Select(friendId => sessionDiscovery.GetUserSessionsAsync(friendId, ct)));

            sessions.AddRange(perFriend.SelectMany(x => x));
        }

        if (sessions.Count == 0)
            return;

        // There is no space this is about; the client reads userId and status and ignores the rest.
        await notifier.NotifySessionsAsync(
            sessions,
            new UserChangedStatus(Guid.Empty, userId, status, new IonArray<string>([""])),
            ct);
    }

    /// <summary>The spaces this user is a member of.</summary>
    /// <remarks>
    /// The same query <c>UserGrain.GetMyServersIds</c> answers, asked here rather than through that
    /// grain on purpose. <c>UserGrain</c> is a <c>[StatelessWorker]</c> whose activation pool is sized
    /// by the core count, and its presence members forward <em>into</em> this grain — so a call the
    /// other way could find every worker in the pool already blocked on this activation, which on a
    /// two-core box is two of them. One indexed read per fan-out is the cheaper half of that trade.
    /// </remarks>
    private async Task<List<Guid>> GetMyServersIdsAsync(CancellationToken ct = default)
    {
        await using var ctx = await context.CreateDbContextAsync(ct);

        return await ctx.Users
           .AsNoTracking()
           .Include(user => user.ServerMembers)
           .Where(u => u.Id == this.GetPrimaryKey())
           .SelectMany(x => x.ServerMembers)
           .Select(x => x.SpaceId)
           .ToListAsync(ct);
    }
}
