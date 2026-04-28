namespace Argon.Core.Features.Integrations.Xsolla;

public interface IXsollaService
{
    Task<(string checkoutUrl, string sessionId)> CreateSubscriptionCheckoutAsync(Guid userId, string email, UltimaPlan plan, CancellationToken ct = default);
    Task<string> CreateBoostPackCheckoutAsync(Guid userId, string email, BoostPackType pack, CancellationToken ct = default);
    Task<string> CreateGiftCheckoutAsync(Guid senderId, string email, Guid recipientId, UltimaPlan plan, string? giftMessage, CancellationToken ct = default);
    Task<bool> CancelSubscriptionAsync(string xsollaSubscriptionId, CancellationToken ct = default);
    Task<PaymentAccountInfo?> GetPaymentAccountAsync(Guid userId, string xsollaSubscriptionId, CancellationToken ct = default);
    Task UpdateUserAttributeAsync(Guid userId, string key, string value, CancellationToken ct = default);
    Task EnsureSubscriberAttributeAsync(Guid userId, bool isSubscriber, CancellationToken ct = default);
    Task<UltimaPricing> GetPricingAsync(Guid userId, string countryCode, CancellationToken ct = default);
    bool ValidateWebhookSignature(string body, string signature);
}
