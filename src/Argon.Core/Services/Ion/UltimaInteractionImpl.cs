namespace Argon.Services.Ion;

using Argon.Api.Grains.Interfaces;
using Argon.Core.Features.Integrations.Xsolla;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ion.runtime;

public class UltimaInteractionImpl(IXsollaService xsolla, ILogger<UltimaInteractionImpl> logger) : IUltimaInteraction
{
    // Pricing

    public async Task<UltimaPricing> GetPricing(CancellationToken ct = default)
    {
        var userId  = this.GetUserId();
        var country = this.GetUserCountry();

        return await xsolla.GetPricingAsync(userId, country, ct);
    }

    // Subscription

    public async Task<UltimaSubscriptionInfo?> GetMySubscription(CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        var grain = this.GetGrain<IUltimaGrain>(userId);
        var sub = await grain.GetSubscriptionAsync(ct);

        if (sub is null)
            return null;

        // Enrich with payment account (card info) from Xsolla
        PaymentAccountInfo? paymentAccount = null;
        try
        {
            var xsollaSubId = await grain.GetXsollaSubscriptionIdAsync(ct);
            if (xsollaSubId is not null)
                paymentAccount = await xsolla.GetPaymentAccountAsync(userId, xsollaSubId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch payment account for user {UserId}", userId);
        }

        return sub with { paymentAccount = paymentAccount };
    }

    public async Task<ICheckoutResult> CreateCheckoutSession(UltimaPlan plan, CancellationToken ct = default)
    {
        try
        {
            var userId = this.GetUserId();

            // Check if already subscribed
            var existing = await this.GetGrain<IUltimaGrain>(userId).GetSubscriptionAsync(ct);
            if (existing is { status: UltimaSubscriptionStatus.Active })
                return new FailedCheckout(CheckoutError.ALREADY_SUBSCRIBED);

            var user = await this.GetGrain<IUserGrain>(userId).GetMe();
            var (checkoutUrl, sessionId) = await xsolla.CreateSubscriptionCheckoutAsync(userId, user.Email, plan, ct);

            return new SuccessCheckout(checkoutUrl, sessionId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CreateCheckoutSession failed for plan {Plan}", plan);
            return new FailedCheckout(CheckoutError.PAYMENT_ERROR);
        }
    }

    public async Task<bool> CancelSubscription(CancellationToken ct = default)
        => await this.GetGrain<IUltimaGrain>(this.GetUserId()).CancelSubscriptionAsync(ct);

    // Transaction history

    public async Task<IonArray<UltimaTransaction>> GetTransactionHistory(CancellationToken ct = default)
    {
        var userId = this.GetUserId();
        return await this.GetGrain<IUltimaGrain>(userId).GetTransactionHistoryAsync(ct);
    }

    // Boosts

    public async Task<IonArray<UltimaBoost>> GetMyBoosts(CancellationToken ct = default)
        => await this.GetGrain<IUltimaGrain>(this.GetUserId()).GetBoostsAsync(ct);

    public async Task<IApplyBoostResult> ApplyBoost(Guid boostId, Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<IUltimaGrain>(this.GetUserId()).ApplyBoostAsync(boostId, spaceId, ct);

    public async Task<ITransferBoostResult> TransferBoost(Guid boostId, Guid newSpaceId, CancellationToken ct = default)
        => await this.GetGrain<IUltimaGrain>(this.GetUserId()).TransferBoostAsync(boostId, newSpaceId, ct);

    public async Task<bool> RemoveBoost(Guid boostId, CancellationToken ct = default)
        => await this.GetGrain<IUltimaGrain>(this.GetUserId()).RemoveBoostAsync(boostId, ct);

    public async Task<SpaceBoostStatus> GetSpaceBoostStatus(Guid spaceId, CancellationToken ct = default)
        => await this.GetGrain<ISpaceBoostGrain>(spaceId).GetBoostStatusAsync(ct);

    public async Task<IPurchaseBoostResult> PurchaseBoostPack(BoostPackType pack, CancellationToken ct = default)
    {
        try
        {
            var userId = this.GetUserId();
            var user = await this.GetGrain<IUserGrain>(userId).GetMe();

            // Ensure Xsolla subscriber attribute is in sync before checkout —
            // if previous sync failed, the promotion won't apply without this
            var sub = await this.GetGrain<IUltimaGrain>(userId).GetSubscriptionAsync(ct);
            var isSubscriber = sub is { status: UltimaSubscriptionStatus.Active };
            await xsolla.EnsureSubscriberAttributeAsync(userId, isSubscriber, ct);

            var checkoutUrl = await xsolla.CreateBoostPackCheckoutAsync(userId, user.Email, pack, ct);

            return new SuccessPurchaseBoost(checkoutUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "PurchaseBoostPack failed for {Pack}", pack);
            return new FailedPurchaseBoost(PurchaseBoostError.PAYMENT_ERROR);
        }
    }

    // Gifts

    public async Task<ISendGiftResult> SendUltimaGift(Guid recipientId, UltimaPlan plan, string? message, CancellationToken ct = default)
    {
        try
        {
            var senderId = this.GetUserId();

            if (senderId == recipientId)
                return new FailedSendGift(SendGiftError.SELF_GIFT);

            // Verify recipient exists
            try
            {
                await this.GetGrain<IUserGrain>(recipientId).GetAsArgonUser();
            }
            catch
            {
                return new FailedSendGift(SendGiftError.USER_NOT_FOUND);
            }

            var sender = await this.GetGrain<IUserGrain>(senderId).GetMe();
            var checkoutUrl = await xsolla.CreateGiftCheckoutAsync(senderId, sender.Email, recipientId, plan, message, ct);

            return new SuccessSendGift(checkoutUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SendUltimaGift failed for recipient {RecipientId}, plan {Plan}", recipientId, plan);
            return new FailedSendGift(SendGiftError.PAYMENT_ERROR);
        }
    }
}
