namespace Argon.Grains.Interfaces;

using ArchetypeModel;
using Users;

[Alias($"Argon.Grains.Interfaces.{nameof(IEntitlementGrain)}")]
public interface IEntitlementGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetServerArchetypes))]
    Task<List<Archetype>> GetServerArchetypes();

    [Alias(nameof(GetFullyServerArchetypes))]
    Task<List<ArchetypeGroup>> GetFullyServerArchetypes();

    [Alias(nameof(CreateArchetypeAsync))]
    Task<Archetype> CreateArchetypeAsync( string name);

    [Alias(nameof(UpdateArchetypeAsync))]
    Task<Archetype?> UpdateArchetypeAsync(Archetype dto);

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

    [Alias(nameof(SetArchetypeToMember))]
    Task<bool> SetArchetypeToMember(Guid memberId, Guid archetypeId, bool isGrant);
}

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

    [Alias(nameof(CreateChannel))]
    Task<ChannelEntity> CreateChannel(ChannelInput input);

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
    ValueTask<ArgonUserProfile> PrefetchProfile(Guid userId);
}

public enum ServerCreationError
{
    BAD_MODEL
}


public sealed record ServerInput(
    string? Name,
    string? Description,
    string? AvatarUrl);