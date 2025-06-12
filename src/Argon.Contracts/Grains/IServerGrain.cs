namespace Argon.Grains.Interfaces;

using ArchetypeModel;
using Users;

[Alias($"Argon.Grains.Interfaces.{nameof(IEntitlementGrain)}")]
public interface IEntitlementGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetServerArchetypes))]
    Task<List<ArchetypeDto>> GetServerArchetypes();

    [Alias(nameof(CreateArchetypeAsync))]
    Task<ArchetypeDto> CreateArchetypeAsync( string name);

    [Alias(nameof(UpdateArchetypeAsync))]
    Task<ArchetypeDto?> UpdateArchetypeAsync(ArchetypeDto dto);

    [Alias(nameof(GetChannelEntitlementOverwrites))]
    Task<List<ChannelEntitlementOverwrite>> GetChannelEntitlementOverwrites(Guid channelId);

    [Alias(nameof(UpsertArchetypeEntitlementForChannel))]
    Task<ChannelEntitlementOverwrite?>
        UpsertArchetypeEntitlementForChannel(Guid channelId, Guid archetypeId,
            ArgonEntitlement deny, ArgonEntitlement allow);

    [Alias(nameof(UpsertMemberEntitlementForChannel))]
    Task<ChannelEntitlementOverwrite?>
        UpsertMemberEntitlementForChannel(Guid channelId, Guid memberId,
            ArgonEntitlement deny, ArgonEntitlement allow);

    [Alias(nameof(DeleteEntitlementForChannel))]
    Task<bool> DeleteEntitlementForChannel(Guid channelId, Guid EntitlementOverwriteId);
}

[Alias($"Argon.Grains.Interfaces.{nameof(IServerGrain)}")]
public interface IServerGrain : IGrainWithGuidKey
{
    [Alias(nameof(CreateServer))]
    Task<Either<Server, ServerCreationError>> CreateServer(ServerInput input);

    [Alias(nameof(GetServer))]
    Task<Server> GetServer();

    [Alias(nameof(UpdateServer))]
    Task<Server> UpdateServer(ServerInput input);

    [Alias(nameof(DeleteServer))]
    Task DeleteServer();

    [Alias(nameof(CreateChannel))]
    Task<Channel> CreateChannel(ChannelInput input);

    [Alias(nameof(DeleteChannel))]
    Task DeleteChannel(Guid channelId);

    [Alias(nameof(SetUserStatus))]
    ValueTask SetUserStatus(Guid userId, UserStatus status);

    [Alias(nameof(SetUserPresence))]
    ValueTask SetUserPresence(Guid userId, UserActivityPresence presence);

    [Alias(nameof(RemoveUserPresence))]
    ValueTask RemoveUserPresence(Guid userId);

    [Alias(nameof(GetMembers))]
    Task<List<RealtimeServerMember>> GetMembers();

    [Alias(nameof(GetMember))]
    Task<RealtimeServerMember> GetMember(Guid userId);

    [Alias(nameof(GetChannels))]
    Task<List<RealtimeChannel>> GetChannels();

    [Alias(nameof(DoJoinUserAsync))]
    ValueTask DoJoinUserAsync();

    [Alias(nameof(DoUserUpdatedAsync))]
    ValueTask DoUserUpdatedAsync();

    [Alias(nameof(PrefetchProfile))]
    ValueTask<UserProfileDto> PrefetchProfile(Guid userId);
}

public enum ServerCreationError
{
    BAD_MODEL
}

[MessagePackObject(true)]
public sealed record ServerInput(
    string? Name,
    string? Description,
    string? AvatarUrl);