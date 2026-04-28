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

    // Pay Station API — token creation
    // https://developers.xsolla.com/api/pay-station/token/create-token
    private const string MerchantApiBase = "https://api.xsolla.com/merchant/v2";

    // Catalog API — item prices, subscription plans
    // https://developers.xsolla.com/api/igs-bb/
    private const string StoreApiBase = "https://store.xsolla.com/api";

    // Login API — user attributes, auth by custom ID
    // https://developers.xsolla.com/api/login/
    private const string LoginApiBase = "https://login.xsolla.com/api";

    private static readonly HybridCacheEntryOptions PriceCacheOptions = new()
    {
        Expiration           = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(15),
        Flags                = HybridCacheEntryFlags.DisableCompression
    };

    private static readonly HybridCacheEntryOptions ServerJwtCacheOptions = new()
    {
        Expiration           = TimeSpan.FromHours(6),
        LocalCacheExpiration = TimeSpan.FromHours(1),
    };

    private static readonly HybridCacheEntryOptions UserJwtCacheOptions = new()
    {
        Expiration           = TimeSpan.FromHours(12),
        LocalCacheExpiration = TimeSpan.FromHours(2),
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
                mode       = Opts.IsSandbox ? "sandbox" : (string?)null
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

        var response = await PostMerchantAsync($"/merchants/{Opts.MerchantId}/token", payload, ct);

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
                mode       = Opts.IsSandbox ? "sandbox" : (string?)null
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

        var response = await PostMerchantAsync($"/merchants/{Opts.MerchantId}/token", payload, ct);

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
                mode       = Opts.IsSandbox ? "sandbox" : (string?)null
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

        var response = await PostMerchantAsync($"/merchants/{Opts.MerchantId}/token", payload, ct);

        var token = response?.GetProperty("token").GetString() ?? throw new InvalidOperationException("No token in Xsolla response");

        return Opts.IsSandbox
            ? $"https://sandbox-secure.xsolla.com/paystation4/?token={token}"
            : $"https://secure.xsolla.com/paystation4/?token={token}";
    }

    public async Task<bool> CancelSubscriptionAsync(string xsollaSubscriptionId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{MerchantApiBase}/merchants/{Opts.MerchantId}/subscriptions/{xsollaSubscriptionId}/cancel";
            var request = new HttpRequestMessage(HttpMethod.Put, url);
            request.Headers.Authorization = MerchantAuth();

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
        // Use auth_by_custom_id to update attribute and refresh the user JWT cache in one call
        await GetOrCreateXsollaUserJwtAsync(userId, ct, new[] { (key, value) });
    }

    public async Task EnsureSubscriberAttributeAsync(Guid userId, bool isSubscriber, CancellationToken ct = default)
    {
        try
        {
            await GetOrCreateXsollaUserJwtAsync(userId, ct,
                new[] { ("ultima_subscriber", isSubscriber ? "1" : "0") });
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

        // User JWT carries ultima_subscriber attribute — Xsolla returns personalized prices
        // based on user segment configured in the dashboard
        var cacheKey = $"xsolla:pricing:{country}:{userId}";

        return await cache.GetOrCreateAsync(cacheKey, async cancel =>
        {
            string? userJwt = null;
            try
            {
                userJwt = await GetOrCreateXsollaUserJwtAsync(userId, cancel);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to get Xsolla user JWT for personalized pricing, falling back to base prices");
            }

            var items = await FetchItemPricesAsync(country, userJwt, cancel);
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
        string country, string? userJwt, CancellationToken ct)
    {
        var prices = new Dictionary<string, ProductPrice>();
        try
        {
            var url = $"/v2/project/{Opts.ProjectId}/items/virtual_items?country={country}";

            var json = await GetStoreAsync(url, userJwt, ct);

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
            var json = await GetStoreAsync(
                $"/v2/project/{Opts.ProjectId}/subscriptions/plans?country={country}", null, ct);

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

    // ── HTTP helpers ────────────────────────────────────────────────────

    /// <summary>Pay Station Merchant API — basic auth with merchant_id:api_key</summary>
    private async Task<JsonElement?> PostMerchantAsync(string path, object payload, CancellationToken ct)
    {
        var url = $"{MerchantApiBase}{path}";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = MerchantAuth();

        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Xsolla Merchant API error {StatusCode}: {Body}", response.StatusCode, errorBody);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>Catalog API — uses user JWT if available, otherwise project basic auth.</summary>
    private async Task<JsonElement?> GetStoreAsync(string path, string? userJwt, CancellationToken ct)
    {
        var url = $"{StoreApiBase}{path}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        if (userJwt is not null)
            request.Headers.Authorization = new("Bearer", userJwt);
        else
            request.Headers.Authorization = ProjectAuth();

        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Xsolla Store API GET {Path} returned {StatusCode}: {Body}", path, response.StatusCode, errorBody);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    // ── Xsolla Login: Server JWT & User JWT ─────────────────────────────

    /// <summary>
    /// Gets a cached server JWT via OAuth 2.0 client_credentials grant.
    /// https://developers.xsolla.com/api/login/operation/generate-jwt/
    /// </summary>
    private async Task<string> GetServerJwtAsync(CancellationToken ct)
    {
        return await cache.GetOrCreateAsync("xsolla:server_jwt", async cancel =>
        {
            var payload = new Dictionary<string, string>
            {
                ["grant_type"]    = "client_credentials",
                ["client_id"]     = Opts.ServerOAuthClientId.ToString(),
                ["client_secret"] = Opts.ServerOAuthClientSecret,
            };

            var request = new HttpRequestMessage(HttpMethod.Post, $"{LoginApiBase}/oauth2/token")
            {
                Content = new FormUrlEncodedContent(payload)
            };

            var response = await httpClient.SendAsync(request, cancel);
            var body = await response.Content.ReadAsStringAsync(cancel);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Xsolla server JWT request failed {StatusCode}: {Body}", response.StatusCode, body);
                throw new InvalidOperationException($"Failed to obtain Xsolla server JWT: {response.StatusCode}");
            }

            var json = JsonSerializer.Deserialize<JsonElement>(body);
            return json.GetProperty("access_token").GetString()
                   ?? throw new InvalidOperationException("No access_token in Xsolla OAuth response");
        }, ServerJwtCacheOptions, cancellationToken: ct);
    }

    /// <summary>
    /// Gets a cached Xsolla Login user JWT by authenticating via server custom ID.
    /// Creates the Xsolla user if it doesn't exist.
    /// Optionally updates user attributes in the same call.
    /// https://developers.xsolla.com/api/login/operation/auth-by-custom-id/
    /// </summary>
    private async Task<string> GetOrCreateXsollaUserJwtAsync(
        Guid userId, CancellationToken ct, (string key, string value)[]? attributes = null)
    {
        // If attributes are being updated, bypass cache to get fresh token with updated attrs
        if (attributes is { Length: > 0 })
        {
            var jwt = await FetchXsollaUserJwtAsync(userId, attributes, ct);
            // Update cache with fresh token
            await cache.SetAsync($"xsolla:user_jwt:{userId}", jwt, UserJwtCacheOptions, cancellationToken: ct);
            return jwt;
        }

        return await cache.GetOrCreateAsync($"xsolla:user_jwt:{userId}", async cancel =>
        {
            return await FetchXsollaUserJwtAsync(userId, null, cancel);
        }, UserJwtCacheOptions, cancellationToken: ct);
    }

    private async Task<string> FetchXsollaUserJwtAsync(
        Guid userId, (string key, string value)[]? attributes, CancellationToken ct)
    {
        var serverJwt = await GetServerJwtAsync(ct);

        var body = new Dictionary<string, object>
        {
            ["server_custom_id"] = userId.ToString()
        };

        if (attributes is { Length: > 0 })
        {
            body["attributes"] = attributes.Select(a => new
            {
                attr_type  = "server",
                key        = a.key,
                permission = "private",
                value      = a.value
            }).ToArray();
        }

        var url = $"{LoginApiBase}/users/login/server_custom_id?projectId={Opts.LoginProjectId}";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-SERVER-AUTHORIZATION", serverJwt);

        var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Xsolla auth_by_custom_id failed {StatusCode}: {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException($"Failed to get Xsolla user JWT: {response.StatusCode}");
        }

        var json = JsonSerializer.Deserialize<JsonElement>(responseBody);
        return json.GetProperty("token").GetString()
               ?? throw new InvalidOperationException("No token in Xsolla auth_by_custom_id response");
    }

    /// <summary>merchant_id:api_key — for Pay Station &amp; merchant endpoints</summary>
    private System.Net.Http.Headers.AuthenticationHeaderValue MerchantAuth()
        => new("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Opts.MerchantId}:{Opts.ApiKey}")));

    /// <summary>project_id:api_key — for Catalog/Store endpoints</summary>
    private System.Net.Http.Headers.AuthenticationHeaderValue ProjectAuth()
        => new("Basic", Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{Opts.ProjectId}:{Opts.ApiKey}")));
}
