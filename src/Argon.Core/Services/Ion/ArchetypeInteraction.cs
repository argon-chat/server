namespace Argon.Services.Ion;

using ArgonContracts;
using ion.runtime;

public class ArchetypeInteraction : IArchetypeInteraction
{
    public async Task<IonArray<Archetype>> GetServerArchetypes(Guid spaceId)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

    public async Task<Archetype> CreateArchetype(Guid spaceId, string name)
        => await this.GetGrain<IEntitlementGrain>(spaceId).CreateArchetypeAsync(name);

    public async Task<Archetype> UpdateArchetype(Guid spaceId, Archetype data)
        => await this.GetGrain<IEntitlementGrain>(spaceId).UpdateArchetypeAsync(data);

    public async Task<bool> SetArchetypeToMember(Guid spaceId, Guid memberId, Guid archetypeId, bool isGrant)
        => await this.GetGrain<IEntitlementGrain>(spaceId).SetArchetypeToMember(memberId, archetypeId, isGrant);

    public async Task<IonArray<ArchetypeGroup>> GetDetailedServerArchetypes(Guid spaceId)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetFullyServerArchetypes();

    public async Task<ChannelEntitlementOverwrite?> UpsertArchetypeEntitlementForChannel(Guid spaceId, Guid channelId, Guid archetypeId, ArgonEntitlement deny, ArgonEntitlement allow)
        => await this.GetGrain<IEntitlementGrain>(spaceId).UpsertArchetypeEntitlementForChannel(channelId, archetypeId, deny, allow);
}