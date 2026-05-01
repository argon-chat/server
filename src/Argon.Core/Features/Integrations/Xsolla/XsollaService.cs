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

    // Catalog API — item prices
    // https://developers.xsolla.com/api/catalog/
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
        Guid userId, string email, UltimaPlan plan, string countryCode, CancellationToken ct = default)
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
                id      = new { value = userId.ToString() },
                email   = new { value = email },
                country = new { value = countryCode, allow_modify = false }
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
        Guid userId, string email, BoostPackType pack, string countryCode, CancellationToken ct = default)
    {
        var (sku, quantity) = pack switch
        {
            BoostPackType.Pack1       => ("boost_pack_1", 1),
            BoostPackType.Pack3       => ("boost_pack_3", 3),
            BoostPackType.Pack5       => ("boost_pack_5", 5),
            BoostPackType.Pack1Annual => ("boost_pack_1_annual", 1),
            BoostPackType.Pack3Annual => ("boost_pack_3_annual", 3),
            BoostPackType.Pack5Annual => ("boost_pack_5_annual", 5),
            _                        => ("boost_pack_1", 1)
        };

        var payload = new
        {
            user = new
            {
                id      = new { value = userId.ToString() },
                email   = new { value = email },
                country = new { value = countryCode, allow_modify = false }
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
        Guid senderId, string email, Guid recipientId, UltimaPlan plan, string? giftMessage, string countryCode, CancellationToken ct = default)
    {
        var sku = plan switch
        {
            UltimaPlan.Annual => "ultima_gift_annual",
            _                => "ultima_gift_monthly"
        };

        var payload = new
        {
            user = new
            {
                id      = new { value = senderId.ToString() },
                email   = new { value = email },
                country = new { value = countryCode, allow_modify = false }
            },
            settings = new
            {
                project_id = Opts.ProjectId,
                mode       = Opts.IsSandbox ? "sandbox" : (string?)null
            },
            purchase = new
            {
                virtual_items = new
                {
                    items = new[] { new { sku, amount = 1 } }
                }
            },
            custom_parameters = new
            {
                type         = "gift",
                user_id      = senderId.ToString(),
                recipient_id = recipientId.ToString(),
                plan         = plan == UltimaPlan.Annual ? "Annual" : "Monthly",
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

        var isValid = string.Equals(expected, signature, StringComparison.OrdinalIgnoreCase);
        if (!isValid)
        {
            logger.LogWarning(
                "Webhook signature mismatch. Expected={Expected}, Got={Got}, SecretLength={SecretLen}, BodyLength={BodyLen}",
                expected, signature, Opts.WebhookSecret.Length, body.Length);
        }
        return isValid;
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

            logger.LogInformation(
                "Xsolla pricing fetched for {UserId}/{Country}: items={ItemCount} ({ItemSkus}), plans={PlanCount} ({PlanSkus})",
                userId, country,
                items.Count, string.Join(",", items.Keys),
                plans.Count, string.Join(",", plans.Keys));

            var pricing = new UltimaPricing(
                ExtractPrice(plans, "ultima_monthly"),
                ExtractPrice(plans, "ultima_annual"),
                ExtractPrice(plans, "boost_pack_1"),
                ExtractPrice(plans, "boost_pack_3"),
                ExtractPrice(plans, "boost_pack_5"),
                ExtractPrice(plans, "boost_pack_1_annual"),
                ExtractPrice(plans, "boost_pack_3_annual"),
                ExtractPrice(plans, "boost_pack_5_annual")
            );

            logger.LogInformation(
                "Xsolla pricing result: monthly={Monthly}, annual={Annual}, boost1={Boost1}, boost3={Boost3}, boost5={Boost5}",
                pricing.subscriptionMonthly.amount, pricing.subscriptionAnnual.amount,
                pricing.boostPack1.amount, pricing.boostPack3.amount, pricing.boostPack5.amount);

            return pricing;
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
            // Subscriptions API lives on MerchantApiBase, NOT StoreApiBase
            // https://developers.xsolla.com/api/subscriptions/plans
            var url = $"{MerchantApiBase}/projects/{Opts.ProjectId}/subscriptions/plans";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = MerchantAuth();

            var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Xsolla Subscriptions API GET plans returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                return prices;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var json = JsonSerializer.Deserialize<JsonElement>(body);

            // Subscriptions API may return array directly or { items: [...] }
            var plans = json.ValueKind == JsonValueKind.Array
                ? json
                : json.TryGetProperty("items", out var itemsProp) ? itemsProp : json;

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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Xsolla subscription plans for country {Country}", country);
        }
        return prices;
    }

    // ── Subscription Management (user-facing) API base ────────────────
    // https://developers.xsolla.com/api/subscriptions/subscription-management
    private const string SubscriptionManagementBase = "https://api.xsolla.com";

    public async Task<PaymentAccountInfo?> GetPaymentAccountAsync(
        Guid userId, string xsollaSubscriptionId, CancellationToken ct = default)
    {
        try
        {
            var userJwt = await GetOrCreateXsollaUserJwtAsync(userId, ct);
            var url = $"{SubscriptionManagementBase}/api/user/v1/management/projects/{Opts.ProjectId}/subscriptions/{xsollaSubscriptionId}/payment_account";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new("Bearer", userJwt);

            var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Xsolla payment_account GET returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var json = JsonSerializer.Deserialize<JsonElement>(body);

            // Parse name field: "** 7398" → "7398"
            string? cardLastFour = null;
            if (json.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { } name)
            {
                var digits = name.Replace("*", "").Replace(" ", "").Trim();
                cardLastFour = digits.Length > 0 ? digits : null;
            }

            string? cardType = json.TryGetProperty("ps_name", out var ps) ? ps.GetString() : null;
            string? expiryMonth = null;
            string? expiryYear = null;

            if (json.TryGetProperty("card_expiry_date", out var expiry) && expiry.ValueKind == JsonValueKind.Object)
            {
                expiryMonth = expiry.TryGetProperty("month", out var m) ? m.GetString() : null;
                expiryYear  = expiry.TryGetProperty("year", out var y) ? y.GetString() : null;
            }

            return new PaymentAccountInfo(cardLastFour, cardType, expiryMonth, expiryYear);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get payment account for user {UserId}, subscription {SubId}", userId, xsollaSubscriptionId);
            return null;
        }
    }

    // ── HTTP helpers ────────────────────────────────────────────────────

    /// <summary>Pay Station Merchant API — basic auth with merchant_id:api_key</summary>
    private async Task<JsonElement?> PostMerchantAsync(string path, object payload, CancellationToken ct)
    {
        var url = $"{MerchantApiBase}{path}";
        var payloadJson = JsonSerializer.Serialize(payload);

        logger.LogInformation("Xsolla Merchant API POST {Url}, payload: {Payload}", url, payloadJson);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = MerchantAuth();

        var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Xsolla Merchant API error {StatusCode} for {Url}: {Body}. Request payload: {Payload}",
                response.StatusCode, url, responseBody, payloadJson);
            throw new InvalidOperationException($"Xsolla Merchant API error {response.StatusCode}: {responseBody}");
        }

        logger.LogInformation("Xsolla Merchant API success for {Url}", url);
        return JsonSerializer.Deserialize<JsonElement>(responseBody);
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

        // Step 1: Create/auth user via server_custom_id (without attributes — auth_by_custom_id
        // stores them in the custom bucket, not the read-only bucket that promotions use)
        var body = new Dictionary<string, object>
        {
            ["server_custom_id"] = userId.ToString()
        };

        var url = $"{LoginApiBase}/users/login/server_custom_id?projectId={Opts.LoginProjectId}";

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("X-SERVER-AUTHORIZATION", serverJwt);

        var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        // If 401 — cached server JWT is likely stale; invalidate and retry once
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            logger.LogWarning("Xsolla auth_by_custom_id returned 401 — invalidating cached server JWT and retrying");
            await cache.RemoveAsync("xsolla:server_jwt", ct);

            serverJwt = await GetServerJwtAsync(ct);

            body = new Dictionary<string, object> { ["server_custom_id"] = userId.ToString() };
            request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            request.Headers.Add("X-SERVER-AUTHORIZATION", serverJwt);

            response = await httpClient.SendAsync(request, ct);
            responseBody = await response.Content.ReadAsStringAsync(ct);
        }

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Xsolla auth_by_custom_id failed {StatusCode}: {Body}", response.StatusCode, responseBody);
            throw new InvalidOperationException($"Failed to get Xsolla user JWT: {response.StatusCode}");
        }

        var json = JsonSerializer.Deserialize<JsonElement>(responseBody);
        var token = json.GetProperty("token").GetString()
               ?? throw new InvalidOperationException("No token in Xsolla auth_by_custom_id response");

        // Step 2: If attributes need updating, set them via update_read_only endpoint
        if (attributes is { Length: > 0 })
        {
            var xsollaSub = ExtractSubFromJwt(token);

            var attrBody = new
            {
                attributes = attributes.Select(a => new
                {
                    key        = a.key,
                    permission = "private",
                    value      = a.value
                }).ToArray(),
                publisher_id = Opts.MerchantId
            };

            var attrRequest = new HttpRequestMessage(HttpMethod.Post,
                $"{LoginApiBase}/attributes/users/{xsollaSub}/update_read_only")
            {
                Content = JsonContent.Create(attrBody)
            };
            attrRequest.Headers.Add("X-SERVER-AUTHORIZATION", serverJwt);

            var attrResponse = await httpClient.SendAsync(attrRequest, ct);
            if (!attrResponse.IsSuccessStatusCode)
            {
                var attrErr = await attrResponse.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Failed to update read-only attributes for {UserId}: {StatusCode} {Body}",
                    userId, attrResponse.StatusCode, attrErr);
            }
        }

        return token;
    }

    private static string ExtractSubFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
        var claims = JsonSerializer.Deserialize<JsonElement>(
            Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        return claims.GetProperty("sub").GetString()
               ?? throw new InvalidOperationException("No sub claim in Xsolla JWT");
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
