namespace Argon.Core.Features.WebHooks;

using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Unified webhook payload (all fields nullable, routed by notification_type) ─

public sealed class XsollaWebhookPayload
{
    [JsonPropertyName("notification_type")]
    public string NotificationType { get; set; } = null!;

    // ─── Common ──────────────────────────────────────────────────────────────

    [JsonPropertyName("settings")]
    public XsollaWebhookSettings? Settings { get; set; }

    [JsonPropertyName("custom_parameters")]
    public JsonElement? CustomParameters { get; set; }

    // ─── User (payment/refund/validation/subscription webhooks) ──────────────

    [JsonPropertyName("user")]
    public XsollaWebhookUser? User { get; set; }

    // ─── Payment / Refund webhooks ───────────────────────────────────────────

    [JsonPropertyName("transaction")]
    public XsollaPaymentTransaction? Transaction { get; set; }

    [JsonPropertyName("purchase")]
    public XsollaPurchase? Purchase { get; set; }

    [JsonPropertyName("payment_details")]
    public XsollaPaymentDetails? PaymentDetails { get; set; }

    [JsonPropertyName("refund_details")]
    public XsollaRefundDetails? RefundDetails { get; set; }

    // ─── Subscription lifecycle webhooks ─────────────────────────────────────

    [JsonPropertyName("subscription")]
    public XsollaSubscription? Subscription { get; set; }

    // ─── Combined webhooks (order_paid / order_canceled) ─────────────────────

    [JsonPropertyName("items")]
    public XsollaOrderItem[]? Items { get; set; }

    [JsonPropertyName("order")]
    public XsollaOrder? Order { get; set; }

    [JsonPropertyName("billing")]
    public XsollaBilling? Billing { get; set; }

    // ─── AFS blocklist webhook ───────────────────────────────────────────────

    [JsonPropertyName("event")]
    public XsollaAfsEvent? Event { get; set; }

    // ─── Payment account webhooks ────────────────────────────────────────────

    [JsonPropertyName("payment_account")]
    public XsollaPaymentAccountInfo? PaymentAccount { get; set; }

    // ─── Dispute webhook ─────────────────────────────────────────────────────

    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("dispute")]
    public XsollaDispute? Dispute { get; set; }
}

// ─── Settings ────────────────────────────────────────────────────────────────

public sealed class XsollaWebhookSettings
{
    [JsonPropertyName("project_id")]
    public int ProjectId { get; set; }

    [JsonPropertyName("merchant_id")]
    public int MerchantId { get; set; }
}

// ─── User ────────────────────────────────────────────────────────────────────

public sealed class XsollaWebhookUser
{
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("zip")]
    public string? Zip { get; set; }

    // For user_search webhook
    [JsonPropertyName("public_id")]
    public string? PublicId { get; set; }

    // For combined webhooks (order_paid / order_canceled) user is {external_id, email, country}
    [JsonPropertyName("external_id")]
    public string? ExternalId { get; set; }

    // For partner_side_catalog webhook
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("locale")]
    public string? Locale { get; set; }
}

// ─── Money amounts (reusable) ────────────────────────────────────────────────

public sealed class XsollaMoneyAmount
{
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }
}

public sealed class XsollaMoneyAmountWithPercent
{
    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("percent")]
    public decimal? Percent { get; set; }
}

// ─── Transaction (payment/refund webhooks) ───────────────────────────────────

public sealed class XsollaPaymentTransaction
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("external_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ExternalId { get; set; }

    [JsonPropertyName("payment_date")]
    public string? PaymentDate { get; set; }

    [JsonPropertyName("payment_method")]
    public int? PaymentMethod { get; set; }

    [JsonPropertyName("payment_method_name")]
    public string? PaymentMethodName { get; set; }

    [JsonPropertyName("payment_method_order_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? PaymentMethodOrderId { get; set; }

    [JsonPropertyName("dry_run")]
    public int? DryRun { get; set; }

    [JsonPropertyName("agreement")]
    public int? Agreement { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }
}

// ─── Payment Details ─────────────────────────────────────────────────────────

public sealed class XsollaPaymentDetails
{
    [JsonPropertyName("payment")]
    public XsollaMoneyAmount? Payment { get; set; }

    [JsonPropertyName("payment_method_sum")]
    public XsollaMoneyAmount? PaymentMethodSum { get; set; }

    [JsonPropertyName("xsolla_balance_sum")]
    public XsollaMoneyAmount? XsollaBalanceSum { get; set; }

    [JsonPropertyName("payout")]
    public XsollaMoneyAmount? Payout { get; set; }

    [JsonPropertyName("vat")]
    public XsollaMoneyAmountWithPercent? Vat { get; set; }

    [JsonPropertyName("payout_currency_rate")]
    public string? PayoutCurrencyRate { get; set; }

    [JsonPropertyName("country_wht")]
    public XsollaMoneyAmountWithPercent? CountryWht { get; set; }

    [JsonPropertyName("user_acquisition_fee")]
    public XsollaMoneyAmountWithPercent? UserAcquisitionFee { get; set; }

    [JsonPropertyName("xsolla_fee")]
    public XsollaMoneyAmount? XsollaFee { get; set; }

    [JsonPropertyName("payment_method_fee")]
    public XsollaMoneyAmount? PaymentMethodFee { get; set; }

    [JsonPropertyName("sales_tax")]
    public XsollaMoneyAmountWithPercent? SalesTax { get; set; }

    [JsonPropertyName("direct_wht")]
    public XsollaMoneyAmountWithPercent? DirectWht { get; set; }

    [JsonPropertyName("repatriation_commission")]
    public XsollaMoneyAmount? RepatriationCommission { get; set; }
}

// ─── Purchase (payment/refund webhooks) ──────────────────────────────────────

public sealed class XsollaPurchase
{
    [JsonPropertyName("checkout")]
    public XsollaMoneyAmount? Checkout { get; set; }

    [JsonPropertyName("subscription")]
    public XsollaPurchaseSubscription? Subscription { get; set; }

    [JsonPropertyName("gift")]
    public XsollaPurchaseGift? Gift { get; set; }

    [JsonPropertyName("total")]
    public XsollaMoneyAmount? Total { get; set; }

    [JsonPropertyName("promotions")]
    public XsollaPromotion[]? Promotions { get; set; }

    [JsonPropertyName("coupon")]
    public XsollaCoupon? Coupon { get; set; }

    [JsonPropertyName("order")]
    public XsollaPurchaseOrder? Order { get; set; }
}

public sealed class XsollaPurchaseSubscription
{
    [JsonPropertyName("plan_id")]
    public string? PlanId { get; set; }

    [JsonPropertyName("subscription_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }

    [JsonPropertyName("date_create")]
    public string? DateCreate { get; set; }

    [JsonPropertyName("date_next_charge")]
    public string? DateNextCharge { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("trial_days")]
    public int? TrialDays { get; set; }
}

public sealed class XsollaPurchaseGift
{
    [JsonPropertyName("giver_id")]
    public string? GiverId { get; set; }

    [JsonPropertyName("receiver_id")]
    public string? ReceiverId { get; set; }

    [JsonPropertyName("receiver_email")]
    public string? ReceiverEmail { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("hide_giver_from_receiver")]
    public string? HideGiverFromReceiver { get; set; }
}

public sealed class XsollaPromotion
{
    [JsonPropertyName("technical_name")]
    public string? TechnicalName { get; set; }

    [JsonPropertyName("id")]
    public int? Id { get; set; }
}

public sealed class XsollaCoupon
{
    [JsonPropertyName("coupon_code")]
    public string? CouponCode { get; set; }

    [JsonPropertyName("campaign_code")]
    public string? CampaignCode { get; set; }
}

public sealed class XsollaPurchaseOrder
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("lineitems")]
    public XsollaPurchaseLineItem[]? LineItems { get; set; }
}

public sealed class XsollaPurchaseLineItem
{
    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("price")]
    public XsollaMoneyAmount? Price { get; set; }
}

// ─── Refund Details ──────────────────────────────────────────────────────────

public sealed class XsollaRefundDetails
{
    [JsonPropertyName("code")]
    public int? Code { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("date")]
    public string? Date { get; set; }
}

// ─── Order (combined webhooks: order_paid / order_canceled) ──────────────────

public sealed class XsollaOrderItem
{
    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("promotions")]
    public XsollaItemPromotion[]? Promotions { get; set; }

    [JsonPropertyName("is_pre_order")]
    public bool? IsPreOrder { get; set; }

    [JsonPropertyName("is_free")]
    public bool? IsFree { get; set; }

    [JsonPropertyName("is_bonus")]
    public bool? IsBonus { get; set; }

    [JsonPropertyName("is_bundle_content")]
    public bool? IsBundleContent { get; set; }

    [JsonPropertyName("custom_attributes")]
    public JsonElement? CustomAttributes { get; set; }
}

public sealed class XsollaItemPromotion
{
    [JsonPropertyName("amount_without_discount")]
    public string? AmountWithoutDiscount { get; set; }

    [JsonPropertyName("amount_with_discount")]
    public string? AmountWithDiscount { get; set; }

    [JsonPropertyName("sequence")]
    public int? Sequence { get; set; }
}

public sealed class XsollaOrder
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("currency_type")]
    public string? CurrencyType { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("amount")]
    public string? Amount { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("invoice_id")]
    public string? InvoiceId { get; set; }

    [JsonPropertyName("promotions")]
    public XsollaItemPromotion[]? Promotions { get; set; }

    [JsonPropertyName("coupons")]
    public XsollaOrderCouponCode[]? Coupons { get; set; }

    [JsonPropertyName("promocodes")]
    public XsollaOrderCouponCode[]? Promocodes { get; set; }
}

public sealed class XsollaOrderCouponCode
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("external_id")]
    public string? ExternalId { get; set; }
}

// ─── Billing (nested in combined webhooks) ───────────────────────────────────

public sealed class XsollaBilling
{
    [JsonPropertyName("notification_type")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("settings")]
    public XsollaWebhookSettings? Settings { get; set; }

    [JsonPropertyName("purchase")]
    public XsollaPurchase? Purchase { get; set; }

    [JsonPropertyName("transaction")]
    public XsollaPaymentTransaction? Transaction { get; set; }

    [JsonPropertyName("payment_details")]
    public XsollaPaymentDetails? PaymentDetails { get; set; }

    [JsonPropertyName("refund_details")]
    public XsollaRefundDetails? RefundDetails { get; set; }
}

// ─── Subscription (lifecycle webhooks) ───────────────────────────────────────

public sealed class XsollaSubscription
{
    [JsonPropertyName("plan_id")]
    public string? PlanId { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }

    [JsonPropertyName("subscription_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SubscriptionId { get; set; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("date_create")]
    public string? DateCreate { get; set; }

    [JsonPropertyName("date_next_charge")]
    public string? DateNextCharge { get; set; }

    [JsonPropertyName("date_end")]
    public string? DateEnd { get; set; }

    [JsonPropertyName("trial")]
    public XsollaSubscriptionTrial? Trial { get; set; }

    [JsonPropertyName("is_gift")]
    public bool? IsGift { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }
}

public sealed class XsollaSubscriptionTrial
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

// ─── AFS Blocklist Event ─────────────────────────────────────────────────────

public sealed class XsollaAfsEvent
{
    [JsonPropertyName("action")]
    public string? Action { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("parameter")]
    public string? Parameter { get; set; }

    [JsonPropertyName("parameter_value")]
    public string? ParameterValue { get; set; }

    [JsonPropertyName("date_of_last_action")]
    public string? DateOfLastAction { get; set; }

    [JsonPropertyName("transaction_id")]
    public string? TransactionId { get; set; }

    [JsonPropertyName("project_id")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? ProjectId { get; set; }
}

// ─── Payment Account ─────────────────────────────────────────────────────────

public sealed class XsollaPaymentAccountInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("payment_method")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? PaymentMethod { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

// ─── Dispute ─────────────────────────────────────────────────────────────────

public sealed class XsollaDispute
{
    [JsonPropertyName("incoming_date")]
    public string? IncomingDate { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

// ─── Custom parameters typed helper ──────────────────────────────────────────

public static class XsollaCustomParametersHelper
{
    public static string? GetString(JsonElement? element, string key)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;
        return obj.TryGetProperty(key, out var prop) ? prop.GetString() : null;
    }

    public static int? GetInt(JsonElement? element, string key)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;
        if (!obj.TryGetProperty(key, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.Number) return prop.GetInt32();
        if (prop.ValueKind == JsonValueKind.String && int.TryParse(prop.GetString(), out var v)) return v;
        return null;
    }
}

// ─── JSON Converter: reads int/long as string ────────────────────────────────

public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString("G"),
            JsonTokenType.Null   => null,
            _                    => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}
