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

    [Alias(nameof(GetChannelGroups))]
    Task<List<ChannelGroupEntity>> GetChannelGroups();

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

    [Alias(nameof(SetUserStatus))]
    Task SetUserStatus(Guid userId, UserStatus status);

    [Alias(nameof(SetUserPresence))]
    Task SetUserPresence(Guid userId, UserActivityPresence presence);

    [Alias(nameof(RemoveUserPresence))]
    Task RemoveUserPresence(Guid userId);

    [Alias(nameof(GetMembers))]
    Task<List<RealtimeServerMember>> GetMembers();

    [Alias(nameof(GetMember))]
    Task<RealtimeServerMember> GetMember(Guid userId);

    [Alias(nameof(GetChannels))]
    Task<List<RealtimeChannel>> GetChannels();

    [Alias(nameof(DoJoinUserAsync))]
    Task DoJoinUserAsync();

    [Alias(nameof(DoUserUpdatedAsync))]
    Task DoUserUpdatedAsync(ArgonUser user);

    [Alias(nameof(DoUserProfileUpdatedAsync))]
    Task DoUserProfileUpdatedAsync(Guid userId, ArgonUserProfile profile);

    [Alias(nameof(PrefetchProfile))]
    Task<ArgonUserProfile> PrefetchProfile(Guid userId);

    [Alias(nameof(PrefetchUser))]
    Task<ArgonUser> PrefetchUser(Guid userId, CancellationToken ct = default);

    [Alias(nameof(BeginUploadSpaceFile))]
    ValueTask<Either<BlobId, UploadFileError>> BeginUploadSpaceFile(SpaceFileKind kind, CancellationToken ct = default);

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
    ProfileHeader
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