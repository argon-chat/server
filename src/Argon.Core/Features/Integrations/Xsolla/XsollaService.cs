namespace Argon.Core.Features.Integrations.Xsolla;

using System.Diagnostics;
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

        var payload = new XsollaTokenRequest
        {
            User = CreateUser(userId, email, countryCode),
            Settings = CreateSettings(),
            Purchase = new XsollaTokenPurchase
            {
                Subscription = new XsollaSubscriptionPurchase { PlanId = sku }
            },
            CustomParameters = new Dictionary<string, object>
            {
                ["type"]    = "subscription",
                ["user_id"] = userId.ToString()
            }
        };

        var sw = Stopwatch.StartNew();
        var response = await PostMerchantAsync($"/merchants/{Opts.MerchantId}/token", payload, ct);

        var sessionId = response?.GetProperty("token").GetString() ?? throw new InvalidOperationException("No token in Xsolla response");
        var checkoutUrl = BuildCheckoutUrl(sessionId);

        sw.Stop();
        XsollaInstruments.CheckoutsCreated.Add(1, new KeyValuePair<string, object?>("type", "subscription"));
        XsollaInstruments.ApiCallDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("endpoint", "create_subscription_token"), new KeyValuePair<string, object?>("status", "ok"));
        logger.LogInformation("Xsolla subscription checkout created: userId={UserId}, plan={Plan}, country={Country}, elapsed={ElapsedMs}ms",
            userId, sku, countryCode, sw.Elapsed.TotalMilliseconds);

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

        var customParameters = new Dictionary<string, object>
        {
            ["type"]        = "boost_pack",
            ["user_id"]     = userId.ToString(),
            ["pack_type"]   = pack.ToString(),
            ["boost_count"] = quantity
        };

        var sw = Stopwatch.StartNew();
        var response = await CreateCatalogPaymentTokenAsync(
            userId, email, countryCode,
            [new XsollaCatalogPurchaseItem { Sku = sku, Quantity = 1 }],
            customParameters, ct);

        sw.Stop();
        XsollaInstruments.CheckoutsCreated.Add(1, new KeyValuePair<string, object?>("type", "boost_pack"));
        XsollaInstruments.ApiCallDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("endpoint", "create_boost_token"), new KeyValuePair<string, object?>("status", "ok"));
        logger.LogInformation("Xsolla boost checkout created: userId={UserId}, pack={Pack}, sku={Sku}, country={Country}, elapsed={ElapsedMs}ms",
            userId, pack, sku, countryCode, sw.Elapsed.TotalMilliseconds);

        return BuildCheckoutUrl(response.Token);
    }

    public async Task<string> CreateGiftCheckoutAsync(
        Guid senderId, string email, Guid recipientId, UltimaPlan plan, string? giftMessage, string countryCode, CancellationToken ct = default)
    {
        var sku = plan switch
        {
            UltimaPlan.Annual => "ultima_gift_annual",
            _                => "ultima_gift_monthly"
        };

        var customParameters = new Dictionary<string, object>
        {
            ["type"]         = "gift",
            ["user_id"]      = senderId.ToString(),
            ["recipient_id"] = recipientId.ToString(),
            ["plan"]         = plan == UltimaPlan.Annual ? "Annual" : "Monthly",
            ["gift_message"] = giftMessage ?? ""
        };

        var sw = Stopwatch.StartNew();
        var response = await CreateCatalogPaymentTokenAsync(
            senderId, email, countryCode,
            [new XsollaCatalogPurchaseItem { Sku = sku, Quantity = 1 }],
            customParameters, ct);

        sw.Stop();
        XsollaInstruments.CheckoutsCreated.Add(1, new KeyValuePair<string, object?>("type", "gift"));
        XsollaInstruments.ApiCallDuration.Record(sw.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("endpoint", "create_gift_token"), new KeyValuePair<string, object?>("status", "ok"));
        logger.LogInformation("Xsolla gift checkout created: senderId={SenderId}, recipientId={RecipientId}, plan={Plan}, country={Country}, elapsed={ElapsedMs}ms",
            senderId, recipientId, plan, countryCode, sw.Elapsed.TotalMilliseconds);

        return BuildCheckoutUrl(response.Token);
    }

    public async Task<bool> CancelSubscriptionAsync(string xsollaSubscriptionId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{MerchantApiBase}/projects/{Opts.ProjectId}/subscriptions/{xsollaSubscriptionId}";
            var request = new HttpRequestMessage(HttpMethod.Put, url)
            {
                Content = JsonContent.Create(new { status = "non_renewing", cancel_subscription_payment = true })
            };
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
                ExtractPrice(items, "boost_pack_1"),
                ExtractPrice(items, "boost_pack_3"),
                ExtractPrice(items, "boost_pack_5"),
                ExtractPrice(items, "boost_pack_1_annual"),
                ExtractPrice(items, "boost_pack_3_annual"),
                ExtractPrice(items, "boost_pack_5_annual")
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
            var url = $"{MerchantApiBase}/projects/{Opts.ProjectId}/subscriptions/plans?limit=100";
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = MerchantAuth();

            var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Xsolla Subscriptions API GET plans returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                return prices;
            }

            var plans = await response.Content.ReadFromJsonAsync<XsollaSubscriptionPlanResponse[]>(ct)
                     ?? [];

            foreach (var plan in plans)
            {
                var planId = plan.ExternalId is { Length: > 0 } ? plan.ExternalId : plan.Id.ToString();
                if (plan.Charge is not { Amount: not null } charge) continue;

                prices[planId] = new ProductPrice(
                    charge.Amount.Value.ToString("G"),
                    null,
                    charge.Currency ?? "USD");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch Xsolla subscription plans for country {Country}", country);
        }
        return prices;
    }

    public async Task<PaymentAccountInfo?> GetPaymentAccountAsync(
        Guid userId, CancellationToken ct = default)
    {
        try
        {
            var url = $"{MerchantApiBase}/projects/{Opts.ProjectId}/users/{userId}/payment_accounts";

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = MerchantAuth();

            var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning("Xsolla payment_accounts GET returned {StatusCode}: {Body}", response.StatusCode, errorBody);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var json = JsonSerializer.Deserialize<JsonElement>(body);

            if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() == 0)
                return null;

            // Take the most recently created account (last in array)
            var account = json[json.GetArrayLength() - 1];

            long? paymentAccountId = null;
            if (account.TryGetProperty("id", out var idProp) && idProp.TryGetInt64(out var accId))
                paymentAccountId = accId;

            // Parse name field: "411111******1111" → last 4 digits
            string? cardLastFour = null;
            if (account.TryGetProperty("name", out var nameProp) && nameProp.GetString() is { } name)
            {
                var digits = name.Replace("*", "").Replace(" ", "").Trim();
                cardLastFour = digits.Length >= 4 ? digits[^4..] : digits.Length > 0 ? digits : null;
            }

            string? cardType = null;
            if (account.TryGetProperty("payment_system", out var ps) && ps.ValueKind == JsonValueKind.Object)
                cardType = ps.TryGetProperty("name", out var psName) ? psName.GetString() : null;

            return new PaymentAccountInfo(cardLastFour, cardType, null, null, paymentAccountId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get payment accounts for user {UserId}", userId);
            return null;
        }
    }

    // ── Catalog API: Server-side payment ────────────────────────────────

    /// <summary>
    /// Creates a payment token for virtual item purchase (server-side).
    /// POST /api/v3/project/{project_id}/admin/payment/token
    /// </summary>
    public async Task<XsollaCatalogPaymentTokenResponse> CreateCatalogPaymentTokenAsync(
        Guid userId, string email, string countryCode, List<XsollaCatalogPurchaseItem> items,
        Dictionary<string, object>? customParameters = null, CancellationToken ct = default)
    {
        var payload = new XsollaCatalogPaymentTokenRequest
        {
            User = new XsollaCatalogUser
            {
                Id      = new XsollaStringValue { Value = userId.ToString() },
                Email   = new XsollaStringValue { Value = email },
                Country = new XsollaCatalogCountry { Value = countryCode, AllowModify = false }
            },
            Purchase = new XsollaCatalogPurchase { Items = items },
            Sandbox  = Opts.IsSandbox ? true : null,
            Settings = new XsollaCatalogPaymentSettings
            {
                Ui = new XsollaCatalogUiSettings { Theme = "63295aab2e47fab76f7708e3" },
                RedirectPolicy = new XsollaCatalogRedirectPolicy
                {
                    RedirectConditions        = "none",
                    StatusForManualRedirection = "none"
                }
            },
            CustomParameters = customParameters
        };

        var json = await PostStoreAdminAsync($"/v3/project/{Opts.ProjectId}/admin/payment/token", payload, ct);
        return JsonSerializer.Deserialize<XsollaCatalogPaymentTokenResponse>(json.GetRawText())
               ?? throw new InvalidOperationException("Failed to deserialize payment token response");
    }

    /// <summary>
    /// Gets order status.
    /// GET /api/v2/project/{project_id}/order/{order_id}
    /// </summary>
    public async Task<XsollaOrderStatusResponse?> GetOrderAsync(long orderId, string? userJwt = null, CancellationToken ct = default)
    {
        var json = await GetStoreAsync($"/v2/project/{Opts.ProjectId}/order/{orderId}", userJwt, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaOrderStatusResponse>(json.Value.GetRawText());
    }

    // ── Catalog API: Virtual Items (client-side read) ───────────────────

    /// <summary>
    /// Gets virtual items list for catalog.
    /// GET /api/v2/project/{project_id}/items/virtual_items
    /// </summary>
    public async Task<XsollaVirtualItemsListResponse?> GetVirtualItemsAsync(
        string? locale = null, string? country = null, int limit = 50, int offset = 0,
        string? userJwt = null, CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/items/virtual_items?limit={limit}&offset={offset}";
        if (locale is not null) url += $"&locale={locale}";
        if (country is not null) url += $"&country={country}";

        var json = await GetStoreAsync(url, userJwt, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaVirtualItemsListResponse>(json.Value.GetRawText());
    }

    /// <summary>
    /// Gets virtual items by group.
    /// GET /api/v2/project/{project_id}/items/virtual_items/group/{external_id}
    /// </summary>
    public async Task<XsollaVirtualItemsListResponse?> GetVirtualItemsByGroupAsync(
        string groupExternalId, string? locale = null, string? country = null,
        int limit = 50, int offset = 0, string? userJwt = null, CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/items/virtual_items/group/{groupExternalId}?limit={limit}&offset={offset}";
        if (locale is not null) url += $"&locale={locale}";
        if (country is not null) url += $"&country={country}";

        var json = await GetStoreAsync(url, userJwt, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaVirtualItemsListResponse>(json.Value.GetRawText());
    }

    /// <summary>
    /// Gets a virtual item by SKU.
    /// GET /api/v2/project/{project_id}/items/virtual_items/sku/{item_sku}
    /// </summary>
    public async Task<XsollaCatalogVirtualItem?> GetVirtualItemBySkuAsync(
        string sku, string? locale = null, string? country = null,
        string? userJwt = null, CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/items/virtual_items/sku/{sku}";
        var separator = '?';
        if (locale is not null) { url += $"{separator}locale={locale}"; separator = '&'; }
        if (country is not null) { url += $"{separator}country={country}"; }

        var json = await GetStoreAsync(url, userJwt, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaCatalogVirtualItem>(json.Value.GetRawText());
    }

    /// <summary>
    /// Gets item groups for catalog.
    /// GET /api/v2/project/{project_id}/items/groups
    /// </summary>
    public async Task<XsollaCatalogItemGroupsResponse?> GetItemGroupsAsync(
        string? locale = null, CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/items/groups";
        if (locale is not null) url += $"?locale={locale}";

        var json = await GetStoreAsync(url, null, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaCatalogItemGroupsResponse>(json.Value.GetRawText());
    }

    // ── Admin API: Virtual Items CRUD ───────────────────────────────────

    /// <summary>
    /// Gets admin virtual items list.
    /// GET /api/v2/project/{project_id}/admin/items/virtual_items
    /// </summary>
    public async Task<XsollaAdminVirtualItemsListResponse?> AdminGetVirtualItemsAsync(
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/admin/items/virtual_items?limit={limit}&offset={offset}";
        var json = await GetStoreAdminAsync(url, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaAdminVirtualItemsListResponse>(json.Value.GetRawText());
    }

    /// <summary>
    /// Gets a single admin virtual item by SKU.
    /// GET /api/v2/project/{project_id}/admin/items/virtual_items/sku/{item_sku}
    /// </summary>
    public async Task<XsollaAdminVirtualItemResponse?> AdminGetVirtualItemAsync(
        string sku, CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/admin/items/virtual_items/sku/{sku}";
        var json = await GetStoreAdminAsync(url, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaAdminVirtualItemResponse>(json.Value.GetRawText());
    }

    /// <summary>
    /// Creates a virtual item.
    /// POST /api/v2/project/{project_id}/admin/items/virtual_items
    /// </summary>
    public async Task AdminCreateVirtualItemAsync(
        XsollaAdminVirtualItemRequest item, CancellationToken ct = default)
    {
        await PostStoreAdminAsync($"/v2/project/{Opts.ProjectId}/admin/items/virtual_items", item, ct);
    }

    /// <summary>
    /// Updates a virtual item by SKU.
    /// PUT /api/v2/project/{project_id}/admin/items/virtual_items/sku/{item_sku}
    /// </summary>
    public async Task AdminUpdateVirtualItemAsync(
        string sku, XsollaAdminVirtualItemRequest item, CancellationToken ct = default)
    {
        await PutStoreAdminAsync($"/v2/project/{Opts.ProjectId}/admin/items/virtual_items/sku/{sku}", item, ct);
    }

    /// <summary>
    /// Deletes a virtual item by SKU.
    /// DELETE /api/v2/project/{project_id}/admin/items/virtual_items/sku/{item_sku}
    /// </summary>
    public async Task AdminDeleteVirtualItemAsync(string sku, CancellationToken ct = default)
    {
        await DeleteStoreAdminAsync($"/v2/project/{Opts.ProjectId}/admin/items/virtual_items/sku/{sku}", ct);
    }

    // ── Admin API: Item Groups CRUD ─────────────────────────────────────

    /// <summary>
    /// Gets admin item groups list.
    /// GET /api/v2/project/{project_id}/admin/items/groups
    /// </summary>
    public async Task<XsollaAdminItemGroupsListResponse?> AdminGetItemGroupsAsync(CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/admin/items/groups";
        var json = await GetStoreAdminAsync(url, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaAdminItemGroupsListResponse>(json.Value.GetRawText());
    }

    /// <summary>
    /// Gets an admin item group by external ID.
    /// GET /api/v2/project/{project_id}/admin/items/groups/{external_id}
    /// </summary>
    public async Task<XsollaAdminItemGroupDetailResponse?> AdminGetItemGroupAsync(
        string externalId, CancellationToken ct = default)
    {
        var url = $"/v2/project/{Opts.ProjectId}/admin/items/groups/{externalId}";
        var json = await GetStoreAdminAsync(url, ct);
        if (json is null) return null;
        return JsonSerializer.Deserialize<XsollaAdminItemGroupDetailResponse>(json.Value.GetRawText());
    }

    /// <summary>
    /// Creates an item group.
    /// POST /api/v2/project/{project_id}/admin/items/groups
    /// </summary>
    public async Task AdminCreateItemGroupAsync(
        XsollaAdminItemGroupRequest group, CancellationToken ct = default)
    {
        await PostStoreAdminAsync($"/v2/project/{Opts.ProjectId}/admin/items/groups", group, ct);
    }

    /// <summary>
    /// Updates an item group by external ID.
    /// PUT /api/v2/project/{project_id}/admin/items/groups/{external_id}
    /// </summary>
    public async Task AdminUpdateItemGroupAsync(
        string externalId, XsollaAdminItemGroupRequest group, CancellationToken ct = default)
    {
        await PutStoreAdminAsync($"/v2/project/{Opts.ProjectId}/admin/items/groups/{externalId}", group, ct);
    }

    /// <summary>
    /// Deletes an item group by external ID.
    /// DELETE /api/v2/project/{project_id}/admin/items/groups/{external_id}
    /// </summary>
    public async Task AdminDeleteItemGroupAsync(string externalId, CancellationToken ct = default)
    {
        await DeleteStoreAdminAsync($"/v2/project/{Opts.ProjectId}/admin/items/groups/{externalId}", ct);
    }

    // ── HTTP helpers ────────────────────────────────────────────────────

    /// <summary>Pay Station Merchant API — basic auth with merchant_id:api_key</summary>
    private async Task<JsonElement?> PostMerchantAsync(string path, object payload, CancellationToken ct)
    {
        var url = $"{MerchantApiBase}{path}";
        var payloadJson = JsonSerializer.Serialize(payload);

        logger.LogDebug("Xsolla Merchant API POST {Url}, payload: {Payload}", url, payloadJson);

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

    /// <summary>Store Admin API GET — basic auth with project_id:api_key</summary>
    private async Task<JsonElement?> GetStoreAdminAsync(string path, CancellationToken ct)
    {
        var url = $"{StoreApiBase}{path}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = ProjectAuth();

        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Xsolla Store Admin GET {Path} returned {StatusCode}: {Body}", path, response.StatusCode, errorBody);
            return null;
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<JsonElement>(json);
    }

    /// <summary>Store Admin API POST — basic auth with project_id:api_key</summary>
    private async Task<JsonElement> PostStoreAdminAsync(string path, object payload, CancellationToken ct)
    {
        var url = $"{StoreApiBase}{path}";
        var payloadJson = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = ProjectAuth();

        var response = await httpClient.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Xsolla Store Admin POST {Path} error {StatusCode}: {Body}",
                path, response.StatusCode, responseBody);
            throw new InvalidOperationException($"Xsolla Store Admin API error {response.StatusCode}: {responseBody}");
        }

        return JsonSerializer.Deserialize<JsonElement>(responseBody);
    }

    /// <summary>Store Admin API PUT — basic auth with project_id:api_key</summary>
    private async Task PutStoreAdminAsync(string path, object payload, CancellationToken ct)
    {
        var url = $"{StoreApiBase}{path}";
        var payloadJson = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = ProjectAuth();

        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Xsolla Store Admin PUT {Path} error {StatusCode}: {Body}",
                path, response.StatusCode, errorBody);
            throw new InvalidOperationException($"Xsolla Store Admin API error {response.StatusCode}: {errorBody}");
        }
    }

    /// <summary>Store Admin API DELETE — basic auth with project_id:api_key</summary>
    private async Task DeleteStoreAdminAsync(string path, CancellationToken ct)
    {
        var url = $"{StoreApiBase}{path}";
        var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = ProjectAuth();

        var response = await httpClient.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("Xsolla Store Admin DELETE {Path} error {StatusCode}: {Body}",
                path, response.StatusCode, errorBody);
            throw new InvalidOperationException($"Xsolla Store Admin API error {response.StatusCode}: {errorBody}");
        }
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

    // ── Token request helpers ───────────────────────────────────────────

    private XsollaTokenUser CreateUser(Guid userId, string email, string countryCode) => new()
    {
        Id      = new XsollaStringValue { Value = userId.ToString() },
        Email   = new XsollaStringValue { Value = email },
        Country = new XsollaCountryValue { Value = countryCode, AllowModify = false }
    };

    private XsollaTokenSettings CreateSettings() => new()
    {
        ProjectId     = Opts.ProjectId,
        Mode          = Opts.IsSandbox ? "sandbox" : null,
        PaymentMethod = Opts.PaymentMethodId,
        RedirectPolicy = new XsollaRedirectPolicy
        {
            ManualRedirectionAction = "postmessage",
            RedirectConditions      = "none",
        },
        Ui = new XsollaUiSettings
        {
            IsThreeDsIndependentWindows = true,
            IsPaymentMethodsListMode    = false,
            IsSearchFieldHidden         = false,
            Theme                       = "63295aab2e47fab76f7708e3"
        }
    };

    private string BuildCheckoutUrl(string token) => Opts.IsSandbox
        ? $"https://sandbox-secure.xsolla.com/paystation4/?token={token}"
        : $"https://secure.xsolla.com/paystation4/?token={token}";
}
