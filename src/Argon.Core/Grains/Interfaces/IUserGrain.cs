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

    /// <summary>
    /// This user's public identity as anyone else sees it, resolved even when the account has been
    /// deleted. <see langword="null"/> only when no row with that id has ever existed.
    /// </summary>
    /// <remarks>
    /// <para>Defect ACC-06, pinned by
    /// <c>AccountPeripheralTests.A_deleted_peer_still_resolves_to_the_tombstone_identity</c> and by
    /// <c>AccountDeletionTests.The_social_graph_forgets_the_deleted_account</c>. <see cref="GetMe"/>
    /// reads under the global <c>!IsDeleted</c> filter every <c>ArgonEntity</c> carries, and it reads
    /// with <c>FirstAsync</c> — so for an account an executed deletion anonymised in place it does not
    /// answer "gone", it throws, and <c>UserInteraction.LookupUser</c> handed the caller an
    /// <c>UPSTREAM_ERROR</c>. That call is the client's only id-only route to a person its local cache
    /// does not hold, which is exactly the situation of a DM peer on a fresh install, so the chat
    /// window of a surviving conversation had nothing to put at the top of it.</para>
    ///
    /// <para>Kept as a separate method rather than widening <see cref="GetMe"/> or
    /// <see cref="GetAsArgonUser"/>, because both of those are read for their filtering:
    /// <see cref="GetMe"/> is a self-read and would hand a deleted account its own row back, and
    /// <see cref="GetAsArgonUser"/>'s throw is what <c>XsollaWebHookController</c>, Ultima gifting and
    /// <c>BotUserCache</c> use as an existence check — a gift must not land on an erased account. The
    /// name says which of the two this is, so a future caller has to choose on purpose.</para>
    ///
    /// <para>Deletion already writes the tombstone this returns — display name "Deleted Account", a
    /// randomised <c>deleted_…</c> username, no avatar — and <c>UserEntity.GetFlags</c> raises
    /// <c>UserFlag.DELETED</c> from the same row, which is how a client tells a tombstone from a
    /// person. Nothing identifying survives on the DTO: <c>ArgonUser</c> is id, username, display
    /// name, avatar and flags. It is the shape <c>SpaceGrain.PrefetchUser</c> has always used, so the
    /// two ways of looking at the same deleted account now agree.</para>
    /// </remarks>
    [Alias(nameof(GetIdentityIncludingDeleted))]
    Task<ArgonUser?> GetIdentityIncludingDeleted();

    [Alias(nameof(GetMyProfile))]
    Task<ArgonUserProfile> GetMyProfile();

    [Alias(nameof(GetMyServers))]
    Task<List<ArgonSpaceBase>> GetMyServers();

    [Alias(nameof(GetMyServersIds))]
    Task<List<Guid>> GetMyServersIds(CancellationToken ct = default);

    /// <inheritdoc cref="IUserPresenceGrain.BroadcastPresenceAsync"/>
    /// <remarks>
    /// A forwarder to <see cref="IUserPresenceGrain"/>, which owns every presence fan-out for a user
    /// and is the only thing that orders them. Kept on this interface because the Ion surface
    /// (<c>UserInteractionImpl</c>) addresses the user grain and nothing about that address is wrong;
    /// the work simply is not done here any more. Do not reimplement it — see
    /// <see cref="IUserPresenceGrain"/> for what two activations doing this at once cost.
    /// </remarks>
    [Alias(nameof(BroadcastPresenceAsync))]
    ValueTask BroadcastPresenceAsync(UserActivityPresence presence, string sessionId);

    /// <inheritdoc cref="IUserPresenceGrain.RemoveBroadcastPresenceAsync"/>
    /// <remarks>A forwarder to <see cref="IUserPresenceGrain"/>, as <see cref="BroadcastPresenceAsync"/> is.</remarks>
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

    /// <inheritdoc cref="IUserPresenceGrain.AggregateAndBroadcastStatusAsync(CancellationToken)"/>
    /// <remarks>
    /// A forwarder to <see cref="IUserPresenceGrain"/>. This grain is a <c>[StatelessWorker]</c>, so
    /// several activations of one user run at once — which is exactly what a presence fold and a
    /// presence fan-out must not do, and is what left a switching user cached Offline and a room
    /// holding a stale Offline event. Call the presence grain directly from new code; this member
    /// exists so callers that already hold an <see cref="IUserGrain"/> reference do not have to
    /// change, and it must never grow a body of its own.
    /// </remarks>
    [Alias(nameof(AggregateAndBroadcastStatusAsync))]
    ValueTask AggregateAndBroadcastStatusAsync(CancellationToken ct = default);

    /// <inheritdoc cref="IUserPresenceGrain.AggregateAndBroadcastStatusAsync(Guid[],CancellationToken)"/>
    /// <remarks>A forwarder to <see cref="IUserPresenceGrain"/>, as the overload above is.</remarks>
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