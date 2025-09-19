namespace Argon.Api.Grains.Interfaces;


[Alias(nameof(IInventoryGrain))]
public interface IInventoryGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetMyItemsAsync))]
    Task<List<InventoryItem>> GetMyItemsAsync(CancellationToken ct = default);

    [Alias(nameof(GetNotificationsAsync))]
    Task<List<InventoryNotification>> GetNotificationsAsync(CancellationToken ct = default);

    [Alias(nameof(MarkSeenAsync))]
    Task MarkSeenAsync(List<Guid> inventoryItemIds, CancellationToken ct = default);

    [Alias(nameof(RedeemCodeAsync))]
    Task<RedeemError?> RedeemCodeAsync(string code, CancellationToken ct = default);

    [Alias(nameof(UseItemAsync))]
    Task<bool> UseItemAsync(Guid itemId, CancellationToken ct = default);

    [Alias(nameof(GetItemsForUserAsync))]
    Task<List<InventoryItem>> GetItemsForUserAsync(Guid userId, CancellationToken ct = default);

    [Alias(nameof(GetReferencesItemsAsync))]
    Task<List<InventoryItem>> GetReferencesItemsAsync(CancellationToken ct = default);
}