namespace Argon.Core.Features.WebHooks;

using System.Text.Json;
using System.Text.Json.Serialization;

// ─── Root webhook payload ────────────────────────────────────────────────────

public sealed class XsollaWebhookPayload
{
    [JsonPropertyName("notification_type")]
    public string NotificationType { get; set; } = null!;

    [JsonPropertyName("user")]
    public XsollaWebhookUser? User { get; set; }

    [JsonPropertyName("transaction")]
    public XsollaWebhookTransaction? Transaction { get; set; }

    [JsonPropertyName("purchase")]
    public XsollaWebhookPurchase? Purchase { get; set; }

    [JsonPropertyName("custom_parameters")]
    public XsollaCustomParameters? CustomParameters { get; set; }

    [JsonPropertyName("payment_details")]
    public XsollaPaymentDetails? PaymentDetails { get; set; }

    // Subscription lifecycle webhooks
    [JsonPropertyName("subscription")]
    public XsollaWebhookSubscription? Subscription { get; set; }

    // Card info (from extended settings)
    [JsonPropertyName("card_suffix")]
    public string? CardSuffix { get; set; }

    [JsonPropertyName("card_bin")]
    public string? CardBin { get; set; }

    [JsonPropertyName("card_brand")]
    public string? CardBrand { get; set; }

    // Payment account (from extended settings)
    [JsonPropertyName("payment_account")]
    public XsollaPaymentAccount? PaymentAccount { get; set; }

    // order_paid / order_canceled
    [JsonPropertyName("order")]
    public XsollaWebhookOrder? Order { get; set; }

    [JsonPropertyName("items")]
    public XsollaOrderItem[]? Items { get; set; }

    // Refund
    [JsonPropertyName("refund_details")]
    public XsollaRefundDetails? RefundDetails { get; set; }
}

// ─── User ────────────────────────────────────────────────────────────────────

public sealed class XsollaWebhookUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("phone")]
    public string? Phone { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }
}

// ─── Transaction ─────────────────────────────────────────────────────────────

public sealed class XsollaWebhookTransaction
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("external_id")]
    public string? ExternalId { get; set; }

    [JsonPropertyName("dry_run")]
    public int DryRun { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("payment_method")]
    public XsollaPaymentMethod? PaymentMethod { get; set; }
}

[JsonConverter(typeof(XsollaPaymentMethodConverter))]
public sealed class XsollaPaymentMethod
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class XsollaPaymentMethodConverter : JsonConverter<XsollaPaymentMethod>
{
    public override XsollaPaymentMethod? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return new XsollaPaymentMethod { Id = reader.GetInt32() };

        if (reader.TokenType == JsonTokenType.String && int.TryParse(reader.GetString(), out var id))
            return new XsollaPaymentMethod { Id = id };

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            var method = new XsollaPaymentMethod();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                    return method;
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var prop = reader.GetString();
                reader.Read();
                switch (prop)
                {
                    case "id":
                        method.Id = reader.TokenType == JsonTokenType.Number
                            ? reader.GetInt32()
                            : int.TryParse(reader.GetString(), out var mid) ? mid : 0;
                        break;
                    case "name":
                        method.Name = reader.GetString();
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }
            return method;
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, XsollaPaymentMethod value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", value.Id);
        if (value.Name is not null)
            writer.WriteString("name", value.Name);
        writer.WriteEndObject();
    }
}

// ─── Purchase ────────────────────────────────────────────────────────────────

public sealed class XsollaWebhookPurchase
{
    [JsonPropertyName("subscription")]
    public XsollaWebhookPurchaseSubscription? Subscription { get; set; }

    [JsonPropertyName("virtual_items")]
    public XsollaWebhookVirtualItems? VirtualItems { get; set; }

    [JsonPropertyName("total")]
    public XsollaAmount? Total { get; set; }

    [JsonPropertyName("checkout")]
    public XsollaAmount? Checkout { get; set; }
}

public sealed class XsollaWebhookPurchaseSubscription
{
    [JsonPropertyName("plan_id")]
    public string? PlanId { get; set; }

    [JsonPropertyName("subscription_id")]
    public long SubscriptionId { get; set; }

    [JsonPropertyName("product_id")]
    public string? ProductId { get; set; }

    [JsonPropertyName("trial_days")]
    public int? TrialDays { get; set; }
}

public sealed class XsollaWebhookVirtualItems
{
    [JsonPropertyName("items")]
    public XsollaVirtualItemEntry[]? Items { get; set; }
}

public sealed class XsollaVirtualItemEntry
{
    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("amount")]
    public int Amount { get; set; }
}

public sealed class XsollaAmount
{
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

// ─── Custom Parameters ───────────────────────────────────────────────────────

public sealed class XsollaCustomParameters
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("pack_type")]
    public string? PackType { get; set; }

    [JsonPropertyName("boost_count")]
    public int? BoostCount { get; set; }

    [JsonPropertyName("recipient_id")]
    public string? RecipientId { get; set; }

    [JsonPropertyName("plan")]
    public string? Plan { get; set; }

    [JsonPropertyName("gift_message")]
    public string? GiftMessage { get; set; }
}

// ─── Payment Details ─────────────────────────────────────────────────────────

public sealed class XsollaPaymentDetails
{
    [JsonPropertyName("payment")]
    public XsollaPaymentInfo? Payment { get; set; }

    [JsonPropertyName("payout")]
    public XsollaPayoutInfo? Payout { get; set; }
}

public sealed class XsollaPaymentInfo
{
    [JsonPropertyName("amount_from_ps")]
    public decimal? AmountFromPs { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

public sealed class XsollaPayoutInfo
{
    [JsonPropertyName("fx_rate")]
    public decimal? FxRate { get; set; }
}

// ─── Payment Account ─────────────────────────────────────────────────────────

public sealed class XsollaPaymentAccount
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("payment_system")]
    public XsollaPaymentSystem? PaymentSystem { get; set; }
}

public sealed class XsollaPaymentSystem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

// ─── Subscription (lifecycle webhooks) ───────────────────────────────────────

public sealed class XsollaWebhookSubscription
{
    [JsonPropertyName("subscription_id")]
    public long SubscriptionId { get; set; }

    [JsonPropertyName("plan_id")]
    public string? PlanId { get; set; }

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

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class XsollaSubscriptionTrial
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

// ─── Order (order_paid / order_canceled) ─────────────────────────────────────

public sealed class XsollaWebhookOrder
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }
}

public sealed class XsollaOrderItem
{
    [JsonPropertyName("sku")]
    public string? Sku { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }

    [JsonPropertyName("currency")]
    public string? Currency { get; set; }
}

// ─── Refund Details ──────────────────────────────────────────────────────────

public sealed class XsollaRefundDetails
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}
