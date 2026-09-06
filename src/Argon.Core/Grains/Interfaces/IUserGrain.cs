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
    /// The space a channel belongs to, but only when this user is a member of that space — otherwise
    /// <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The channel half of the hub's subscribe gate (defect S11). <c>AppHub.SubscribeToChannel</c>
    /// joined <c>channels/{id}</c> for any id an authenticated caller named, so a stranger who knew a
    /// channel id received everything published to it. The space is the authority on membership and
    /// <c>IChannelGrain</c> exposes no space accessor, so the lookup lives here where the DbContext
    /// does — one indexed read, at most once per channel the client opens.
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