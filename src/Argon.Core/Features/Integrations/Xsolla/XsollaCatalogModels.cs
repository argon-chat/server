namespace Argon.Core.Features.Integrations.Xsolla;

using System.Text.Json.Serialization;

// ── Server-side Payment Token (v3) ─────────────────────────────────────

/// <summary>
/// POST /api/v3/project/{project_id}/admin/payment/token
/// Creates a payment token for purchasing virtual items server-side.
/// https://developers.xsolla.com/api/catalog/payment-server-side/admin-create-payment-token
/// </summary>
public sealed class XsollaCatalogPaymentTokenRequest
{
    [JsonPropertyName("user")]
    public required XsollaCatalogUser User { get; init; }

    [JsonPropertyName("purchase")]
    public required XsollaCatalogPurchase Purchase { get; init; }

    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCatalogPaymentSettings? Settings { get; init; }

    [JsonPropertyName("sandbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sandbox { get; init; }

    [JsonPropertyName("custom_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomParameters { get; init; }
}

public sealed class XsollaCatalogUser
{
    [JsonPropertyName("id")]
    public required XsollaStringValue Id { get; init; }

    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaStringValue? Email { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaStringValue? Name { get; init; }

    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCatalogCountry? Country { get; init; }
}

public sealed class XsollaCatalogCountry
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }

    [JsonPropertyName("allow_modify")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AllowModify { get; init; }
}

public sealed class XsollaCatalogPurchase
{
    [JsonPropertyName("items")]
    public required List<XsollaCatalogPurchaseItem> Items { get; init; }
}

public sealed class XsollaCatalogPurchaseItem
{
    [JsonPropertyName("sku")]
    public required string Sku { get; init; }

    [JsonPropertyName("quantity")]
    public required int Quantity { get; init; }
}

public sealed class XsollaCatalogPaymentSettings
{
    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("language")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Language { get; init; }

    [JsonPropertyName("payment_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PaymentMethod { get; init; }

    [JsonPropertyName("return_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnUrl { get; init; }

    [JsonPropertyName("redirect_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCatalogRedirectPolicy? RedirectPolicy { get; init; }

    [JsonPropertyName("ui")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCatalogUiSettings? Ui { get; init; }
}

public sealed class XsollaCatalogRedirectPolicy
{
    [JsonPropertyName("redirect_conditions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RedirectConditions { get; init; }

    [JsonPropertyName("delay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Delay { get; init; }

    [JsonPropertyName("status_for_manual_redirection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StatusForManualRedirection { get; init; }

    [JsonPropertyName("redirect_button_caption")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RedirectButtonCaption { get; init; }
}

public sealed class XsollaCatalogUiSettings
{
    [JsonPropertyName("theme")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Theme { get; init; }
}

/// <summary>Response from POST /api/v3/project/{project_id}/admin/payment/token</summary>
public sealed class XsollaCatalogPaymentTokenResponse
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = "";

    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }
}

// ── Order ───────────────────────────────────────────────────────────────

/// <summary>
/// GET /api/v2/project/{project_id}/order/{order_id}
/// </summary>
public sealed class XsollaOrderStatusResponse
{
    [JsonPropertyName("order_id")]
    public long OrderId { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaOrderContent? Content { get; init; }
}

public sealed class XsollaOrderContent
{
    [JsonPropertyName("is_free")]
    public bool IsFree { get; init; }

    [JsonPropertyName("items")]
    public List<XsollaOrderItem> Items { get; init; } = [];

    [JsonPropertyName("price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaOrderPrice? Price { get; init; }

    [JsonPropertyName("virtual_price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaOrderPrice? VirtualPrice { get; init; }
}

public sealed class XsollaOrderItem
{
    [JsonPropertyName("sku")]
    public string Sku { get; init; } = "";

    [JsonPropertyName("quantity")]
    public int Quantity { get; init; }

    [JsonPropertyName("is_free")]
    public bool IsFree { get; init; }

    [JsonPropertyName("price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaOrderPrice? Price { get; init; }
}

public sealed class XsollaOrderPrice
{
    [JsonPropertyName("amount")]
    public string Amount { get; init; } = "";

    [JsonPropertyName("amount_without_discount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AmountWithoutDiscount { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "";
}

// ── Catalog: Virtual Items ──────────────────────────────────────────────

/// <summary>
/// Response from GET /api/v2/project/{project_id}/items/virtual_items
/// </summary>
public sealed class XsollaVirtualItemsListResponse
{
    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    [JsonPropertyName("items")]
    public List<XsollaCatalogVirtualItem> Items { get; init; } = [];
}

public sealed class XsollaCatalogVirtualItem
{
    [JsonPropertyName("sku")]
    public string Sku { get; init; } = "";

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name { get; init; } = new();

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Description { get; init; }

    [JsonPropertyName("long_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LongDescription { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("virtual_item_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VirtualItemType { get; init; }

    [JsonPropertyName("is_free")]
    public bool IsFree { get; init; }

    [JsonPropertyName("can_be_bought")]
    public bool CanBeBought { get; init; }

    [JsonPropertyName("groups")]
    public List<XsollaCatalogItemGroup> Groups { get; init; } = [];

    [JsonPropertyName("price")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaOrderPrice? Price { get; init; }

    [JsonPropertyName("virtual_prices")]
    public List<XsollaCatalogVirtualPrice> VirtualPrices { get; init; } = [];

    [JsonPropertyName("attributes")]
    public List<XsollaCatalogItemAttribute> Attributes { get; init; } = [];

    [JsonPropertyName("limits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCatalogItemLimits? Limits { get; init; }

    [JsonPropertyName("custom_attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomAttributes { get; init; }
}

public sealed class XsollaCatalogItemGroup
{
    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = "";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("item_order_in_group")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ItemOrderInGroup { get; init; }
}

public sealed class XsollaCatalogVirtualPrice
{
    [JsonPropertyName("sku")]
    public string Sku { get; init; } = "";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("amount")]
    public int Amount { get; init; }

    [JsonPropertyName("amount_without_discount")]
    public int AmountWithoutDiscount { get; init; }

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }
}

public sealed class XsollaCatalogItemAttribute
{
    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = "";

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name { get; init; } = new();

    [JsonPropertyName("values")]
    public List<XsollaCatalogItemAttributeValue> Values { get; init; } = [];
}

public sealed class XsollaCatalogItemAttributeValue
{
    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = "";

    [JsonPropertyName("value")]
    public string Value { get; init; } = "";
}

public sealed class XsollaCatalogItemLimits
{
    [JsonPropertyName("per_user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCatalogPerUserLimit? PerUser { get; init; }
}

public sealed class XsollaCatalogPerUserLimit
{
    [JsonPropertyName("available")]
    public int Available { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }
}

// ── Admin: Virtual Item CRUD ────────────────────────────────────────────

/// <summary>
/// POST /api/v2/project/{project_id}/admin/items/virtual_items
/// PUT  /api/v2/project/{project_id}/admin/items/virtual_items/sku/{item_sku}
/// </summary>
public sealed class XsollaAdminVirtualItemRequest
{
    [JsonPropertyName("sku")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sku { get; init; }

    [JsonPropertyName("name")]
    public required Dictionary<string, string> Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Description { get; init; }

    [JsonPropertyName("long_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LongDescription { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("is_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsEnabled { get; init; }

    [JsonPropertyName("is_free")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsFree { get; init; }

    [JsonPropertyName("is_show_in_store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsShowInStore { get; init; }

    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }

    [JsonPropertyName("groups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Groups { get; init; }

    [JsonPropertyName("prices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<XsollaAdminItemPrice>? Prices { get; init; }

    [JsonPropertyName("vc_prices")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<XsollaAdminVcPrice>? VcPrices { get; init; }

    [JsonPropertyName("limits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaAdminItemLimit? Limits { get; init; }

    [JsonPropertyName("periods")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<XsollaAdminItemPeriod>? Periods { get; init; }

    [JsonPropertyName("custom_attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomAttributes { get; init; }

    [JsonPropertyName("media_list")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<XsollaAdminMediaItem>? MediaList { get; init; }
}

public sealed class XsollaAdminItemPrice
{
    [JsonPropertyName("amount")]
    public required decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("is_default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDefault { get; init; }

    [JsonPropertyName("is_enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsEnabled { get; init; }

    [JsonPropertyName("country_iso")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CountryIso { get; init; }
}

public sealed class XsollaAdminVcPrice
{
    [JsonPropertyName("sku")]
    public required string Sku { get; init; }

    [JsonPropertyName("amount")]
    public required decimal Amount { get; init; }

    [JsonPropertyName("is_default")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsDefault { get; init; }
}

public sealed class XsollaAdminItemLimit
{
    [JsonPropertyName("per_user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaAdminPerUserLimit? PerUser { get; init; }
}

public sealed class XsollaAdminPerUserLimit
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("recurrent_schedule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaAdminRecurrentSchedule? RecurrentSchedule { get; init; }
}

public sealed class XsollaAdminRecurrentSchedule
{
    [JsonPropertyName("interval_type")]
    public required string IntervalType { get; init; }
}

public sealed class XsollaAdminItemPeriod
{
    [JsonPropertyName("date_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateFrom { get; init; }

    [JsonPropertyName("date_until")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateUntil { get; init; }
}

public sealed class XsollaAdminMediaItem
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("url")]
    public required string Url { get; init; }
}

/// <summary>Response from GET admin virtual items list.</summary>
public sealed class XsollaAdminVirtualItemsListResponse
{
    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }

    [JsonPropertyName("items")]
    public List<XsollaAdminVirtualItemResponse> Items { get; init; } = [];
}

public sealed class XsollaAdminVirtualItemResponse
{
    [JsonPropertyName("sku")]
    public string Sku { get; init; } = "";

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name { get; init; } = new();

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Description { get; init; }

    [JsonPropertyName("long_description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? LongDescription { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; init; }

    [JsonPropertyName("is_free")]
    public bool IsFree { get; init; }

    [JsonPropertyName("is_show_in_store")]
    public bool IsShowInStore { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("groups")]
    public List<XsollaAdminItemGroupResponse> Groups { get; init; } = [];

    [JsonPropertyName("prices")]
    public List<XsollaAdminItemPrice> Prices { get; init; } = [];

    [JsonPropertyName("vc_prices")]
    public List<XsollaAdminVcPrice> VcPrices { get; init; } = [];

    [JsonPropertyName("limits")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaAdminItemLimit? Limits { get; init; }

    [JsonPropertyName("custom_attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomAttributes { get; init; }
}

public sealed class XsollaAdminItemGroupResponse
{
    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = "";

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name { get; init; } = new();
}

// ── Admin: Item Groups CRUD ─────────────────────────────────────────────

/// <summary>
/// POST /api/v2/project/{project_id}/admin/items/groups
/// PUT  /api/v2/project/{project_id}/admin/items/groups/{external_id}
/// </summary>
public sealed class XsollaAdminItemGroupRequest
{
    [JsonPropertyName("external_id")]
    public required string ExternalId { get; init; }

    [JsonPropertyName("name")]
    public required Dictionary<string, string> Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Description { get; init; }

    [JsonPropertyName("iconUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IconUrl { get; init; }

    [JsonPropertyName("isEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsEnabled { get; init; }

    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Order { get; init; }
}

/// <summary>Response from GET admin item groups list.</summary>
public sealed class XsollaAdminItemGroupsListResponse
{
    [JsonPropertyName("groups")]
    public List<XsollaAdminItemGroupDetailResponse> Groups { get; init; } = [];
}

public sealed class XsollaAdminItemGroupDetailResponse
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = "";

    [JsonPropertyName("name")]
    public Dictionary<string, string> Name { get; init; } = new();

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Description { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; init; }

    [JsonPropertyName("items_count")]
    public int ItemsCount { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }
}

// ── Catalog: Item Groups (client-side) ──────────────────────────────────

/// <summary>
/// Response from GET /api/v2/project/{project_id}/items/groups
/// </summary>
public sealed class XsollaCatalogItemGroupsResponse
{
    [JsonPropertyName("groups")]
    public List<XsollaCatalogItemGroupDetail> Groups { get; init; } = [];
}

public sealed class XsollaCatalogItemGroupDetail
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("external_id")]
    public string ExternalId { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; init; }

    [JsonPropertyName("order")]
    public int Order { get; init; }

    [JsonPropertyName("level")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Level { get; init; }

    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<XsollaCatalogItemGroupDetail>? Children { get; init; }
}
