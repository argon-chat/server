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
            logger.LogWarning("Xsolla webhook: invalid signature. AuthHeader={AuthHeader}, BodyLength={BodyLength}, BodyPreview={BodyPreview}",
                rawAuth, body.Length, body.Length > 0 ? body[..Math.Min(100, body.Length)] : "<empty>");
            return Unauthorized();
        }

        JsonElement json;
        try
        {
            json = JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Xsolla webhook: failed to parse JSON body");
            return BadRequest();
        }

        var notificationType = json.GetProperty("notification_type").GetString();

        logger.LogInformation("Xsolla webhook received: {Type}", notificationType);

        try
        {
            switch (notificationType)
            {
                case "user_validation":
                    return await HandleUserValidation(json);

                case "payment":
                    await HandlePayment(json);
                    break;

                case "refund":
                    await HandleRefund(json);
                    break;

                case "cancel_subscription":
                    await HandleCancelSubscription(json);
                    break;

                default:
                    logger.LogInformation("Xsolla webhook: unhandled notification type {Type}", notificationType);
                    break;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Xsolla webhook processing failed for {Type}", notificationType);
            return StatusCode(500);
        }

        return NoContent();
    }

    private async Task<IActionResult> HandleUserValidation(JsonElement json)
    {
        var userIdStr = json.GetProperty("user").GetProperty("id").GetString();

        logger.LogInformation("Xsolla user_validation: userId={UserId}", userIdStr);

        if (!Guid.TryParse(userIdStr, out var userId))
        {
            logger.LogWarning("Xsolla user_validation: failed to parse userId '{UserId}' as Guid", userIdStr);
            return NotFound();
        }

        // Verify user exists by attempting to get the grain — no DB access outside grains
        try
        {
            var user = await client.GetGrain<IUserGrain>(userId).GetAsArgonUser();
            logger.LogInformation("Xsolla user_validation: user {UserId} found, returning 204", userId);
            return NoContent();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Xsolla user_validation: user {UserId} NOT found, returning 404", userId);
            return NotFound();
        }
    }

    private async Task HandlePayment(JsonElement json)
    {
        var customParams = json.GetProperty("custom_parameters");
        var type         = customParams.GetProperty("type").GetString();
        var userIdStr    = customParams.GetProperty("user_id").GetString();
        var txId         = json.GetProperty("transaction").GetProperty("id").GetInt64().ToString();

        if (!Guid.TryParse(userIdStr, out var userId))
        {
            logger.LogWarning("Xsolla payment: invalid user_id {UserId}", userIdStr);
            return;
        }

        // Extract amount from transaction if available
        string? amount = null;
        string? currency = null;
        if (json.TryGetProperty("purchase", out var purchase))
        {
            if (purchase.TryGetProperty("total", out var total) && total.ValueKind == JsonValueKind.Object)
            {
                amount   = total.TryGetProperty("amount", out var a) ? a.GetRawText().Trim('"') : null;
                currency = total.TryGetProperty("currency", out var c) ? c.GetString() : null;
            }

            if (amount is null && purchase.TryGetProperty("checkout", out var checkout) && checkout.ValueKind == JsonValueKind.Object)
            {
                amount   = checkout.TryGetProperty("amount", out var a2) ? a2.GetRawText().Trim('"') : null;
                currency = checkout.TryGetProperty("currency", out var c2) ? c2.GetString() : null;
            }
        }

        switch (type)
        {
            case "subscription":
            {
                var planId = json.GetProperty("purchase").GetProperty("subscription").GetProperty("plan_id").GetString();
                var (tier, days) = ParsePlan(planId);
                var xsollaSubId = json.GetProperty("purchase").GetProperty("subscription").GetProperty("subscription_id").GetInt64().ToString();

                await client.GetGrain<IUltimaGrain>(userId)
                   .ActivateSubscriptionAsync(tier, days, xsollaSubId, null);

                await SaveTransactionAsync(userId, txId, "subscription", planExternalId: planId, amount: amount, currency: currency);

                logger.LogInformation("Xsolla: activated subscription for {UserId}, plan {Plan}, txId {TxId}", userId, planId, txId);
                break;
            }
            case "boost_pack":
            {
                var packTypeStr = customParams.GetProperty("pack_type").GetString();
                var boostCount  = customParams.GetProperty("boost_count").GetInt32();
                var source = packTypeStr switch
                {
                    "Pack3" => BoostSource.PurchasedPack3,
                    "Pack5" => BoostSource.PurchasedPack5,
                    _       => BoostSource.PurchasedPack1
                };

                await client.GetGrain<IUltimaGrain>(userId)
                   .GrantPurchasedBoostsAsync(boostCount, source, txId);

                await SaveTransactionAsync(userId, txId, "boost_pack", boostPackType: packTypeStr, boostCount: boostCount, amount: amount, currency: currency);

                logger.LogInformation("Xsolla: granted {Count} boosts to {UserId}, txId {TxId}", boostCount, userId, txId);
                break;
            }
            case "gift":
            {
                var recipientIdStr = customParams.GetProperty("recipient_id").GetString();
                var planStr        = customParams.GetProperty("plan").GetString();
                var giftMessage    = customParams.TryGetProperty("gift_message", out var gm) ? gm.GetString() : null;

                if (!Guid.TryParse(recipientIdStr, out var recipientId))
                {
                    logger.LogWarning("Xsolla gift: invalid recipient_id {RecipientId}", recipientIdStr);
                    return;
                }

                var (_, days) = ParsePlan(planStr switch
                {
                    "Monthly" => "ultima_monthly",
                    "Annual"  => "ultima_annual",
                    _         => "ultima_monthly"
                });

                var planId = planStr switch
                {
                    "Annual" => "ultima_annual",
                    _        => "ultima_monthly"
                };

                await client.GetGrain<IInventoryGrain>(Guid.NewGuid())
                   .GiveUltimaGiftAsync(recipientId, planId, days, userId, giftMessage);

                // Grant 3 gift-reward boosts to the sender
                await client.GetGrain<IUltimaGrain>(userId)
                   .GrantPurchasedBoostsAsync(3, BoostSource.GiftReward, txId);

                await SaveTransactionAsync(userId, txId, "gift", planExternalId: planId, recipientId: recipientId, amount: amount, currency: currency);

                logger.LogInformation("Xsolla: gift from {SenderId} to {RecipientId}, txId {TxId}", userId, recipientId, txId);
                break;
            }
            default:
                logger.LogInformation("Xsolla payment: unhandled purchase type {Type}", type);
                break;
        }
    }

    private async Task SaveTransactionAsync(
        Guid userId, string txId, string transactionType,
        string? planExternalId = null, string? boostPackType = null,
        int? boostCount = null, Guid? recipientId = null,
        string? amount = null, string? currency = null)
    {
        try
        {
            await client.GetGrain<IUltimaGrain>(userId)
               .SaveTransactionAsync(txId, transactionType, planExternalId, boostPackType, boostCount, recipientId, amount, currency);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to save payment transaction {TxId} for {UserId}", txId, userId);
        }
    }

    private async Task HandleRefund(JsonElement json)
    {
        var customParams = json.GetProperty("custom_parameters");
        var userIdStr    = customParams.GetProperty("user_id").GetString();

        if (!Guid.TryParse(userIdStr, out var userId))
            return;

        await client.GetGrain<IUltimaGrain>(userId).ExpireSubscriptionAsync();

        logger.LogInformation("Xsolla: refund processed for {UserId}", userId);
    }

    private async Task HandleCancelSubscription(JsonElement json)
    {
        var userIdStr = json.GetProperty("user").GetProperty("id").GetString();

        if (!Guid.TryParse(userIdStr, out var userId))
            return;

        await client.GetGrain<IUltimaGrain>(userId).CancelSubscriptionAsync();

        logger.LogInformation("Xsolla: subscription cancelled for {UserId}", userId);
    }

    private static (UltimaTier tier, int days) ParsePlan(string? planId) => planId switch
    {
        "ultima_annual"  => (UltimaTier.Annual, 365),
        "ultima_monthly" => (UltimaTier.Monthly, 30),
        _                => (UltimaTier.Monthly, 30)
    };
}
