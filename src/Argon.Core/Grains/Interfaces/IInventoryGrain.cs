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
    Task<List<DetailedInventoryItem>> GetReferencesItemsAsync(CancellationToken ct = default);


    [Alias(nameof(GiveItemFor))]
    Task<bool> GiveItemFor(Guid userId, Guid refItemId, CancellationToken ct = default);

    [Alias(nameof(CreateReferenceItem))]
    Task<Guid?> CreateReferenceItem(string templateId, bool isUsable, bool isGiftable, bool isAffectToBadge, CancellationToken ct = default);

    [Alias(nameof(CreateCaseForReferenceItem))]
    Task<Guid?> CreateCaseForReferenceItem(Guid refItemId, string caseTemplateId, CancellationToken ct = default);

    [Alias(nameof(GiveCoinFor))]
    Task<bool> GiveCoinFor(Guid userId, string coinTemplateId, CancellationToken ct = default);

    /// <summary>
    /// The same coin, with the badge it puts on the profile named separately from the template.
    /// </summary>
    /// <remarks>
    /// <see cref="GiveCoinFor(Guid,string,CancellationToken)"/> writes the template id into
    /// <c>Badges</c>, which is right for the level coins the client lists by template. The contributor
    /// coin is <c>coin_argon_contributor</c> in the inventory and <c>contributor</c> on the card — the
    /// badge the client already draws — so the two ids are passed apart.
    /// </remarks>
    [Alias("GiveCoinWithBadgeFor")]
    Task<bool> GiveCoinFor(Guid userId, string coinTemplateId, string? badgeId, CancellationToken ct = default);

    [Alias(nameof(GiveUltimaGiftAsync))]
    Task<bool> GiveUltimaGiftAsync(Guid recipientId, string planId, int durationDays, Guid senderId, string? giftMessage, CancellationToken ct = default);

    [Alias(nameof(GiveBoostItemsAsync))]
    Task GiveBoostItemsAsync(Guid userId, int count, int durationDays, CancellationToken ct = default);
}