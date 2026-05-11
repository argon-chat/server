namespace Argon.Features.Integrations.Klipy;

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public interface IKlipyService
{
    Task<(List<KlipyMediaItem> Items, bool HasNext)> GetTrendingAsync(int page, int perPage, Guid userId, string? locale = null, CancellationToken ct = default);
    Task<(List<KlipyMediaItem> Items, bool HasNext)> SearchAsync(string query, int page, int perPage, Guid userId, string? locale = null, CancellationToken ct = default);
    Task<List<KlipyCategory>> GetCategoriesAsync(CancellationToken ct = default);
    Task<byte[]> DownloadMediaAsync(string url, CancellationToken ct = default);
    string ComputeUserHmac(string gifId, Guid userId);
    bool ValidateUserHmac(string gifId, Guid userId, string hmac);
    string ComputeCachePath(string slug);
}

public class KlipyService(
    HttpClient httpClient,
    IOptions<KlipyOptions> options,
    HybridCache cache,
    ILogger<KlipyService> logger) : IKlipyService
{
    private KlipyOptions Opts => options.Value;

    private static readonly HybridCacheEntryOptions TrendingCacheOptions = new()
    {
        Expiration           = TimeSpan.FromHours(72),
        LocalCacheExpiration = TimeSpan.FromHours(1),
        Flags                = HybridCacheEntryFlags.DisableCompression
    };

    private static readonly HybridCacheEntryOptions SearchCacheOptions = new()
    {
        Expiration           = TimeSpan.FromHours(1),
        LocalCacheExpiration = TimeSpan.FromMinutes(15),
        Flags                = HybridCacheEntryFlags.DisableCompression
    };

    private static readonly HybridCacheEntryOptions CategoriesCacheOptions = new()
    {
        Expiration           = TimeSpan.FromHours(24),
        LocalCacheExpiration = TimeSpan.FromHours(1),
        Flags                = HybridCacheEntryFlags.DisableCompression
    };

    public async Task<(List<KlipyMediaItem> Items, bool HasNext)> GetTrendingAsync(
        int page, int perPage, Guid userId, string? locale = null, CancellationToken ct = default)
    {
        var loc = NormalizeLocale(locale);
        var key = $"klipy:trending:{loc}:{page}:{perPage}";
        var cid = ComputeCustomerId(userId);
        var result = await cache.GetOrCreateAsync(key,
            async token => await FetchTrendingAsync(page, perPage, cid, loc, token),
            TrendingCacheOptions, cancellationToken: ct);
        return result;
    }

    public async Task<(List<KlipyMediaItem> Items, bool HasNext)> SearchAsync(
        string query, int page, int perPage, Guid userId, string? locale = null, CancellationToken ct = default)
    {
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var loc = NormalizeLocale(locale);
        var key = $"klipy:search:{loc}:{normalizedQuery}:{page}:{perPage}";
        var cid = ComputeCustomerId(userId);
        var result = await cache.GetOrCreateAsync(key,
            async token => await FetchSearchAsync(normalizedQuery, page, perPage, cid, loc, token),
            SearchCacheOptions, cancellationToken: ct);
        return result;
    }

    public async Task<List<KlipyCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var result = await cache.GetOrCreateAsync("klipy:categories",
            async token => await FetchCategoriesAsync(token),
            CategoriesCacheOptions, cancellationToken: ct);
        return result;
    }

    public async Task<byte[]> DownloadMediaAsync(string url, CancellationToken ct = default)
    {
        using var response = await httpClient.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    public string ComputeUserHmac(string gifId, Guid userId)
    {
        var payload = $"{gifId}|{userId}";
        var keyBytes = Encoding.UTF8.GetBytes(Opts.HmacKey);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexStringLower(hash);
    }

    public bool ValidateUserHmac(string gifId, Guid userId, string hmac)
    {
        var expected = ComputeUserHmac(gifId, userId);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(hmac));
    }

    public string ComputeCachePath(string slug)
    {
        var payload = $"cdn|{slug}";
        var keyBytes = Encoding.UTF8.GetBytes(Opts.HmacKey);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return $"gifs/{Convert.ToHexStringLower(hash)}";
    }

    #region Private fetch methods

    private string ComputeCustomerId(Guid userId)
    {
        var payload = $"cid|{userId}";
        var keyBytes = Encoding.UTF8.GetBytes(Opts.HmacKey);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string NormalizeLocale(string? locale)
        => string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim().ToLowerInvariant()[..Math.Min(locale.Trim().Length, 5)];

    private string BuildQueryParams(string? customerId, string? locale)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(customerId))
            sb.Append($"&customer_id={customerId}");
        if (!string.IsNullOrEmpty(locale))
            sb.Append($"&locale={Uri.EscapeDataString(locale)}");
        return sb.ToString();
    }

    private async Task<(List<KlipyMediaItem> Items, bool HasNext)> FetchTrendingAsync(
        int page, int perPage, string customerId, string locale, CancellationToken ct)
    {
        var url = $"{Opts.BaseUrl}/gifs/trending?page={page}&per_page={perPage}{BuildQueryParams(customerId, locale)}";
        var response = await CallApiAsync<List<KlipyMediaItem>>(url, ct);
        return (response.Data ?? [], response.HasNext);
    }

    private async Task<(List<KlipyMediaItem> Items, bool HasNext)> FetchSearchAsync(
        string query, int page, int perPage, string customerId, string locale, CancellationToken ct)
    {
        var url = $"{Opts.BaseUrl}/gifs/search?q={Uri.EscapeDataString(query)}&page={page}&per_page={perPage}{BuildQueryParams(customerId, locale)}";
        var response = await CallApiAsync<List<KlipyMediaItem>>(url, ct);
        return (response.Data ?? [], response.HasNext);
    }

    private async Task<List<KlipyCategory>> FetchCategoriesAsync(CancellationToken ct)
    {
        var url = $"{Opts.BaseUrl}/gifs/categories";
        var response = await CallApiAsync<List<KlipyCategory>>(url, ct);
        return response.Data ?? [];
    }

    private async Task<KlipyResponse<T>> CallApiAsync<T>(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-API-Key", Opts.ApiKey);

        using var response = await httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<KlipyResponse<T>>(ct);
        return result ?? new KlipyResponse<T>();
    }

    #endregion
}
