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
    Task DoUserUpdatedAsync();

    [Alias(nameof(PrefetchProfile))]
    Task<ArgonUserProfile> PrefetchProfile(Guid userId);

    [Alias(nameof(PrefetchUser))]
    Task<ArgonUser> PrefetchUser(Guid userId, CancellationToken ct = default);

    [Alias(nameof(BeginUploadSpaceFile))]
    ValueTask<Either<BlobId, UploadFileError>> BeginUploadSpaceFile(SpaceFileKind kind, CancellationToken ct = default);

    [Alias(nameof(CompleteUploadSpaceFile))]
    ValueTask CompleteUploadSpaceFile(Guid blobId, SpaceFileKind kind, CancellationToken ct = default);
}

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