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


    [Alias(nameof(GiveItemFor))]
    Task<bool> GiveItemFor(Guid userId, Guid refItemId, CancellationToken ct = default);

    [Alias(nameof(CreateReferenceItem))]
    Task<bool> CreateReferenceItem(string templateId, bool isUsable, bool isGiftable, bool isAffectToBadge, CancellationToken ct = default);

    [Alias(nameof(CreateCaseForReferenceItem))]
    Task<Guid?> CreateCaseForReferenceItem(Guid refItemId, string caseTemplateId, CancellationToken ct = default);

    [Alias(nameof(GiveCoinFor))]
    Task<bool> GiveCoinFor(Guid userId, string coinTemplateId, CancellationToken ct = default);
}