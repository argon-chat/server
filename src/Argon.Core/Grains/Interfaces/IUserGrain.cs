namespace Argon.Grains.Interfaces;


[Alias("Argon.Grains.Interfaces.IUserGrain")]
public interface IUserGrain : IGrainWithGuidKey
{
    [Alias(nameof(UpdateProfileAsync))]
    Task<Either<UpdateProfileResult, UpdateMeError>> UpdateProfileAsync(UserEditInput input, CancellationToken ct = default);

    [Alias(nameof(GetMe))]
    Task<UserEntity> GetMe();

    [Alias(nameof(GetAsArgonUser))]
    Task<ArgonUser> GetAsArgonUser();

    [Alias(nameof(GetMyProfile))]
    Task<ArgonUserProfile> GetMyProfile();

    [Alias(nameof(GetMyServers))]
    Task<List<ArgonSpaceBase>> GetMyServers();

    [Alias(nameof(GetMyServersIds))]
    Task<List<Guid>> GetMyServersIds(CancellationToken ct = default);

    [Alias(nameof(BroadcastPresenceAsync))]
    ValueTask BroadcastPresenceAsync(UserActivityPresence presence, string sessionId);

    // alwaysBroadcast=true: the user explicitly cleared their activity (client RemoveBroadcastPresence)
    // — fan out the removal/representative even if this session's activity key already lapsed (TTL), so
    // already-connected observers don't keep a stale activity forever. alwaysBroadcast=false: a session
    // just ended — only fan out if it actually had an activity, to avoid spamming removals on every
    // disconnect of activity-less users.
    [Alias(nameof(RemoveBroadcastPresenceAsync))]
    ValueTask RemoveBroadcastPresenceAsync(string sessionId, bool alwaysBroadcast);

    [Alias(nameof(UpdateUserDeviceHistory))]
    ValueTask UpdateUserDeviceHistory();

    /// <summary>
    /// Replaces this user's stored password digest with one made the way the node hashes today.
    /// </summary>
    /// <remarks>
    /// Called after a successful sign-in, which is the only moment the plaintext exists to hash
    /// again — so this is how an account leaves an old scheme behind, one login at a time, and there
    /// is no batch job that could do it instead. The caller has already verified the password; this
    /// takes the digest it produced rather than the password itself, so the plaintext never crosses
    /// a grain boundary.
    /// </remarks>
    [Alias(nameof(UpgradePasswordDigest))]
    ValueTask UpgradePasswordDigest(string digest);

    [Alias(nameof(BeginUploadUserFile))]
    ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadUserFile(UserFileKind kind, CancellationToken ct = default);

    [Alias(nameof(CompleteUploadUserFile))]
    ValueTask CompleteUploadUserFile(Guid blobId, UserFileKind kind, CancellationToken ct = default);

    [Alias(nameof(GetLimitationForUser))]
    ValueTask<LockedAuthStatus> GetLimitationForUser();

    /// <summary>
    /// Aggregates status from all active sessions and broadcasts the result to all servers.
    /// Called by UserSessionGrain when session status changes.
    /// </summary>
    [Alias(nameof(AggregateAndBroadcastStatusAsync))]
    ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// The same, plus spaces that must be told the aggregate whether or not it changed.
    /// </summary>
    /// <remarks>
    /// <para>For a join. A space that has just gained a member has heard nothing about them ever, so
    /// "nothing changed" is the wrong answer for it and the right answer for everybody else: the
    /// per-user hysteresis record (<c>status:user:{u}:lastbroadcast</c>) exists to stop a re-asserted
    /// status being fanned out to spaces that already have it, and a seed is precisely the case it
    /// gets wrong.</para>
    ///
    /// <para>Why here rather than in <c>SpaceGrain.UserJoined</c>, which used to read the aggregate
    /// and announce it itself: the same user's first heartbeat runs the ordinary fan-out through this
    /// grain at the same instant, and the two racing over one Redis record produced a hysteresis entry
    /// that disagreed with the aggregate — after which the next real transition was suppressed for
    /// <em>every</em> space, not only the new one. One owner for the read, the record and the
    /// announcement is what removes the interleaving rather than narrowing it.</para>
    ///
    /// <para>An Offline aggregate announces nothing at all, to the seeds included: a member who
    /// accepted an invite without ever opening the app has no status to report, and the space has no
    /// stale value to correct — see <c>SpaceGrain.UserJoined</c>.</para>
    ///
    /// <para>A seed is announced by publishing to the space's group directly rather than by calling
    /// that space's grain, which is what lets a <c>SpaceGrain</c> await this from inside its own turn
    /// without deadlocking on itself. Passing a space that is <em>not</em> the caller is still correct;
    /// it simply skips a hop.</para>
    /// </remarks>
    [Alias("AggregateAndBroadcastStatusAsyncSeeded")]
    ValueTask AggregateAndBroadcastStatusAsync(Guid[] seedSpaces, CancellationToken ct = default);

    /// <summary>
    /// Sends this user's sessions the current status of each of their friends.
    /// Called by UserSessionGrain when a session starts: presence events only travel forward in
    /// time, so a fresh session knows nothing about friends who came online before it connected.
    /// </summary>
    [Alias(nameof(PushFriendPresenceAsync))]
    ValueTask PushFriendPresenceAsync(CancellationToken ct = default);

    /// <summary>
    /// Takes this user out of every voice channel they are still listed in.
    /// </summary>
    /// <remarks>
    /// <para>Called by <c>UserSessionGrain.FinalizeOfflineAsync</c> when the user's LAST session ends
    /// — sign-out, revocation or the disconnect grace expiring. Voice membership lives in
    /// <c>ChannelGrain.Users</c> and was emptied only by an explicit <c>DisconnectFromVoiceChannel</c>,
    /// a moderator kick or the LiveKit participant-left webhook, so a client that quit or crashed left
    /// an occupant in the room for everyone else, and <c>ChannelGrain</c> pins its own activation for
    /// a day while any occupant remains (defect S15, pinned by
    /// <c>PresenceVoiceAndCountsTests.A_last_session_going_offline_takes_the_user_out_of_voice</c>).</para>
    ///
    /// <para>O(1) per space, not a scan: <c>SpaceGrain.GetUserVoiceSlotAsync</c> is the reverse index
    /// that <c>ChannelGrain.Join</c> already maintains. Going through <c>ChannelGrain.Leave</c> rather
    /// than editing state directly is what makes observers hear <c>LeavedFromChannelUser</c> and what
    /// settles the voice XP, exactly as a deliberate hang-up does.</para>
    /// </remarks>
    [Alias(nameof(LeaveAllVoiceAsync))]
    ValueTask LeaveAllVoiceAsync(CancellationToken ct = default);

    /// <summary>
    /// The space a channel belongs to, but only when this user may actually see that channel —
    /// otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// <para>The channel half of the hub's subscribe gate (defect S11). <c>AppHub.SubscribeToChannel</c>
    /// joined <c>channels/{id}</c> for any id an authenticated caller named, so a stranger who knew a
    /// channel id received everything published to it. The space is the authority and
    /// <c>IChannelGrain</c> exposes no space accessor, so the lookup lives here where the DbContext
    /// does — one indexed read, at most once per channel the client opens.</para>
    ///
    /// <para><b>Membership is necessary and not sufficient</b>, despite the name this method has kept:
    /// it answers with the space id only when the caller is a member <em>and</em> holds
    /// <c>ArgonEntitlement.ViewChannel</c> on that channel, resolved through the same
    /// <c>EntitlementEvaluator.ApplyPermissionOverwrites</c> path
    /// <c>SpaceReadGrain.VisibleChannelsAsync</c> filters the channel list with. Space membership
    /// alone let any member of a space subscribe to its moderators-only channel and read every
    /// message posted in it live, while the same channel was correctly missing from their roster. The
    /// two answers now come from the same rule, deliberately, so they cannot drift apart.</para>
    ///
    /// <para>Null covers all three refusals — no such channel, not a member, not permitted — because
    /// telling them apart tells a caller which channels exist and who can see them.</para>
    /// </remarks>
    [Alias(nameof(ResolveChannelSpaceIfMemberAsync))]
    Task<Guid?> ResolveChannelSpaceIfMemberAsync(Guid channelId, CancellationToken ct = default);

    [Alias(nameof(ResetPremiumProfileAsync))]
    ValueTask ResetPremiumProfileAsync(CancellationToken ct = default);

    [Alias(nameof(GetLegalState))]
    Task<LegalState> GetLegalState();

    [Alias(nameof(AcceptLegal))]
    Task<LegalState> AcceptLegal(string tosVersion, string privacyVersion);
}


public record UploadTicket(Guid BlobId, string Url, Dictionary<string, string> Fields, int TtlSeconds);

public record UpdateProfileResult(ArgonUser User, ArgonUserProfile Profile);

public enum UserFileKind
{
    Avatar
}