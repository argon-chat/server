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

    /// <summary>Removes an archetype and every grant of it. <see cref="ArchetypeError.NONE"/> on success.</summary>
    [Alias(nameof(DeleteArchetypeAsync))]
    Task<ArchetypeError> DeleteArchetypeAsync(Guid archetypeId);

    /// <summary>
    /// Rewrites the whole hierarchy from a complete list of ids, highest first, and returns the
    /// space's archetypes in their new order.
    /// </summary>
    /// <remarks>
    /// The whole list rather than one moved id: two people dragging at once would otherwise each
    /// write a position computed against a hierarchy the other had already changed. A list that
    /// does not name every archetype exactly once is rejected as
    /// <see cref="ArchetypeError.INCOMPLETE_ORDER"/> rather than partially applied.
    /// </remarks>
    [Alias(nameof(ReorderArchetypesAsync))]
    Task<(ArchetypeError error, List<Archetype> archetypes)> ReorderArchetypesAsync(List<Guid> ordered);

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