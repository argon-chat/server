namespace Argon.Grains.Interfaces;

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