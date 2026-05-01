namespace Argon.Core.Features.WebHooks;

using System.Diagnostics;
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
        var sw = Stopwatch.StartNew();
        Request.EnableBuffering();
        using var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
        {
            logger.LogWarning("Xsolla webhook: no Authorization header, IP={RemoteIp}", HttpContext.Connection.RemoteIpAddress);
            XsollaInstruments.WebhookSignatureFailures.Add(1);
            XsollaInstruments.WebhooksReceived.Add(1, new KeyValuePair<string, object?>("type", "unknown"), new KeyValuePair<string, object?>("status", "unauthorized"));
            return Unauthorized();
        }

        var rawAuth = authHeader.ToString();
        var signature = rawAuth.Replace("Signature ", "");

        if (!xsolla.ValidateWebhookSignature(body, signature))
        {
            logger.LogWarning("Xsolla webhook: invalid signature, bodyLength={BodyLength}, IP={RemoteIp}",
                body.Length, HttpContext.Connection.RemoteIpAddress);
            XsollaInstruments.WebhookSignatureFailures.Add(1);
            XsollaInstruments.WebhooksReceived.Add(1, new KeyValuePair<string, object?>("type", "unknown"), new KeyValuePair<string, object?>("status", "invalid_signature"));
            return BadRequest(new { error = new { code = "INVALID_SIGNATURE", message = "Invalid signature" } });
        }

        XsollaWebhookPayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<XsollaWebhookPayload>(body, JsonOpts)!;
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Xsolla webhook: failed to parse JSON body, bodyLength={BodyLength}", body.Length);
            XsollaInstruments.WebhookErrors.Add(1, new KeyValuePair<string, object?>("type", "unknown"), new KeyValuePair<string, object?>("error", "malformed_json"));
            return BadRequest(new { error = new { code = "INVALID_PARAMETER", message = "Malformed JSON" } });
        }

        var notificationType = payload.NotificationType ?? "unknown";
        logger.LogInformation("Xsolla webhook received: type={Type}, txId={TxId}, userId={UserId}, dryRun={DryRun}",
            notificationType,
            payload.Transaction?.Id,
            payload.User?.Id ?? payload.User?.ExternalId,
            payload.Transaction?.DryRun);

        try
        {
            var result = payload.NotificationType switch
            {
                "user_validation"          => await HandleUserValidation(payload),
                "user_search"              => await HandleUserSearch(payload),
                "payment"                  => await HandlePayment(payload),
                "refund"                   => await HandleRefund(payload),
                "partial_refund"           => await HandlePartialRefund(payload),
                "ps_declined"              => HandleDeclinedPayment(payload),
                "afs_reject"               => HandleAfsReject(payload),
                "afs_black_list"           => HandleAfsBlocklist(payload),
                "order_paid"               => await HandleOrderPaid(payload),
                "order_canceled"           => await HandleOrderCanceled(payload),
                "create_subscription"      => await HandleCreateSubscription(payload),
                "update_subscription"      => await HandleUpdateSubscription(payload),
                "cancel_subscription"      => await HandleCancelSubscription(payload),
                "non_renewal_subscription" => await HandleNonRenewalSubscription(payload),
                "payment_account_add"      => HandlePaymentAccountAdd(payload),
                "payment_account_remove"   => HandlePaymentAccountRemove(payload),
                "partner_side_catalog"     => await HandlePartnerSideCatalog(payload),
                "dispute"                  => HandleDispute(payload),
                _ => LogAndAcceptUnknown(payload.NotificationType)
            };

            sw.Stop();
            XsollaInstruments.WebhooksReceived.Add(1,
                new KeyValuePair<string, object?>("type", notificationType),
                new KeyValuePair<string, object?>("status", "ok"));
            XsollaInstruments.WebhookDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type", notificationType));
            logger.LogInformation("Xsolla webhook processed: type={Type}, elapsed={ElapsedMs}ms",
                notificationType, sw.Elapsed.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            XsollaInstruments.WebhookErrors.Add(1,
                new KeyValuePair<string, object?>("type", notificationType),
                new KeyValuePair<string, object?>("error", ex.GetType().Name));
            XsollaInstruments.WebhooksReceived.Add(1,
                new KeyValuePair<string, object?>("type", notificationType),
                new KeyValuePair<string, object?>("status", "error"));
            XsollaInstruments.WebhookDuration.Record(sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("type", notificationType));
            logger.LogError(ex, "Xsolla webhook processing failed: type={Type}, elapsed={ElapsedMs}ms",
                notificationType, sw.Elapsed.TotalMilliseconds);
            return StatusCode(500);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Handlers
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task<IActionResult> HandleUserValidation(XsollaWebhookPayload payload)
    {
        var userIdStr = payload.User?.Id ?? payload.User?.ExternalId;
        if (!Guid.TryParse(userIdStr, out var userId))
        {
            logger.LogWarning("Xsolla user_validation: invalid userId '{UserId}'", userIdStr);
            return NotFound();
        }

        try
        {
            await client.GetGrain<IUserGrain>(userId).GetAsArgonUser();
            return NoContent();
        }
        catch
        {
            return NotFound();
        }
    }

    private async Task<IActionResult> HandleUserSearch(XsollaWebhookPayload payload)
    {
        var publicId = payload.User?.PublicId;
        var userId = payload.User?.Id;

        var idStr = userId ?? publicId;
        if (!Guid.TryParse(idStr, out var uid))
        {
            logger.LogWarning("Xsolla user_search: cannot resolve user from '{Id}'", idStr);
            return NotFound();
        }

        try
        {
            var user = await client.GetGrain<IUserGrain>(uid).GetAsArgonUser();
            return Ok(new
            {
                user = new
                {
                    id = uid.ToString(),
                    name = user.username
                }
            });
        }
        catch
        {
            return NotFound();
        }
    }

    private async Task<IActionResult> HandlePayment(XsollaWebhookPayload payload)
    {
        var txId = payload.Transaction?.Id.ToString();
        if (txId is null)
        {
            logger.LogWarning("Xsolla payment: no transaction ID");
            return NoContent();
        }

        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        var isDryRun = payload.Transaction?.DryRun == 1;
        if (isDryRun)
        {
            logger.LogInformation("Xsolla payment: dry_run transaction {TxId}, skipping", txId);
            return NoContent();
        }

        var amount = payload.PaymentDetails?.Payment?.Amount?.ToString("G");
        var currency = payload.PaymentDetails?.Payment?.Currency;

        logger.LogInformation("Xsolla payment: processing txId={TxId}, userId={UserId}, amount={Amount} {Currency}",
            txId, userId, amount, currency);

        // Route by subscription plan
        if (payload.Purchase?.Subscription is { PlanId: not null } sub)
        {
            await HandleSubscriptionPayment(userId, txId, sub, amount, currency);
            RecordPaymentMetrics("subscription", amount, currency);
            return NoContent();
        }

        // Route by custom_parameters
        var customType = XsollaCustomParametersHelper.GetString(payload.CustomParameters, "type");
        switch (customType)
        {
            case "boost_pack":
                await HandleBoostPackPayment(payload, userId, txId, amount, currency);
                RecordPaymentMetrics("boost_pack", amount, currency);
                break;
            case "gift":
                await HandleGiftPayment(payload, userId, txId, amount, currency);
                RecordPaymentMetrics("gift", amount, currency);
                break;
            default:
                logger.LogWarning("Xsolla payment: no routing match for txId={TxId}, userId={UserId}, type={Type}, amount={Amount} {Currency}",
                    txId, userId, customType, amount, currency);
                break;
        }

        return NoContent();
    }

    private async Task<IActionResult> HandleRefund(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        var txId = payload.Transaction?.Id.ToString();
        var refundCode = payload.RefundDetails?.Code;

        if (txId is not null)
        {
            try
            {
                await client.GetGrain<IUltimaGrain>(userId).MarkTransactionRefundedAsync(txId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to mark transaction {TxId} as refunded for userId={UserId}", txId, userId);
            }
        }

        await client.GetGrain<IUltimaGrain>(userId).ExpireSubscriptionAsync();
        XsollaInstruments.RefundsProcessed.Add(1, new KeyValuePair<string, object?>("type", "refund"));
        logger.LogWarning("Xsolla refund processed: userId={UserId}, txId={TxId}, code={Code}, reason={Reason}",
            userId, txId, refundCode, payload.RefundDetails?.Reason);

        return NoContent();
    }

    private async Task<IActionResult> HandlePartialRefund(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        var txId = payload.Transaction?.Id.ToString();
        var refundAmount = payload.Purchase?.Total?.Amount;

        logger.LogWarning("Xsolla partial_refund: userId={UserId}, txId={TxId}, amount={Amount}, reason={Reason}, code={Code}",
            userId, txId, refundAmount, payload.RefundDetails?.Reason, payload.RefundDetails?.Code);

        if (txId is not null)
        {
            try
            {
                await client.GetGrain<IUltimaGrain>(userId).MarkTransactionRefundedAsync(txId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to mark partial refund transaction {TxId} for userId={UserId}", txId, userId);
            }
        }

        XsollaInstruments.RefundsProcessed.Add(1, new KeyValuePair<string, object?>("type", "partial_refund"));
        return NoContent();
    }

    private IActionResult HandleDeclinedPayment(XsollaWebhookPayload payload)
    {
        var userId = payload.User?.Id;
        var txId = payload.Transaction?.Id.ToString();
        var reason = payload.RefundDetails?.Reason;

        logger.LogWarning("Xsolla ps_declined: userId={UserId}, txId={TxId}, reason={Reason}",
            userId, txId, reason);

        return NoContent();
    }

    private IActionResult HandleAfsReject(XsollaWebhookPayload payload)
    {
        var userId = payload.User?.Id;
        var txId = payload.Transaction?.Id.ToString();
        var code = payload.RefundDetails?.Code;

        logger.LogWarning("Xsolla afs_reject: userId={UserId}, txId={TxId}, code={Code}, reason={Reason}",
            userId, txId, code, payload.RefundDetails?.Reason);

        return NoContent();
    }

    private IActionResult HandleAfsBlocklist(XsollaWebhookPayload payload)
    {
        var evt = payload.Event;
        logger.LogWarning("Xsolla afs_black_list: action={Action}, reason={Reason}, param={Param}, value={Value}, txId={TxId}",
            evt?.Action, evt?.Reason, evt?.Parameter, evt?.ParameterValue, evt?.TransactionId);

        return NoContent();
    }

    private async Task<IActionResult> HandleOrderPaid(XsollaWebhookPayload payload)
    {
        // Combined webhook: items[] + billing (payment details)
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        var txId = payload.Billing?.Transaction?.Id.ToString()
                ?? payload.Order?.InvoiceId;
        var isDryRun = payload.Billing?.Transaction?.DryRun == 1;

        if (isDryRun)
        {
            logger.LogInformation("Xsolla order_paid: dry_run, skipping. orderId={OrderId}", payload.Order?.Id);
            return NoContent();
        }

        var amount = payload.Billing?.PaymentDetails?.Payment?.Amount?.ToString("G");
        var currency = payload.Billing?.PaymentDetails?.Payment?.Currency;

        // If billing.purchase.subscription exists, handle as subscription payment
        if (payload.Billing?.Purchase?.Subscription is { PlanId: not null } sub)
        {
            await HandleSubscriptionPayment(userId, txId ?? "0", sub, amount, currency);
            RecordPaymentMetrics("subscription", amount, currency);
            return NoContent();
        }

        // Route by custom_parameters
        var customType = XsollaCustomParametersHelper.GetString(payload.CustomParameters, "type");
        switch (customType)
        {
            case "boost_pack":
                await HandleBoostPackPayment(payload, userId, txId ?? "0", amount, currency);
                RecordPaymentMetrics("boost_pack", amount, currency);
                return NoContent();
            case "gift":
                await HandleGiftPayment(payload, userId, txId ?? "0", amount, currency);
                RecordPaymentMetrics("gift", amount, currency);
                return NoContent();
        }

        // Route by items SKU patterns
        if (payload.Items is { Length: > 0 } items)
        {
            foreach (var item in items)
            {
                if (item.Sku is not null && item.Sku.StartsWith("boost_pack_", StringComparison.Ordinal))
                {
                    var (count, source, durationDays) = ParseBoostPlan(item.Sku);
                    var totalBoosts = count * item.Quantity;
                    await client.GetGrain<IUltimaGrain>(userId)
                       .GrantPurchasedBoostsAsync(totalBoosts, source, txId, durationDays);
                    await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
                       .GiveBoostItemsAsync(userId, totalBoosts, durationDays);
                    await SaveTransaction(userId, txId ?? "0", "boost_pack",
                        boostPackType: item.Sku, boostCount: totalBoosts,
                        amount: amount, currency: currency);
                    XsollaInstruments.BoostsGranted.Add(totalBoosts,
                        new KeyValuePair<string, object?>("plan", item.Sku),
                        new KeyValuePair<string, object?>("count", totalBoosts));
                    logger.LogInformation("Xsolla order_paid boost: userId={UserId}, sku={Sku}, count={Count}, duration={Days}d",
                        userId, item.Sku, totalBoosts, durationDays);
                }
                else if (item.Sku is "ultima_monthly" or "ultima_annual")
                {
                    var (tier, days) = ParsePlan(item.Sku);
                    await client.GetGrain<IUltimaGrain>(userId)
                       .ActivateSubscriptionAsync(tier, days, null, null);
                    await SaveTransaction(userId, txId ?? "0", "subscription",
                        planExternalId: item.Sku, amount: amount, currency: currency);
                    XsollaInstruments.SubscriptionsCreated.Add(1, new KeyValuePair<string, object?>("plan", item.Sku));
                    logger.LogInformation("Xsolla order_paid subscription: userId={UserId}, sku={Sku}, tier={Tier}, days={Days}",
                        userId, item.Sku, tier, days);
                }
            }
            RecordPaymentMetrics("order_items", amount, currency);
        }

        logger.LogInformation("Xsolla order_paid: userId={UserId}, orderId={OrderId}, txId={TxId}, items={ItemCount}, amount={Amount} {Currency}",
            userId, payload.Order?.Id, txId, payload.Items?.Length ?? 0, amount, currency);

        return NoContent();
    }

    private async Task<IActionResult> HandleOrderCanceled(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        var txId = payload.Billing?.Transaction?.Id.ToString();
        var refundCode = payload.Billing?.RefundDetails?.Code;

        if (txId is not null)
        {
            try
            {
                await client.GetGrain<IUltimaGrain>(userId).MarkTransactionRefundedAsync(txId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to mark order_canceled transaction {TxId}", txId);
            }
        }

        await client.GetGrain<IUltimaGrain>(userId).ExpireSubscriptionAsync();
        XsollaInstruments.RefundsProcessed.Add(1, new KeyValuePair<string, object?>("type", "order_canceled"));

        logger.LogWarning("Xsolla order_canceled: userId={UserId}, orderId={OrderId}, txId={TxId}, refundCode={Code}",
            userId, payload.Order?.Id, txId, refundCode);

        return NoContent();
    }

    private async Task<IActionResult> HandleCreateSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        var sub = payload.Subscription;
        if (sub?.PlanId is null) return NoContent();
        if (IsBoostPlan(sub.PlanId)) return NoContent();

        var (tier, days) = ParsePlan(sub.PlanId);
        await client.GetGrain<IUltimaGrain>(userId)
           .ActivateSubscriptionAsync(tier, days, sub.SubscriptionId, null);

        XsollaInstruments.SubscriptionsCreated.Add(1, new KeyValuePair<string, object?>("plan", sub.PlanId));
        logger.LogInformation("Xsolla create_subscription: userId={UserId}, plan={Plan}, tier={Tier}, days={Days}, subId={SubId}",
            userId, sub.PlanId, tier, days, sub.SubscriptionId);

        return NoContent();
    }

    private async Task<IActionResult> HandleUpdateSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        var sub = payload.Subscription;
        if (sub?.PlanId is null) return NoContent();
        if (IsBoostPlan(sub.PlanId)) return NoContent();

        var (tier, days) = ParsePlan(sub.PlanId);
        await client.GetGrain<IUltimaGrain>(userId)
           .ActivateSubscriptionAsync(tier, days, sub.SubscriptionId, null);

        logger.LogInformation("Xsolla update_subscription: userId={UserId}, plan={Plan}", userId, sub.PlanId);

        return NoContent();
    }

    private async Task<IActionResult> HandleCancelSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        await client.GetGrain<IUltimaGrain>(userId).CancelSubscriptionAsync();
        XsollaInstruments.SubscriptionsCanceled.Add(1, new KeyValuePair<string, object?>("plan", payload.Subscription?.PlanId ?? "unknown"));
        logger.LogWarning("Xsolla cancel_subscription: userId={UserId}, plan={Plan}, subId={SubId}",
            userId, payload.Subscription?.PlanId, payload.Subscription?.SubscriptionId);

        return NoContent();
    }

    private async Task<IActionResult> HandleNonRenewalSubscription(XsollaWebhookPayload payload)
    {
        var userId = ResolveUserId(payload);
        if (userId == Guid.Empty) return NoContent();

        await client.GetGrain<IUltimaGrain>(userId).CancelSubscriptionAsync();
        XsollaInstruments.SubscriptionsCanceled.Add(1, new KeyValuePair<string, object?>("plan", payload.Subscription?.PlanId ?? "unknown"));
        logger.LogWarning("Xsolla non_renewal_subscription: userId={UserId}, plan={Plan}, subId={SubId}",
            userId, payload.Subscription?.PlanId, payload.Subscription?.SubscriptionId);

        return NoContent();
    }

    private IActionResult HandlePaymentAccountAdd(XsollaWebhookPayload payload)
    {
        logger.LogInformation("Xsolla payment_account_add: userId={UserId}, accountId={AccId}, type={Type}",
            payload.User?.Id, payload.PaymentAccount?.Id, payload.PaymentAccount?.Type);
        return NoContent();
    }

    private IActionResult HandlePaymentAccountRemove(XsollaWebhookPayload payload)
    {
        logger.LogInformation("Xsolla payment_account_remove: userId={UserId}, accountId={AccId}, type={Type}",
            payload.User?.Id, payload.PaymentAccount?.Id, payload.PaymentAccount?.Type);
        return NoContent();
    }

    private async Task<IActionResult> HandlePartnerSideCatalog(XsollaWebhookPayload payload)
    {
        var userIdStr = payload.User?.UserId;
        if (!Guid.TryParse(userIdStr, out var userId))
        {
            logger.LogWarning("Xsolla partner_side_catalog: invalid user_id '{UserId}'", userIdStr);
            return NotFound();
        }

        // Return empty catalog (all items available) — customize as needed
        logger.LogInformation("Xsolla partner_side_catalog: userId={UserId}", userId);
        return Ok(Array.Empty<object>());
    }

    private IActionResult HandleDispute(XsollaWebhookPayload payload)
    {
        var dispute = payload.Dispute;
        var txId = payload.Transaction?.Id.ToString();
        var userId = payload.User?.Id;

        logger.LogWarning("Xsolla dispute: action={Action}, userId={UserId}, txId={TxId}, reason={Reason}, type={Type}, status={Status}",
            payload.Action, userId, txId, dispute?.Reason, dispute?.Type, dispute?.Status);

        return NoContent();
    }

    private IActionResult LogAndAcceptUnknown(string notificationType)
    {
        logger.LogInformation("Xsolla webhook: unhandled notification_type '{Type}', accepted", notificationType);
        return NoContent();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Business logic helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private async Task HandleSubscriptionPayment(Guid userId, string txId,
        XsollaPurchaseSubscription sub, string? amount, string? currency)
    {
        var planId = sub.PlanId!;
        var xsollaSubId = sub.SubscriptionId;

        if (IsBoostPlan(planId))
        {
            var (boostCount, source, durationDays) = ParseBoostPlan(planId);
            await client.GetGrain<IUltimaGrain>(userId)
               .GrantPurchasedBoostsAsync(boostCount, source, txId, durationDays);
            await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
               .GiveBoostItemsAsync(userId, boostCount, durationDays);
            await SaveTransaction(userId, txId, "boost_pack",
                boostPackType: planId, boostCount: boostCount,
                amount: amount, currency: currency);
            XsollaInstruments.BoostsGranted.Add(boostCount,
                new KeyValuePair<string, object?>("plan", planId),
                new KeyValuePair<string, object?>("count", boostCount));
            logger.LogInformation("Xsolla: granted {Count} boosts to userId={UserId}, plan={Plan}, txId={TxId}, duration={Days}d, amount={Amount} {Currency}",
                boostCount, userId, planId, txId, durationDays, amount, currency);
        }
        else
        {
            var (tier, days) = ParsePlan(planId);
            await client.GetGrain<IUltimaGrain>(userId)
               .ActivateSubscriptionAsync(tier, days, xsollaSubId, null);
            await SaveTransaction(userId, txId, "subscription",
                planExternalId: planId, amount: amount, currency: currency);
            XsollaInstruments.SubscriptionsCreated.Add(1, new KeyValuePair<string, object?>("plan", planId));
            logger.LogInformation("Xsolla: activated subscription for userId={UserId}, plan={Plan}, txId={TxId}, subId={SubId}, days={Days}, amount={Amount} {Currency}",
                userId, planId, txId, xsollaSubId, days, amount, currency);
        }
    }

    private async Task HandleBoostPackPayment(XsollaWebhookPayload payload, Guid userId, string txId,
        string? amount, string? currency)
    {
        var packType = XsollaCustomParametersHelper.GetString(payload.CustomParameters, "pack_type") ?? "Pack1";
        var boostCount = XsollaCustomParametersHelper.GetInt(payload.CustomParameters, "boost_count") ?? 1;

        var isAnnual = packType.Contains("Annual", StringComparison.OrdinalIgnoreCase);
        var durationDays = isAnnual ? 365 : 30;

        var planId = $"boost_pack_{boostCount}" + (isAnnual ? "_annual" : "");
        var (_, source, _) = ParseBoostPlan(planId);

        await client.GetGrain<IUltimaGrain>(userId)
           .GrantPurchasedBoostsAsync(boostCount, source, txId, durationDays);
        await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
           .GiveBoostItemsAsync(userId, boostCount, durationDays);
        await SaveTransaction(userId, txId, "boost_pack",
            boostPackType: planId, boostCount: boostCount,
            amount: amount, currency: currency);

        logger.LogInformation("Xsolla: granted {Count} boosts to {UserId} (pack {Pack})", boostCount, userId, packType);
    }

    private async Task HandleGiftPayment(XsollaWebhookPayload payload, Guid userId, string txId,
        string? amount, string? currency)
    {
        var recipientIdStr = XsollaCustomParametersHelper.GetString(payload.CustomParameters, "recipient_id");
        if (!Guid.TryParse(recipientIdStr, out var recipientId))
        {
            logger.LogWarning("Xsolla gift: invalid recipient_id '{RecipientId}'", recipientIdStr);
            return;
        }

        var plan = XsollaCustomParametersHelper.GetString(payload.CustomParameters, "plan");
        var giftMessage = XsollaCustomParametersHelper.GetString(payload.CustomParameters, "gift_message");

        var planId = plan switch
        {
            "Annual" => "ultima_annual",
            _        => "ultima_monthly"
        };
        var (_, days) = ParsePlan(planId);

        await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
           .GiveUltimaGiftAsync(recipientId, planId, days, userId, giftMessage);

        await client.GetGrain<IUltimaGrain>(userId)
           .GrantPurchasedBoostsAsync(2, BoostSource.GiftReward, txId, 30);
        await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
           .GiveBoostItemsAsync(userId, 2, 30);

        await SaveTransaction(userId, txId, "gift",
            planExternalId: planId, recipientId: recipientId,
            amount: amount, currency: currency);

        logger.LogInformation("Xsolla: gift from {SenderId} to {RecipientId}", userId, recipientId);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Utilities
    // ═══════════════════════════════════════════════════════════════════════════

    private Guid ResolveUserId(XsollaWebhookPayload payload)
    {
        // Priority: custom_parameters.user_id > user.external_id > user.id
        var str = XsollaCustomParametersHelper.GetString(payload.CustomParameters, "user_id")
               ?? payload.User?.ExternalId
               ?? payload.User?.Id;
        if (Guid.TryParse(str, out var id)) return id;
        logger.LogWarning("Xsolla webhook: cannot resolve user_id from payload");
        return Guid.Empty;
    }

    private async Task SaveTransaction(Guid userId, string txId, string transactionType,
        string? planExternalId = null, string? boostPackType = null,
        int? boostCount = null, Guid? recipientId = null,
        string? amount = null, string? currency = null)
    {
        try
        {
            await client.GetGrain<IUltimaGrain>(userId)
               .SaveTransactionAsync(txId, transactionType, planExternalId, boostPackType,
                    boostCount, recipientId, amount, currency);
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
        "boost_pack_3_annual" => (3, BoostSource.PurchasedPack3Annual, 365),
        "boost_pack_5"        => (5, BoostSource.PurchasedPack5, 30),
        "boost_pack_5_annual" => (5, BoostSource.PurchasedPack5Annual, 365),
        var p when p.Contains("annual") => (1, BoostSource.PurchasedPack1Annual, 365),
        _                     => (1, BoostSource.PurchasedPack1, 30)
    };

    private static (UltimaTier tier, int days) ParsePlan(string? planId) => planId switch
    {
        "ultima_annual"  => (UltimaTier.Annual, 365),
        "ultima_monthly" => (UltimaTier.Monthly, 30),
        _                => (UltimaTier.Monthly, 30)
    };

    private static void RecordPaymentMetrics(string type, string? amount, string? currency)
    {
        XsollaInstruments.PaymentsProcessed.Add(1, new KeyValuePair<string, object?>("type", type));
        if (decimal.TryParse(amount, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var decimalAmount) && decimalAmount > 0)
        {
            XsollaInstruments.PaymentRevenue.Add(decimalAmount,
                new KeyValuePair<string, object?>("currency", currency ?? "USD"),
                new KeyValuePair<string, object?>("type", type));
        }
    }
}
