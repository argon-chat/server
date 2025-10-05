namespace Argon.Services.Ion;

using ArgonContracts;
using ion.runtime;

public class ArchetypeInteraction : IArchetypeInteraction
{
    public async Task<IonArray<Archetype>> GetServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

    public async Task<Archetype> CreateArchetype(Guid spaceId, string name, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).CreateArchetypeAsync(name);

    public async Task<Archetype> UpdateArchetype(Guid spaceId, Archetype data, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).UpdateArchetypeAsync(data);

    public async Task<bool> SetArchetypeToMember(Guid spaceId, Guid memberId, Guid archetypeId, bool isGrant, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).SetArchetypeToMember(memberId, archetypeId, isGrant);

    public async Task<IonArray<ArchetypeGroup>> GetDetailedServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetFullyServerArchetypes();

    public async Task<ChannelEntitlementOverwrite?> UpsertArchetypeEntitlementForChannel(Guid spaceId, Guid channelId, Guid archetypeId, ArgonEntitlement deny, ArgonEntitlement allow, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).UpsertArchetypeEntitlementForChannel(channelId, archetypeId, deny, allow);
}