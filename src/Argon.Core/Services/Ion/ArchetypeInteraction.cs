namespace Argon.Services.Ion;

using ArgonContracts;
using ion.runtime;

public class ArchetypeInteraction : IArchetypeInteraction
{
    public async Task<IonArray<Archetype>> GetServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetServerArchetypes();

    public async Task<ICreateArchetypeResult> CreateArchetype(Guid spaceId, string name, CancellationToken ct = default)
    {
        var (error, archetype) = await this.GetGrain<IEntitlementGrain>(spaceId).CreateArchetypeAsync(name);

        return error is ArchetypeError.NONE
            ? new SuccessCreateArchetype(archetype!)
            : new FailedCreateArchetype(error);
    }

    public async Task<IUpdateArchetypeResult> UpdateArchetype(Guid spaceId, Archetype data, CancellationToken ct = default)
    {
        var (error, archetype) = await this.GetGrain<IEntitlementGrain>(spaceId).UpdateArchetypeAsync(data);

        return error is ArchetypeError.NONE
            ? new SuccessUpdateArchetype(archetype!)
            : new FailedUpdateArchetype(error);
    }

    public async Task<bool> SetArchetypeToMember(Guid spaceId, Guid memberId, Guid archetypeId, bool isGrant, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).SetArchetypeToMember(memberId, archetypeId, isGrant);

    public async Task<IonArray<ArchetypeGroup>> GetDetailedServerArchetypes(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetFullyServerArchetypes();

    public async Task<ChannelEntitlementOverwrite?> UpsertArchetypeEntitlementForChannel(Guid spaceId, Guid channelId, Guid archetypeId, ArgonEntitlement deny, ArgonEntitlement allow, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).UpsertArchetypeEntitlementForChannel(channelId, archetypeId, deny, allow);

    public async Task<IonArray<ChannelEntitlementOverwrite>> GetChannelEntitlementOverwrites(Guid spaceId, Guid channelId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).GetChannelEntitlementOverwrites(channelId);

    public async Task<bool> DeleteEntitlementForChannel(Guid spaceId, Guid channelId, Guid entitlementOverwriteId, CancellationToken ct = default)
        => await this.GetGrain<IEntitlementGrain>(spaceId).DeleteEntitlementForChannel(channelId, entitlementOverwriteId);

    public async Task<IDeleteArchetypeResult> DeleteArchetype(Guid spaceId, Guid archetypeId, CancellationToken ct = default)
    {
        var error = await this.GetGrain<IEntitlementGrain>(spaceId).DeleteArchetypeAsync(archetypeId);

        return error is ArchetypeError.NONE
            ? new SuccessDeleteArchetype()
            : new FailedDeleteArchetype(error);
    }

    public async Task<IReorderArchetypesResult> ReorderArchetypes(Guid spaceId, IonArray<Guid> ordered, CancellationToken ct = default)
    {
        var (error, archetypes) = await this.GetGrain<IEntitlementGrain>(spaceId).ReorderArchetypesAsync([.. ordered]);

        return error is ArchetypeError.NONE
            ? new SuccessReorderArchetypes(new IonArray<Archetype>(archetypes.ToArray()))
            : new FailedReorderArchetypes(error);
    }
}