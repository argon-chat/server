namespace Argon.Grains.Interfaces;

using ion.runtime;

/// <summary>
/// The read side of cosmetics: the catalogue, and what people are wearing.
/// </summary>
/// <remarks>
/// <para>Apart from <see cref="ICosmeticsGrain"/> for the same reason <see cref="ISpaceReadGrain"/>
/// is apart from <see cref="ISpaceGrain"/>. The write grain is one activation per person and
/// serialises that person's changes; reads are about many people at once and have no need to queue
/// behind anybody's writes.</para>
///
/// <para>A <c>[StatelessWorker]</c>, so there is a pool per silo and a call to it from
/// <c>SpaceGrain</c> or <c>UserGrain</c> never leaves the silo. Always keyed <see cref="Guid.Empty"/>:
/// the answers depend on who is asked about, never on who asks.</para>
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(ICosmeticsReadGrain)}")]
public interface ICosmeticsReadGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetCatalogueAsync))]
    Task<CosmeticCatalogue> GetCatalogueAsync();

    /// <summary>
    /// What each of these people is wearing, one entry per distinct id asked about.
    /// </summary>
    /// <remarks>
    /// Read through one cache entry per person, with every miss in the call resolved by the same
    /// two queries however many there are. A member list of a hundred people who have all been read
    /// in the last few minutes costs no database round trip at all.
    /// </remarks>
    [Alias(nameof(GetWornAsync))]
    Task<Dictionary<Guid, IonArray<IWornCosmetic>>> GetWornAsync(List<Guid> userIds);

    /// <summary>
    /// The kinds that are switched on. A kind is on unless a flag row exists and says otherwise.
    /// </summary>
    [Alias(nameof(GetEnabledKindsAsync))]
    Task<HashSet<string>> GetEnabledKindsAsync();
}
