namespace Argon.Core.Features.Integrations.Xsolla;

public interface IXsollaService
{
    Task<(string checkoutUrl, string sessionId)> CreateSubscriptionCheckoutAsync(Guid userId, string email, UltimaPlan plan, string countryCode, CancellationToken ct = default);
    Task<string> CreateBoostPackCheckoutAsync(Guid userId, string email, BoostPackType pack, string countryCode, CancellationToken ct = default);
    Task<string> CreateGiftCheckoutAsync(Guid senderId, string email, Guid recipientId, UltimaPlan plan, string? giftMessage, string countryCode, CancellationToken ct = default);
    Task<bool> CancelSubscriptionAsync(string xsollaSubscriptionId, CancellationToken ct = default);
    Task<PaymentAccountInfo?> GetPaymentAccountAsync(Guid userId, CancellationToken ct = default);
    Task UpdateUserAttributeAsync(Guid userId, string key, string value, CancellationToken ct = default);
    Task<UltimaPricing> GetPricingAsync(Guid userId, string countryCode, CancellationToken ct = default);
    bool ValidateWebhookSignature(string body, string signature);

    // ── Catalog API ─────────────────────────────────────────────────────

    Task<XsollaCatalogPaymentTokenResponse> CreateCatalogPaymentTokenAsync(Guid userId, string email, string countryCode, List<XsollaCatalogPurchaseItem> items, Dictionary<string, object>? customParameters = null, CancellationToken ct = default);
    Task<XsollaOrderStatusResponse?> GetOrderAsync(long orderId, string? userJwt = null, CancellationToken ct = default);
    Task<XsollaVirtualItemsListResponse?> GetVirtualItemsAsync(string? locale = null, string? country = null, int limit = 50, int offset = 0, string? userJwt = null, CancellationToken ct = default);
    Task<XsollaVirtualItemsListResponse?> GetVirtualItemsByGroupAsync(string groupExternalId, string? locale = null, string? country = null, int limit = 50, int offset = 0, string? userJwt = null, CancellationToken ct = default);
    Task<XsollaCatalogVirtualItem?> GetVirtualItemBySkuAsync(string sku, string? locale = null, string? country = null, string? userJwt = null, CancellationToken ct = default);
    Task<XsollaCatalogItemGroupsResponse?> GetItemGroupsAsync(string? locale = null, CancellationToken ct = default);

    // ── Admin API ───────────────────────────────────────────────────────

    Task<XsollaAdminVirtualItemsListResponse?> AdminGetVirtualItemsAsync(int limit = 50, int offset = 0, CancellationToken ct = default);
    Task<XsollaAdminVirtualItemResponse?> AdminGetVirtualItemAsync(string sku, CancellationToken ct = default);
    Task AdminCreateVirtualItemAsync(XsollaAdminVirtualItemRequest item, CancellationToken ct = default);
    Task AdminUpdateVirtualItemAsync(string sku, XsollaAdminVirtualItemRequest item, CancellationToken ct = default);
    Task AdminDeleteVirtualItemAsync(string sku, CancellationToken ct = default);
    Task<XsollaAdminItemGroupsListResponse?> AdminGetItemGroupsAsync(CancellationToken ct = default);
    Task<XsollaAdminItemGroupDetailResponse?> AdminGetItemGroupAsync(string externalId, CancellationToken ct = default);
    Task AdminCreateItemGroupAsync(XsollaAdminItemGroupRequest group, CancellationToken ct = default);
    Task AdminUpdateItemGroupAsync(string externalId, XsollaAdminItemGroupRequest group, CancellationToken ct = default);
    Task AdminDeleteItemGroupAsync(string externalId, CancellationToken ct = default);
}
