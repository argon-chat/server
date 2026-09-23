namespace Argon.Grains.Interfaces;

using ion.runtime;

/// <summary>
/// One person's cosmetics: what they may wear, and putting things on and taking them off.
/// </summary>
/// <remarks>
/// <para><b>One activation per person, and deliberately not a stateless worker.</b> Every change
/// here is a read, a check and a write against rows only this person's calls touch, and running two
/// of them side by side is how two equips of the same slot race to the key and one of them comes back
/// as a database error instead of an answer. A turn-based grain keyed by the person makes that
/// impossible rather than handled.</para>
///
/// <para>Keyed by the user id.</para>
/// </remarks>
[Alias($"Argon.Grains.Interfaces.{nameof(ICosmeticsGrain)}")]
public interface ICosmeticsGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetMyCosmeticsAsync))]
    Task<MyCosmetics> GetMyCosmeticsAsync();

    /// <summary>Puts something on, replacing whatever its kind had: a catalogue row, or a composed look.</summary>
    [Alias(nameof(EquipAsync))]
    Task<IEquipResult> EquipAsync(IWornCosmetic cosmetic);

    [Alias(nameof(UnequipAsync))]
    Task<IEquipResult> UnequipAsync(string kindKey);

    /// <summary>
    /// Takes off whatever this person may no longer wear, after something they held has lapsed.
    /// </summary>
    /// <remarks>
    /// Called from <c>UserGrain</c> as a subscription ends, before it announces the profile — so it
    /// must never call back into <c>UserGrain</c>, which is waiting on it.
    /// </remarks>
    [Alias(nameof(RevalidateAsync))]
    Task RevalidateAsync();
}
