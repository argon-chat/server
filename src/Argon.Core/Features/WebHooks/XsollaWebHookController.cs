namespace Argon.Core.Features.WebHooks;

using System.Text.Json;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Features.Integrations.Xsolla;
using ArgonContracts;
using Argon.Entities;
using Microsoft.AspNetCore.Mvc;

[ApiController, ApiExplorerSettings(IgnoreApi = true)]
public class XsollaWebHookController(
    ILogger<XsollaWebHookController> logger,
    IClusterClient client,
    IXsollaService xsolla) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
    };

    [HttpPost("/api/xsolla/webhook")]
    public async Task<IActionResult> Webhook()
    {
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            logger.LogWarning("Xsolla webhook: no Authorization header");
            return Unauthorized();
        }

        var rawAuth = authHeader.ToString();
        var signature = rawAuth.Replace("Signature ", "");

        if (!xsolla.ValidateWebhookSignature(body, signature))
        {
            logger.LogWarning("Xsolla webhook: invalid signature");
            return BadRequest(new { error = new { code = "INVALID_SIGNATURE", message = "Invalid signature" } });
        }

        XsollaWebhookPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<XsollaWebhookPayload>(body, JsonOpts)!;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Xsolla webhook: failed to parse JSON body: {Body}", body.Length > 2000 ? body[..2000] : body);
            return BadRequest(new { error = new { code = "INVALID_JSON", message = ex.Message } });
        }

        logger.LogInformation("Xsolla webhook received: {Type}, body: {Body}",
            payload.NotificationType, body.Length > 2000 ? body[..2000] : body);

        try
        {
            switch (payload.NotificationType)
            {
                case "user_validation":
                    return await HandleUserValidation(payload);

                case "payment":
                    await HandlePayment(payload);
                    break;

                case "order_paid":
                    await HandleOrderPaid(payload);
                    break;

                case "refund":
                    await HandleRefund(payload);
                    break;

                case "order_canceled":
                    await HandleOrderCanceled(payload);
                    break;

                case "create_subscription":
                    await HandleCreateSubscription(payload);
                    break;

                case "update_subscription":
                    await HandleUpdateSubscription(payload);
                    break;

                case "cancel_subscription":
                    await HandleCancelSubscription(payload);
                    break;

                case "non_renewal_subscription":
                    await HandleNonRenewalSubscription(payload);
                    break;

                default:
                    logger.LogInformation("Xsolla webhook: unhandled notification type {Type}", payload.NotificationType);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Xsolla webhook processing failed for {Type}", payload.NotificationType);
            return StatusCode(500);
        }

        return NoContent();
    }

    // ─── Handlers ────────────────────────────────────────────────────────────────

    private async Task<IActionResult> HandleUserValidation(XsollaWebhookPayload payload)
    {
        var userIdStr = payload.User?.Id;

        if (!Guid.TryParse(userIdStr, out var userId))
        {
            logger.LogWarning("Xsolla user_validation: invalid userId '{UserId}'", userIdStr);
            return BadRequest(new { error = new { code = "INVALID_USER", message = "Invalid user" } });
        }

        try
        {
            await client.GetGrain<IUserGrain>(userId).GetAsArgonUser();
            return NoContent();
        }
        catch
        {
            return BadRequest(new { error = new { code = "INVALID_USER", message = "User not found" } });
        }
    }

    private async Task HandlePayment(XsollaWebhookPayload payload)
    {
        var txId = payload.Transaction!.Id.ToString();
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return;

        var (amount, currency) = ExtractAmount(payload);
        var (cardSuffix, cardBrand, paymentAccountId) = ExtractCardInfo(payload);

        // Route by subscription plan_id
        if (payload.Purchase?.Subscription is { PlanId: not null } sub)
        {
            var planId = sub.PlanId;
            var xsollaSubId = sub.SubscriptionId.ToString();

            if (IsBoostPlan(planId))
            {
                var (boostCount, source, durationDays) = ParseBoostPlan(planId);

                await client.GetGrain<IUltimaGrain>(userId)
                   .GrantPurchasedBoostsAsync(boostCount, source, txId, durationDays);

                await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
                   .GiveBoostItemsAsync(userId, boostCount, durationDays);

                await SaveTransaction(userId, txId, "boost_pack",
                    boostPackType: planId, boostCount: boostCount,
                    amount: amount, currency: currency,
                    cardSuffix: cardSuffix, cardBrand: cardBrand, paymentAccountId: paymentAccountId);

                logger.LogInformation("Xsolla: granted {Count} boosts to {UserId} (plan {Plan})", boostCount, userId, planId);
            }
            else if (planId is "ultima_monthly" or "ultima_annual")
            {
                var (tier, days) = ParsePlan(planId);

                await client.GetGrain<IUltimaGrain>(userId)
                   .ActivateSubscriptionAsync(tier, days, xsollaSubId, null);

                await SaveTransaction(userId, txId, "subscription",
                    planExternalId: planId,
                    amount: amount, currency: currency,
                    cardSuffix: cardSuffix, cardBrand: cardBrand, paymentAccountId: paymentAccountId);

                logger.LogInformation("Xsolla: activated subscription for {UserId}, plan {Plan}", userId, planId);
            }
            else
            {
                logger.LogWarning("Xsolla payment: unknown plan_id {PlanId}", planId);
            }
            return;
        }

        // Fallback: route by custom_parameters.type
        var type = payload.CustomParameters?.Type;

        switch (type)
        {
            case "gift":
                await HandleGiftPayment(payload, userId, txId, amount, currency, cardSuffix, cardBrand, paymentAccountId);
                break;
            default:
                logger.LogInformation("Xsolla payment: unhandled type {Type}", type);
                break;
        }
    }

    private async Task HandleGiftPayment(XsollaWebhookPayload payload, Guid userId, string txId,
        string? amount, string? currency, string? cardSuffix, string? cardBrand, long? paymentAccountId)
    {
        var cp = payload.CustomParameters!;

        if (!Guid.TryParse(cp.RecipientId, out var recipientId))
        {
            logger.LogWarning("Xsolla gift: invalid recipient_id {RecipientId}", cp.RecipientId);
            return;
        }

        var planId = cp.Plan switch
        {
            "Annual" => "ultima_annual",
            _        => "ultima_monthly"
        };
        var (_, days) = ParsePlan(planId);

        await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
           .GiveUltimaGiftAsync(recipientId, planId, days, userId, cp.GiftMessage);

        await client.GetGrain<IUltimaGrain>(userId)
           .GrantPurchasedBoostsAsync(2, BoostSource.GiftReward, txId, 30);

        await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
           .GiveBoostItemsAsync(userId, 2, 30);

        await SaveTransaction(userId, txId, "gift",
            planExternalId: planId, recipientId: recipientId,
            amount: amount, currency: currency,
            cardSuffix: cardSuffix, cardBrand: cardBrand, paymentAccountId: paymentAccountId);

        logger.LogInformation("Xsolla: gift from {SenderId} to {RecipientId}", userId, recipientId);
    }

    private async Task HandleOrderPaid(XsollaWebhookPayload payload)
    {
        // Combined webhook — contains both payment details + items
        // If purchase.subscription exists, treat like payment
        if (payload.Purchase?.Subscription is not null || payload.CustomParameters?.Type is not null)
        {
            await HandlePayment(payload);
            return;
        }

        logger.LogInformation("Xsolla order_paid: no actionable purchase data, order={OrderId}",
            payload.Order?.Id);
    }

    private async Task HandleRefund(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return;

        var txId = payload.Transaction?.Id.ToString();

        // Mark transaction as refunded
        if (txId is not null)
        {
            try
            {
                await client.GetGrain<IUltimaGrain>(userId).MarkTransactionRefundedAsync(txId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to mark transaction {TxId} as refunded", txId);
            }
        }

        await client.GetGrain<IUltimaGrain>(userId).ExpireSubscriptionAsync();
        logger.LogInformation("Xsolla: refund processed for {UserId}, txId={TxId}", userId, txId);
    }

    private async Task HandleOrderCanceled(XsollaWebhookPayload payload)
    {
        // Combined cancel webhook — expire subscription if applicable
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return;

        await client.GetGrain<IUltimaGrain>(userId).ExpireSubscriptionAsync();
        logger.LogInformation("Xsolla: order_canceled for {UserId}", userId);
    }

    private async Task HandleCreateSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return;

        var sub = payload.Subscription;
        if (sub is null) return;

        var planId = sub.PlanId;
        if (planId is null || IsBoostPlan(planId)) return; // boosts handled via payment webhook

        var (tier, days) = ParsePlan(planId);
        await client.GetGrain<IUltimaGrain>(userId)
           .ActivateSubscriptionAsync(tier, days, sub.SubscriptionId.ToString(), null);

        logger.LogInformation("Xsolla: create_subscription for {UserId}, plan {Plan}, subId {SubId}",
            userId, planId, sub.SubscriptionId);
    }

    private async Task HandleUpdateSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return;

        var sub = payload.Subscription;
        if (sub is null) return;

        var planId = sub.PlanId;
        if (planId is null || IsBoostPlan(planId)) return;

        var (tier, days) = ParsePlan(planId);
        await client.GetGrain<IUltimaGrain>(userId)
           .ActivateSubscriptionAsync(tier, days, sub.SubscriptionId.ToString(), null);

        logger.LogInformation("Xsolla: update_subscription for {UserId}, plan {Plan}", userId, planId);
    }

    private async Task HandleCancelSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return;

        await client.GetGrain<IUltimaGrain>(userId).CancelSubscriptionAsync();
        logger.LogInformation("Xsolla: cancel_subscription for {UserId}", userId);
    }

    private async Task HandleNonRenewalSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return;

        // Mark as non-renewing — same as cancel for our purposes
        await client.GetGrain<IUltimaGrain>(userId).CancelSubscriptionAsync();
        logger.LogInformation("Xsolla: non_renewal_subscription for {UserId}", userId);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private Guid ResolveUserId(XsollaWebhookPayload payload)
    {
        var str = payload.CustomParameters?.UserId ?? payload.User?.Id;
        if (Guid.TryParse(str, out var id)) return id;
        logger.LogWarning("Xsolla webhook: cannot resolve user_id from payload");
        return Guid.Empty;
    }

    private static (string? amount, string? currency) ExtractAmount(XsollaWebhookPayload payload)
    {
        if (payload.Purchase?.Total is { } total)
            return (total.Amount.ToString("G"), total.Currency);
        if (payload.Purchase?.Checkout is { } checkout)
            return (checkout.Amount.ToString("G"), checkout.Currency);
        if (payload.PaymentDetails?.Payment is { } payment)
            return (payment.AmountFromPs?.ToString("G"), payment.Currency);
        return (null, null);
    }

    private static (string? cardSuffix, string? cardBrand, long? paymentAccountId) ExtractCardInfo(XsollaWebhookPayload payload)
    {
        var suffix = payload.CardSuffix;
        var brand = payload.CardBrand;
        long? accountId = payload.PaymentAccount?.Id;

        // Fallback: parse from payment_account.name (e.g. "411111******1111")
        if (suffix is null && payload.PaymentAccount?.Name is { } name)
        {
            var digits = name.Replace("*", "").Replace(" ", "").Trim();
            suffix = digits.Length >= 4 ? digits[^4..] : digits.Length > 0 ? digits : null;
        }

        if (brand is null && payload.PaymentAccount?.PaymentSystem?.Name is { } psName)
            brand = psName;

        return (suffix, brand, accountId);
    }

    private async Task SaveTransaction(Guid userId, string txId, string transactionType,
        string? planExternalId = null, string? boostPackType = null,
        int? boostCount = null, Guid? recipientId = null,
        string? amount = null, string? currency = null,
        string? cardSuffix = null, string? cardBrand = null, long? paymentAccountId = null)
    {
        try
        {
            await client.GetGrain<IUltimaGrain>(userId)
               .SaveTransactionAsync(txId, transactionType, planExternalId, boostPackType,
                    boostCount, recipientId, amount, currency, cardSuffix, cardBrand, paymentAccountId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save transaction {TxId} for {UserId}", txId, userId);
        }
    }

    private static bool IsBoostPlan(string planId) =>
        planId.StartsWith("boost_pack_", StringComparison.Ordinal);

    private static (int count, BoostSource source, int durationDays) ParseBoostPlan(string planId) => planId switch
    {
        "boost_pack_3"        => (3, BoostSource.PurchasedPack3, 30),
        "boost_pack_3_annual" => (3, BoostSource.PurchasedPack3, 365),
        "boost_pack_5"        => (5, BoostSource.PurchasedPack5, 30),
        "boost_pack_5_annual" => (5, BoostSource.PurchasedPack5, 365),
        var p when p.Contains("annual") => (1, BoostSource.PurchasedPack1, 365),
        _                     => (1, BoostSource.PurchasedPack1, 30)
    };

    private static (UltimaTier tier, int days) ParsePlan(string? planId) => planId switch
    {
        "ultima_annual"  => (UltimaTier.Annual, 365),
        "ultima_monthly" => (UltimaTier.Monthly, 30),
        _                => (UltimaTier.Monthly, 30)
    };
}
