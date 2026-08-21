namespace Argon.Services.Ion;

using Argon.Api.Grains.Interfaces;
using ion.runtime;

public class InventoryInteractionImpl : IInventoryInteraction
{
    public async Task<IonArray<InventoryItem>> GetMyInventoryItems(CancellationToken ct = default)
        => await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).GetMyItemsAsync(ct);

    public async Task MarkSeen(IonArray<Guid> itemIds, CancellationToken ct = default)
        => await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).MarkSeenAsync(itemIds.Values.ToList(), ct);

    public async Task<IonArray<InventoryNotification>> GetNotifications(CancellationToken ct = default)
        => await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).GetNotificationsAsync(ct);

    public async Task<IRedeemResult> RedeemCode(string code, CancellationToken ct = default)
    {
        var result = await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).RedeemCodeAsync(code, ct);

        if (result is null)
            return new SuccessRedeem();
        return new FailedRedeem(result.Value);
    }

    public async Task<bool> UseItem(Guid itemId, CancellationToken ct = default)
        => await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).UseItemAsync(itemId, ct);
}