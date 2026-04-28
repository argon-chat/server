namespace ArgonComplexTest;

using Argon.Core.Features.Integrations.Xsolla;
using ArgonContracts;
using System.Collections.Concurrent;

public class FakeXsollaService : IXsollaService
{
    public ConcurrentBag<(Guid UserId, string Key, string Value)> AttributeUpdates { get; } = [];
    public ConcurrentBag<string> CancelledSubscriptions { get; } = [];
    public bool ShouldValidateSignature { get; set; } = true;

    public Task<(string checkoutUrl, string sessionId)> CreateSubscriptionCheckoutAsync(
        Guid userId, string email, UltimaPlan plan, CancellationToken ct = default)
        => Task.FromResult(($"https://fake-checkout.test/sub?user={userId}&plan={plan}", Guid.NewGuid().ToString()));

    public Task<string> CreateBoostPackCheckoutAsync(
        Guid userId, string email, BoostPackType pack, CancellationToken ct = default)
        => Task.FromResult($"https://fake-checkout.test/boost?user={userId}&pack={pack}");

    public Task<string> CreateGiftCheckoutAsync(
        Guid senderId, string email, Guid recipientId, UltimaPlan plan, string? giftMessage, CancellationToken ct = default)
        => Task.FromResult($"https://fake-checkout.test/gift?sender={senderId}&recipient={recipientId}&plan={plan}");

    public Task<bool> CancelSubscriptionAsync(string xsollaSubscriptionId, CancellationToken ct = default)
    {
        CancelledSubscriptions.Add(xsollaSubscriptionId);
        return Task.FromResult(true);
    }

    public Task UpdateUserAttributeAsync(Guid userId, string key, string value, CancellationToken ct = default)
    {
        AttributeUpdates.Add((userId, key, value));
        return Task.CompletedTask;
    }

    public Task EnsureSubscriberAttributeAsync(Guid userId, bool isSubscriber, CancellationToken ct = default)
    {
        AttributeUpdates.Add((userId, "ultima_subscriber", isSubscriber ? "1" : "0"));
        return Task.CompletedTask;
    }

    public bool ValidateWebhookSignature(string body, string signature)
        => ShouldValidateSignature;

    public Task<UltimaPricing> GetPricingAsync(Guid userId, string countryCode, CancellationToken ct = default)
    {
        return Task.FromResult(new UltimaPricing(
            new ProductPrice("9.99", null, "USD"),
            new ProductPrice("99.99", null, "USD"),
            new ProductPrice("4.99", null, "USD"),
            new ProductPrice("12.99", null, "USD"),
            new ProductPrice("19.99", null, "USD")
        ));
    }

    public void Reset()
    {
        AttributeUpdates.Clear();
        CancelledSubscriptions.Clear();
        ShouldValidateSignature = true;
    }
}
