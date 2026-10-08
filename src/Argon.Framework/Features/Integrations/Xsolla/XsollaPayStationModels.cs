namespace Argon.Core.Features.Integrations.Xsolla;

using System.Text.Json.Serialization;

// ═══════════════════════════════════════════════════════════════════════════
// Token Response
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from POST /merchants/{merchant_id}/token
/// </summary>
public sealed class XsollaTokenResponse
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Error Response
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Standard Xsolla error response (422, 400, etc.)
/// </summary>
public sealed class XsollaErrorResponse
{
    [JsonPropertyName("http_status_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HttpStatusCode { get; init; }

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; init; }

    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    [JsonPropertyName("extended_message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaExtendedMessage? ExtendedMessage { get; init; }

    // IGS/BB style errors
    [JsonPropertyName("statusCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StatusCode { get; init; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ErrorCode { get; init; }

    [JsonPropertyName("errorMessage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("transactionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TransactionId { get; init; }
}

public sealed class XsollaExtendedMessage
{
    [JsonPropertyName("global_errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? GlobalErrors { get; init; }

    [JsonPropertyName("property_errors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? PropertyErrors { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Tokenization — Saved Payment Accounts
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Saved payment account from GET /projects/{project_id}/users/{user_id}/payment_accounts
/// </summary>
public sealed class XsollaSavedPaymentAccount
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; init; }

    [JsonPropertyName("payment_system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaPaymentSystemInfo? PaymentSystem { get; init; }
}

public sealed class XsollaPaymentSystemInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Tokenization — Charge with Saved Account
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request body for POST /projects/{project_id}/users/{user_id}/payments/{type}/{account_id}
/// </summary>
public sealed class XsollaChargeWithSavedAccountRequest
{
    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaChargeUser? User { get; init; }

    [JsonPropertyName("purchase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaChargePurchase? Purchase { get; init; }

    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaChargeSettings? Settings { get; init; }

    [JsonPropertyName("custom_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomParameters { get; init; }
}

public sealed class XsollaChargeUser
{
    [JsonPropertyName("ip")]
    public required string Ip { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("legal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaLegalEntity? Legal { get; init; }
}

public sealed class XsollaChargePurchase
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaChargeDescription? Description { get; init; }

    [JsonPropertyName("virtual_currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaChargeVirtualCurrency? VirtualCurrency { get; init; }

    [JsonPropertyName("checkout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCheckout? Checkout { get; init; }
}

public sealed class XsollaChargeDescription
{
    [JsonPropertyName("value")]
    public required string Value { get; init; }
}

public sealed class XsollaChargeVirtualCurrency
{
    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }
}

public sealed class XsollaChargeSettings
{
    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; init; }

    [JsonPropertyName("mock_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MockCode { get; init; }
}

/// <summary>
/// Response from POST /projects/{project_id}/users/{user_id}/payments/{type}/{account_id}
/// </summary>
public sealed class XsollaChargeWithSavedAccountResponse
{
    [JsonPropertyName("transaction_id")]
    public int TransactionId { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Reports — Transaction List / Registry
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Transaction item from GET /merchants/{merchant_id}/reports/transactions/registry.json
/// and /reports/transactions/search.json
/// </summary>
public sealed class XsollaTransactionListItem
{
    [JsonPropertyName("transaction_id")]
    public int TransactionId { get; init; }

    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("user_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserId { get; init; }

    [JsonPropertyName("user_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserName { get; init; }

    [JsonPropertyName("user_email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserEmail { get; init; }

    [JsonPropertyName("user_country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UserCountry { get; init; }

    [JsonPropertyName("payment_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PaymentMethod { get; init; }

    [JsonPropertyName("datetime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateTime { get; init; }

    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("project_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProjectId { get; init; }

    [JsonPropertyName("dry_run")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DryRun { get; init; }

    [JsonPropertyName("order_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OrderId { get; init; }

    [JsonPropertyName("transfer_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TransferId { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Reports — Transaction Details
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Full transaction detail from GET /merchants/{merchant_id}/reports/transactions/{transaction_id}/details
/// </summary>
public sealed class XsollaTransactionDetails
{
    [JsonPropertyName("transaction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionInfo? Transaction { get; init; }

    [JsonPropertyName("payment_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionPaymentDetails? PaymentDetails { get; init; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionUser? User { get; init; }

    [JsonPropertyName("purchase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionPurchase? Purchase { get; init; }

    [JsonPropertyName("custom_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomParameters { get; init; }

    [JsonPropertyName("refund_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionRefundDetails? RefundDetails { get; init; }
}

public sealed class XsollaTransactionInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("external_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExternalId { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("dry_run")]
    public bool DryRun { get; init; }

    [JsonPropertyName("datetime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateTime { get; init; }

    [JsonPropertyName("project_id")]
    public int ProjectId { get; init; }

    [JsonPropertyName("order_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OrderId { get; init; }

    [JsonPropertyName("transfer_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TransferId { get; init; }

    [JsonPropertyName("subscription_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SubscriptionId { get; init; }
}

public sealed class XsollaTransactionPaymentDetails
{
    [JsonPropertyName("payment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionPayment? Payment { get; init; }

    [JsonPropertyName("payout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionPayout? Payout { get; init; }

    [JsonPropertyName("payment_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionPaymentMethod? PaymentMethod { get; init; }

    [JsonPropertyName("sales_tax")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionTax? SalesTax { get; init; }

    [JsonPropertyName("vat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionTax? Vat { get; init; }

    [JsonPropertyName("xsolla_fee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionFee? XsollaFee { get; init; }

    [JsonPropertyName("payout_currency_rate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? PayoutCurrencyRate { get; init; }
}

public sealed class XsollaTransactionPayment
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

public sealed class XsollaTransactionPayout
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("amount_from_ps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? AmountFromPs { get; init; }

    [JsonPropertyName("fx_rate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? FxRate { get; init; }
}

public sealed class XsollaTransactionPaymentMethod
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

public sealed class XsollaTransactionTax
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("percent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Percent { get; init; }
}

public sealed class XsollaTransactionFee
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("percent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Percent { get; init; }
}

public sealed class XsollaTransactionUser
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; init; }

    [JsonPropertyName("phone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Phone { get; init; }

    [JsonPropertyName("country")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Country { get; init; }

    [JsonPropertyName("ip")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ip { get; init; }
}

public sealed class XsollaTransactionPurchase
{
    [JsonPropertyName("virtual_items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionVirtualItems? VirtualItems { get; init; }

    [JsonPropertyName("virtual_currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionVirtualCurrency? VirtualCurrency { get; init; }

    [JsonPropertyName("subscription")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionSubscription? Subscription { get; init; }

    [JsonPropertyName("checkout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaCheckout? Checkout { get; init; }
}

public sealed class XsollaTransactionVirtualItems
{
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionVirtualItem[]? Items { get; init; }
}

public sealed class XsollaTransactionVirtualItem
{
    [JsonPropertyName("sku")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sku { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; init; }

    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

public sealed class XsollaTransactionVirtualCurrency
{
    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("quantity")]
    public decimal Quantity { get; init; }

    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }
}

public sealed class XsollaTransactionSubscription
{
    [JsonPropertyName("plan_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PlanId { get; init; }

    [JsonPropertyName("subscription_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SubscriptionId { get; init; }

    [JsonPropertyName("product_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProductId { get; init; }
}

public sealed class XsollaTransactionRefundDetails
{
    [JsonPropertyName("code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Code { get; init; }

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    [JsonPropertyName("author")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Author { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Reports — Simple Search
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from GET /merchants/{merchant_id}/reports/transactions/simple_search
/// </summary>
public sealed class XsollaSimpleSearchResponse
{
    [JsonPropertyName("transactions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaTransactionListItem[]? Transactions { get; init; }

    [JsonPropertyName("has_more")]
    public bool HasMore { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Reports — Report List
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Report item from GET /merchants/{merchant_id}/reports
/// </summary>
public sealed class XsollaReportListItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("period_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PeriodFrom { get; init; }

    [JsonPropertyName("period_to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PeriodTo { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("amount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("created")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Created { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Reports — Payouts / Transfers
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Payout/transfer item from GET /merchants/{merchant_id}/reports/transfers
/// </summary>
public sealed class XsollaPayoutListItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("datetime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateTime { get; init; }
}

/// <summary>
/// Payout breakdown from GET /merchants/{merchant_id}/reports/transactions/summary/transfer
/// </summary>
public sealed class XsollaPayoutBreakdown
{
    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("amount")]
    public decimal Amount { get; init; }

    [JsonPropertyName("transactions_count")]
    public int TransactionsCount { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// Refund
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Full refund request: PUT /merchants/{merchant_id}/reports/transactions/{transaction_id}/refund
/// </summary>
public sealed class XsollaRefundRequest
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }
}

/// <summary>
/// Partial refund request: PUT /merchants/{merchant_id}/reports/transactions/{transaction_id}/partial_refund
/// </summary>
public sealed class XsollaPartialRefundRequest
{
    [JsonPropertyName("amount")]
    public required decimal Amount { get; init; }

    [JsonPropertyName("currency")]
    public required string Currency { get; init; }

    [JsonPropertyName("comment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; init; }
}

// ═══════════════════════════════════════════════════════════════════════════
// In-Game Store / Buy Button — Create Order
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Response from POST /v2/project/{project_id}/payment/item/{item_sku} (client-side)
/// or POST /api/v2/project/{project_id}/admin/payment/token (server-side)
/// </summary>
public sealed class XsollaOrderResponse
{
    [JsonPropertyName("token")]
    public required string Token { get; init; }

    [JsonPropertyName("order_id")]
    public int OrderId { get; init; }
}

/// <summary>
/// Request body for POST /v2/project/{project_id}/payment/item/{item_sku} (client-side order)
/// </summary>
public sealed class XsollaCreateOrderRequest
{
    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; init; }

    [JsonPropertyName("locale")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Locale { get; init; }

    [JsonPropertyName("sandbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sandbox { get; init; }

    [JsonPropertyName("quantity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Quantity { get; init; }

    [JsonPropertyName("promo_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromoCode { get; init; }

    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaOrderSettings? Settings { get; init; }

    [JsonPropertyName("custom_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomParameters { get; init; }
}

public sealed class XsollaOrderSettings
{
    [JsonPropertyName("ui")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaUiSettings? Ui { get; init; }

    [JsonPropertyName("payment_method")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PaymentMethod { get; init; }

    [JsonPropertyName("return_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReturnUrl { get; init; }

    [JsonPropertyName("redirect_policy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaRedirectPolicy? RedirectPolicy { get; init; }
}

/// <summary>
/// Server-side token creation request: POST /api/v2/project/{project_id}/admin/payment/token
/// </summary>
public sealed class XsollaAdminPaymentTokenRequest
{
    [JsonPropertyName("user")]
    public required XsollaAdminPaymentUser User { get; init; }

    [JsonPropertyName("purchase")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaAdminPaymentPurchase? Purchase { get; init; }

    [JsonPropertyName("sandbox")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Sandbox { get; init; }

    [JsonPropertyName("settings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaOrderSettings? Settings { get; init; }

    [JsonPropertyName("custom_parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, object>? CustomParameters { get; init; }
}

public sealed class XsollaAdminPaymentUser
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
    public XsollaCountryValue? Country { get; init; }
}

public sealed class XsollaAdminPaymentPurchase
{
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public XsollaAdminPaymentItem[]? Items { get; init; }
}

public sealed class XsollaAdminPaymentItem
{
    [JsonPropertyName("sku")]
    public required string Sku { get; init; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; init; } = 1;
}

// ═══════════════════════════════════════════════════════════════════════════
// Chargeback Testing
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// Request for POST /merchants/{merchant_id}/projects/{project_id}/payments/{transaction_id}/chargeback
/// </summary>
public sealed class XsollaChargebackTestRequest
{
    // Empty body required per spec
}
