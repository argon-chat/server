namespace Argon.Core.Features.Integrations.Xsolla;

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Hybrid;

public class XsollaService(
    HttpClient httpClient,
    IOptions<XsollaOptions> options,
    HybridCache cache,
    ILogger<XsollaService> logger) : IXsollaService
{
    private XsollaOptions Opts => options.Value;

    private string BaseUrl => Opts.IsSandbox
        ? "https://sandbox-store.xsolla.com/api"
        : "https://store.xsolla.com/api";

    private static readonly HybridCacheEntryOptions PriceCacheOptions = new()
    {
        Expiration           = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(15),
        Flags                = HybridCacheEntryFlags.DisableCompression
    };

    public async Task<(string checkoutUrl, string sessionId)> CreateSubscriptionCheckoutAsync(
        Guid userId, string email, UltimaPlan plan, CancellationToken ct = default)
    {
        var sku = plan switch
        {
            UltimaPlan.Monthly => "ultima_monthly",
            UltimaPlan.Annual  => "ultima_annual",
            _                  => "ultima_monthly"
        };

        var payload = new
        {
            user = new
            {
                id    = new { value = userId.ToString() },
                email = new { value = email }
            },
            settings = new
            {
                project_id = Opts.ProjectId,
                currency   = "USD",
                sandbox    = Opts.IsSandbox
            },
            purchase = new
            {
                subscription = new { plan_id = sku }
            },
            custom_parameters = new
            {
                type    = "subscription",
                user_id = userId.ToString()
            }
        };

        var response = await PostXsollaAsync("/v2/merchant/merchants/{merchant_id}/token", payload, ct);

        var sessionId = response?.GetProperty("token").GetString() ?? throw new InvalidOperationException("No token in Xsolla response");
        var checkoutUrl = Opts.IsSandbox
            ? $"https://sandbox-secure.xsolla.com/paystation4/?token={sessionId}"
            : $"https://secure.xsolla.com/paystation4/?token={sessionId}";

        return (checkoutUrl, sessionId);
    }

    public async Task<string> CreateBoostPackCheckoutAsync(
        Guid userId, string email, BoostPackType pack, CancellationToken ct = default)
    {
        var (sku, quantity) = pack switch
        {
            BoostPackType.Pack1 => ("boost_pack_1", 1),
            BoostPackType.Pack3 => ("boost_pack_3", 3),
            BoostPackType.Pack5 => ("boost_pack_5", 5),
            _                   => ("boost_pack_1", 1)
        };

        var payload = new
        {
            user = new
            {
                id    = new { value = userId.ToString() },
                email = new { value = email }
            },
            settings = new
            {
                project_id = Opts.ProjectId,
                currency   = "USD",
                sandbox    = Opts.IsSandbox
            },
            purchase = new
            {
                items = new[]
                {
                    new { sku, quantity }
                }
            },
            custom_parameters = new
            {
                type       = "boost_pack",
                user_id    = userId.ToString(),
                pack_type  = pack.ToString(),
                boost_count = quantity
            }
        };

        var response = await PostXsollaAsync("/v2/merchant/merchants/{merchant_id}/token", payload, ct);

        var token = response?.GetProperty("token").GetString() ?? throw new InvalidOperationException("No token in Xsolla response");

        return Opts.IsSandbox
            ? $"https://sandbox-secure.xsolla.com/paystation4/?token={token}"
            : $"https://secure.xsolla.com/paystation4/?token={token}";
    }

    public async Task<string> CreateGiftCheckoutAsync(
        Guid senderId, string email, Guid recipientId, UltimaPlan plan, string? giftMessage, CancellationToken ct = default)
    {
        var sku = plan switch
        {
            UltimaPlan.Monthly => "ultima_gift_monthly",
            UltimaPlan.Annual  => "ultima_gift_annual",
            _                  => "ultima_gift_monthly"
        };

        var payload = new
        {
            user = new
            {
                id    = new { value = senderId.ToString() },
                email = new { value = email }
            },
            settings = new
            {
                project_id = Opts.ProjectId,
                currency   = "USD",
                sandbox    = Opts.IsSandbox
            },
            purchase = new
            {
                items = new[]
                {
                    new { sku, quantity = 1 }
                }
            },
            custom_parameters = new
            {
                type         = "gift",
                sender_id    = senderId.ToString(),
                recipient_id = recipientId.ToString(),
                plan         = plan.ToString(),
                gift_message = giftMessage ?? ""
            }
        };

        var response = await PostXsollaAsync("/v2/merchant/merchants/{merchant_id}/token", payload, ct);

        var token = response?.GetProperty("token").GetString() ?? throw new InvalidOperationException("No token in Xsolla response");

        return Opts.IsSandbox
            ? $"https://sandbox-secure.xsolla.com/paystation4/?token={token}"
            : $"https://secure.xsolla.com/paystation4/?token={token}";
    }

    public async Task<bool> CancelSubscriptionAsync(string xsollaSubscriptionId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{BaseUrl}/v2/merchant/merchants/{Opts.MerchantId}/subscriptions/{xsollaSubscriptionId}/cancel";
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Opts.MerchantId}:{Opts.ApiKey}")));

            var response = await httpClient.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel Xsolla subscription {SubscriptionId}", xsollaSubscriptionId);
            return false;
        }
    }

    public bool ValidateWebhookSignature(string body, string signature)
    {
        var expected = Convert.ToHexStringLower(
            SHA1.HashData(Encoding.UTF8.GetBytes(body + Opts.WebhookSecret)));
        return string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase);
    }

    public async Task UpdateUserAttributeAsync(Guid userId, string key, string value, CancellationToken ct = default)
    {
        // Xsolla Login API — update read-only user attribute
        // https://developers.xsolla.com/login-api/attributes/update-users-read-only-attr
        var url = Opts.IsSandbox
            ? $"https://sandbox-login.xsolla.com/api/attributes/users/{userId}/read_only"
            : $"https://login.xsolla.com/api/attributes/users/{userId}/read_only";

        var payload = new[]
        {
            new
            {
                key,
                permission = "public",
                value
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Opts.MerchantId}:{Opts.ApiKey}")));
        request.Headers.Add("X-PROJECT-ID", Opts.LoginProjectId.ToString());

        // Let Polly resilience handler retry on transient HTTP errors (5xx, timeouts)
        var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task EnsureSubscriberAttributeAsync(Guid userId, bool isSubscriber, CancellationToken ct = default)
    {
        try
        {
            await UpdateUserAttributeAsync(userId, "ultima_subscriber", isSubscriber ? "1" : "0", ct);
        }
        catch (Exception ex)
        {
            // Best-effort — don't block checkout if attribute sync fails
            logger.LogWarning(ex, "Failed to ensure Xsolla subscriber attribute for {UserId}, promotion may not apply", userId);
        }
    }

    public async Task<UltimaPricing> GetPricingAsync(Guid userId, string countryCode, CancellationToken ct = default)
    {
        var country = countryCode.ToUpperInvariant();
        var cacheKey = $"xsolla:pricing:{country}:{userId}";

        return await cache.GetOrCreateAsync(cacheKey, async cancel =>
        {
            var items = await FetchItemPricesAsync(userId, country, cancel);
            var plans = await FetchPlanPricesAsync(country, cancel);

            return new UltimaPricing(
                ExtractPrice(plans, "ultima_monthly"),
                ExtractPrice(plans, "ultima_annual"),
                ExtractPrice(items, "boost_pack_1"),
                ExtractPrice(items, "boost_pack_3"),
                ExtractPrice(items, "boost_pack_5")
            );
        }, PriceCacheOptions, cancellationToken: ct);
    }

    private static ProductPrice ExtractPrice(Dictionary<string, ProductPrice> prices, string sku)
        => prices.TryGetValue(sku, out var p) ? p : new ProductPrice("0", null, "USD");

    private async Task<Dictionary<string, ProductPrice>> FetchItemPricesAsync(
        Guid userId, string country, CancellationToken ct)
    {
        var prices = new Dictionary<string, ProductPrice>();
        try
        {
            // Pass user_id so Xsolla applies user-specific promotions (e.g. ultima_subscriber discount)
            var json = await GetXsollaAsync(
                $"/v2/project/{Opts.ProjectId}/items/virtual_items?country={country}&user_id={userId}", ct);

            if (json?.TryGetProperty("items", out var items) == true)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var sku = item.GetProperty("sku").GetString();
                    if (sku is null) continue;

                    if (item.TryGetProperty("price", out var price))
                    {
                        var amount                = price.GetProperty("amount").GetRawText().Trim('"');
                        var amountWithoutDiscount = price.TryGetProperty("amount_without_discount", out var awd)
                            ? awd.GetRawText().Trim('"')
                            : null;
                        var currency = price.GetProperty("currency").GetString() ?? "USD";

                        // If no discount applied, amount_without_discount equals amount — treat as null
                        if (amountWithoutDiscount == amount) amountWithoutDiscount = null;

                        prices[sku] = new ProductPrice(amount, amountWithoutDiscount, currency);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Xsolla virtual items for country {Country}", country);
        }
        return prices;
    }

    private async Task<Dictionary<string, ProductPrice>> FetchPlanPricesAsync(
        string country, CancellationToken ct)
    {
        var prices = new Dictionary<string, ProductPrice>();
        try
        {
            var json = await GetXsollaAsync(
                $"/v2/project/{Opts.ProjectId}/subscriptions/plans?country={country}", ct);

            if (json?.TryGetProperty("items", out var plans) == true)
            {
                foreach (var plan in plans.EnumerateArray())
                {
                    var planId = plan.TryGetProperty("external_id", out var eid)
                        ? eid.GetString()
                        : plan.GetProperty("plan_id").ToString();
                    if (planId is null) continue;

                    if (plan.TryGetProperty("charge", out var charge))
                    {
                        var amount   = charge.GetProperty("amount").GetRawText().Trim('"');
                        var currency = charge.GetProperty("currency").GetString() ?? "USD";
                        prices[planId] = new ProductPrice(amount, null, currency);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Xsolla subscription plans for country {Country}", country);
        }
        return prices;
    }

    private async Task<JsonElement?> GetXsollaAsync(string path, CancellationToken ct)
    {
        var url = $"{BaseUrl}{path}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Opts.MerchantId}:{Opts.ApiKey}")));

        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Xsolla GET {Path} returned {StatusCode}: {Body}", path, response.StatusCode, errorBody);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    private async Task<JsonElement?> PostXsollaAsync(string path, object payload, CancellationToken ct)
    {
        var url = $"{BaseUrl}{path}".Replace("{merchant_id}", Opts.MerchantId.ToString());

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Opts.MerchantId}:{Opts.ApiKey}")));

        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Xsolla API error {StatusCode}: {Body}", response.StatusCode, errorBody);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }
}
