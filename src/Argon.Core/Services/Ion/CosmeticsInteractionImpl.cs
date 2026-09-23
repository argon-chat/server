namespace Argon.Services.Ion;

using ion.runtime;

/// <summary>
/// The wardrobe's calls, handed to the grains that answer them.
/// </summary>
/// <remarks>
/// Nothing here but the hand-off, so the entry point needs neither the kind registry nor the
/// cosmetics cache: both live with the grains, on the silo, and no client role carries them.
/// </remarks>
public class CosmeticsInteractionImpl : ICosmeticsInteraction
{
    public async Task<CosmeticCatalogue> GetCatalogue(CancellationToken ct = default)
        => await this.GetGrain<ICosmeticsReadGrain>(Guid.Empty).GetCatalogueAsync();

    public async Task<MyCosmetics> GetMyCosmetics(CancellationToken ct = default)
        => await Mine().GetMyCosmeticsAsync();

    public async Task<IEquipResult> Equip(IWornCosmetic cosmetic, CancellationToken ct = default)
        => await Mine().EquipAsync(cosmetic);

    public async Task<IEquipResult> Unequip(string kindKey, CancellationToken ct = default)
        => await Mine().UnequipAsync(kindKey);

    private ICosmeticsGrain Mine() => this.GetGrain<ICosmeticsGrain>(this.GetUserId());
}
