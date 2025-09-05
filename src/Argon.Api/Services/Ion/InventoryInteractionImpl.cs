namespace Argon.Services.Ion;

using Argon.Api.Grains.Interfaces;
using ion.runtime;

public class InventoryInteractionImpl : IInventoryInteraction
{
    public async Task<IonArray<InventoryItem>> GetMyInventoryItems()
        => await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).GetMyItemsAsync();

    public async Task MarkSeen(IonArray<Guid> itemIds)
        => await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).MarkSeenAsync(itemIds.Values.ToList());

    public async Task<IonArray<InventoryNotification>> GetNotifications()
        => await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).GetNotificationsAsync();

    public async Task<IRedeemResult> RedeemCode(string code)
    {
        var result = await this.GetGrain<IInventoryGrain>(Guid.NewGuid()).RedeemCodeAsync(code);

        if (result is null)
            return new SuccessRedeem();
        return new FailedRedeem(result.Value);
    }
}