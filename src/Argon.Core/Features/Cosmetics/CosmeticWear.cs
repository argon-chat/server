namespace Argon.Features.Cosmetics;

using Argon.Entities;

/// <summary>
/// Whether one person may have one catalogue item on.
/// </summary>
/// <remarks>
/// <para><b>Asked when something is put on, and not when it is drawn.</b> The draw path is every
/// member list and every message author line; asking there means reading ownership and subscription
/// state for everybody on screen, which is the read the whole cache exists to avoid. So the answer is
/// fixed when an item goes on, and it is asked again when it can change — a subscription lapsing, a
/// grant being revoked — by taking off whatever no longer passes.</para>
///
/// <para>One place, because the question is asked by equip, by an option chosen on an axis, and by
/// the sweep that follows a lapse. Stated three times it drifts, and the result is an item that can
/// be put on but is taken off again the next time anything happens.</para>
/// </remarks>
public static class CosmeticWear
{
    public static bool MayWear(CosmeticItemEntity item, CosmeticKindDefinition kind, bool hasPremium, bool holdsGrant)
        => IsFree(item, kind) || holdsGrant || (hasPremium && IsCoveredByUltima(item, kind));

    /// <summary>Everybody may wear it, with nothing held. What the catalogue tells a picker as <c>free</c>.</summary>
    public static bool IsFree(CosmeticItemEntity item, CosmeticKindDefinition kind)
        => kind.Entitlement is CosmeticEntitlement.Free || item.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.Free);

    /// <summary>
    /// An active subscription covers it. What the catalogue tells a picker as <c>ultima</c>.
    /// </summary>
    /// <remarks>
    /// UltimaTierRequired narrows this to a billing period, which the product does not sell differently
    /// yet, so it is not consulted.
    /// </remarks>
    public static bool IsCoveredByUltima(CosmeticItemEntity item, CosmeticKindDefinition kind)
        => kind.Entitlement is CosmeticEntitlement.UltimaOrOwned && item.AcquisitionMode.HasFlag(CosmeticAcquisitionMode.UltimaTier);
}
