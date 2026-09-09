namespace Argon.Grains.Interfaces;

using ArchetypeModel;
using Users;

[Alias($"Argon.Grains.Interfaces.{nameof(ISpaceGrain)}")]
public interface ISpaceGrain : IGrainWithGuidKey
{
    [Alias(nameof(CreateSpace))]
    Task<Either<ArgonSpaceBase, ServerCreationError>> CreateSpace(ServerInput input);

    [Alias(nameof(GetSpace))]
    Task<SpaceEntity> GetSpace();

    [Alias(nameof(UpdateSpace))]
    Task<SpaceEntity> UpdateSpace(ServerInput input);

    [Alias(nameof(DeleteSpace))]
    Task DeleteSpace();

    [Alias(nameof(AnnounceDeletionScheduled))]
    Task AnnounceDeletionScheduled(SpaceDeletionState deletionState);

    [Alias(nameof(AnnounceDeletionCancelled))]
    Task AnnounceDeletionCancelled();

    [Alias(nameof(CreateChannelGroup))]
    Task<ChannelGroupEntity> CreateChannelGroup(string name, string? description = null);

    [Alias(nameof(MoveChannelGroup))]
    Task MoveChannelGroup(Guid groupId, Guid? afterGroupId, Guid? beforeGroupId);

    [Alias(nameof(DeleteChannelGroup))]
    Task DeleteChannelGroup(Guid groupId, bool deleteChannels = false);

    [Alias(nameof(UpdateChannelGroup))]
    Task<ChannelGroupEntity> UpdateChannelGroup(Guid groupId, string? name = null, string? description = null, bool? isCollapsed = null, CancellationToken ct = default);

    [Alias(nameof(CreateChannel))]
    Task<ChannelEntity> CreateChannel(ChannelInput input, Guid? groupId = null);

    [Alias(nameof(MoveChannel))]
    Task MoveChannel(Guid channelId, Guid? targetGroupId, Guid? afterChannelId, Guid? beforeChannelId);

    [Alias(nameof(DeleteChannel))]
    Task DeleteChannel(Guid channelId);

    /// <summary>
    /// A copy of <paramref name="channelId"/> placed right after it in the same group: same type,
    /// name, topic, cooldown, bitrate and permission overwrites, none of the messages.
    /// </summary>
    [Alias(nameof(DuplicateChannel))]
    Task<Either<ChannelEntity, DuplicateChannelError>> DuplicateChannel(Guid channelId, CancellationToken ct = default);

    [Alias(nameof(SetUserStatus))]
    Task SetUserStatus(Guid userId, UserStatus status);

    [Alias(nameof(SetUserPresence))]
    Task SetUserPresence(Guid userId, UserActivityPresence presence);

    [Alias(nameof(RemoveUserPresence))]
    Task RemoveUserPresence(Guid userId);

    [Alias(nameof(GetMember))]
    Task<RealtimeServerMember> GetMember(Guid userId);

    [Alias(nameof(DoJoinUserAsync))]
    Task<bool> DoJoinUserAsync(ulong? joinedViaInviteId = null);

    /// <summary>
    /// Takes <paramref name="userId"/> off this space's roster: soft-deletes the membership,
    /// invalidates the cached roster and announces the departure.
    /// </summary>
    /// <remarks>
    /// <para>Defect ACC-03, pinned by
    /// <c>AccountDeletionTests.The_spaces_are_told_the_member_left_and_stop_listing_them</c>. Account
    /// erasure removed memberships with a bare <c>ExecuteUpdateAsync</c> from its own
    /// <c>DbContext</c> — no invalidation and no event — while every roster mutation inside
    /// <c>SpaceGrain</c> calls <c>Invalidate()</c>, so <c>SpaceReadGrain</c> went on serving the
    /// erased member out of a snapshot whose distributed expiry is two minutes and no client was ever
    /// told to drop them. This is where that mutation belongs: one type owns the roster, its cache and
    /// its announcements, and a caller outside the space cannot get two of the three right and miss
    /// the last one.</para>
    ///
    /// <para>Idempotent by design — a membership already soft-deleted is not announced again — so a
    /// resumed or retried erasure cannot fan the same departure out twice.</para>
    /// </remarks>
    [Alias(nameof(RemoveMemberAsync))]
    Task RemoveMemberAsync(Guid userId);

    /// <summary>
    /// Says that <paramref name="userId"/> has left, for a membership whose row is already gone:
    /// invalidates the cached roster and fires <c>LeavedFromServerUser</c>, and touches no row.
    /// </summary>
    /// <remarks>
    /// <para>The recovery half of <see cref="RemoveMemberAsync"/>, and it exists because that method's
    /// idempotence has a cost. It commits the soft-delete, then invalidates, then announces; if the
    /// distributed cache or the message bus is briefly unavailable the row is already gone when the
    /// throw happens, and every later attempt sees <c>removed == 0</c> and returns silently by design.
    /// The departure is then never announced: no bot gets its <c>MemberLeave</c>, and every client
    /// holding that space keeps the erased member until it happens to bootstrap the space again. That
    /// is defect ACC-03 surviving on the error path (finding R22), and <c>AccountDeletionGrain</c> is
    /// the caller that meets it — an erasure walks every space the account was in, so a ten-second
    /// NATS outage across forty spaces strands whichever ones threw after committing.</para>
    ///
    /// <para>So the erasure records the space ids it could not announce
    /// (<c>AccountDeletionGrainState.PendingDepartureAnnouncements</c>) and replays them through here
    /// on its next attempt. Separate from <see cref="RemoveMemberAsync"/> rather than a flag on it,
    /// because the two have opposite safety rules: that one must never announce a departure twice, and
    /// this one must announce one whose row is already gone. Only a caller that knows it owes an
    /// announcement may ask for it, and nothing else in the product calls this.</para>
    ///
    /// <para>Announcing a departure for somebody who never left is harmless in the direction that
    /// matters — a client that does not hold the member drops nothing, and the roster is re-read from
    /// the database — but it is still a roster event observers cannot reconcile, which is why this is
    /// not the general leave path.</para>
    /// </remarks>
    [Alias(nameof(AnnounceMemberLeftAsync))]
    Task AnnounceMemberLeftAsync(Guid userId);

    [Alias(nameof(SetBoostStripHidden))]
    Task SetBoostStripHidden(bool hidden);

    /// <summary>
    /// Flips the platform-controlled space flags on behalf of an operator. Null leaves a flag
    /// alone, so the two admin buttons can share one entry point without either clobbering the
    /// other.
    /// </summary>
    /// <remarks>
    /// Deliberately not on the ion service: unlike <see cref="SetBoostStripHidden"/> there is no
    /// caller entitlement that grants this. The only way in is the admin console, which has already
    /// authorised and audited the operator, so the grain does not re-check a caller it does not have.
    /// </remarks>
    [Alias(nameof(SetPlatformSpaceFlags))]
    Task SetPlatformSpaceFlags(bool? isCommunity, bool? isOfficial, CancellationToken ct = default);

    [Alias(nameof(GetSpaceStats))]
    Task<SpaceStats> GetSpaceStats();

    [Alias(nameof(GetInvitePreview))]
    Task<InvitePreview> GetInvitePreview();

    [Alias(nameof(DoUserUpdatedAsync))]
    Task DoUserUpdatedAsync(ArgonUser user);

    [Alias(nameof(DoUserProfileUpdatedAsync))]
    Task DoUserProfileUpdatedAsync(Guid userId, ArgonUserProfile profile);

    [Alias(nameof(PrefetchProfile))]
    Task<ArgonUserProfile> PrefetchProfile(Guid userId);

    [Alias(nameof(PrefetchUser))]
    Task<ArgonUser> PrefetchUser(Guid userId, CancellationToken ct = default);

    [Alias(nameof(BeginUploadSpaceFile))]
    ValueTask<Either<UploadTicket, UploadFileError>> BeginUploadSpaceFile(SpaceFileKind kind, CancellationToken ct = default);

    [Alias(nameof(CompleteUploadSpaceFile))]
    ValueTask CompleteUploadSpaceFile(Guid blobId, SpaceFileKind kind, CancellationToken ct = default);

    [Alias(nameof(GetInstalledBots))]
    Task<List<InstalledBotRecord>> GetInstalledBots();

    [Alias(nameof(InstallBot))]
    Task<InstallBotGrainResult> InstallBot(Guid botAppId);

    [Alias(nameof(UninstallBot))]
    Task<UninstallBotGrainResult> UninstallBot(Guid botAppId);

    [Alias(nameof(ApproveBotEntitlements))]
    Task<ApproveBotEntitlementsGrainResult> ApproveBotEntitlements(Guid botAppId);

    [Alias(nameof(OnUserJoinedVoiceAsync))]
    Task OnUserJoinedVoiceAsync(Guid userId, Guid channelId, DateTimeOffset joinedAt);

    [Alias(nameof(OnUserLeftVoiceAsync))]
    Task OnUserLeftVoiceAsync(Guid userId);

    [Alias(nameof(GetUserVoiceSlotAsync))]
    Task<VoiceSlot?> GetUserVoiceSlotAsync(Guid userId);
}

[DataContract, Serializable, GenerateSerializer]
public sealed partial record VoiceSlot(
    [property: DataMember(Order = 0), Id(0)] Guid ChannelId,
    [property: DataMember(Order = 1), Id(1)] DateTimeOffset JoinedAt);

public enum ServerCreationError
{
    BAD_MODEL
}


public sealed record ServerInput(
    string? Name,
    string? Description,
    string? AvatarUrl);

public enum SpaceFileKind
{
    Avatar,
    ProfileHeader,
    InviteImage
}

[GenerateSerializer, Immutable]
public sealed record InstalledBotRecord(
    [property: Id(0)] Guid              AppId,
    [property: Id(1)] string            Name,
    [property: Id(2)] string            Username,
    [property: Id(3)] string?           AvatarFileId,
    [property: Id(4)] bool              IsVerified,
    [property: Id(5)] Guid              BotUserId,
    [property: Id(6)] ArgonEntitlement  RequiredEntitlements,
    [property: Id(7)] ArgonEntitlement  GrantedEntitlements,
    [property: Id(8)] bool              PendingApproval);

[GenerateSerializer, Immutable]
public sealed record InstallBotGrainResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] InstallBotError? Error = null,
    [property: Id(2)] InstalledBotRecord? Bot = null);

[GenerateSerializer, Immutable]
public sealed record UninstallBotGrainResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] UninstallBotError? Error = null);

[GenerateSerializer, Immutable]
public sealed record ApproveBotEntitlementsGrainResult(
    [property: Id(0)] bool Success,
    [property: Id(1)] ApproveBotEntitlementsError? Error = null);