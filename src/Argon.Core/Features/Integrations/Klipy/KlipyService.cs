namespace Argon.Features.Integrations.Klipy;

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Argon.Features.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public interface IKlipyService
{
    Task<(List<KlipyMediaItem> Items, bool HasNext)> GetTrendingAsync(int page, int perPage, Guid userId, string? locale = null, CancellationToken ct = default);
    Task<(List<KlipyMediaItem> Items, bool HasNext)> SearchAsync(string query, int page, int perPage, Guid userId, string? locale = null, CancellationToken ct = default);
    Task<List<KlipyCategory>> GetCategoriesAsync(string? locale = null, CancellationToken ct = default);
    Task<KlipyMediaItem?> GetItemBySlugAsync(string slug, CancellationToken ct = default);
    Task<byte[]> DownloadMediaAsync(string url, CancellationToken ct = default);
    Task<(Guid FileId, int Width, int Height)?> EnsureCachedAsync(string slug, CancellationToken ct = default);
    string ComputeUserHmac(string gifId, Guid userId);
    bool ValidateUserHmac(string gifId, Guid userId, string hmac);
    string ComputeCachePath(string slug);
}

public class KlipyService(
    HttpClient httpClient,
    IOptions<KlipyOptions> options,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IS3StorageService s3,
    IReferenceCountService refCount,
    HybridCache cache,
    ILogger<KlipyService> logger) : IKlipyService
{
    private KlipyOptions Opts => options.Value;

    private string ApiBase => $"{Opts.BaseUrl}/api/v1/{Opts.ApiKey}";

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
        return await cache.GetOrCreateAsync(key,
            async token =>
            {
                var url = $"{ApiBase}/gifs/trending?page={page}&per_page={perPage}&customer_id={cid}&locale={loc}";
                var resp = await CallApiAsync<KlipyPagedData<KlipyMediaItem>>(url, token);
                var paged = resp.Data;
                return (paged?.Data ?? [], paged?.HasNext ?? false);
            },
            TrendingCacheOptions, cancellationToken: ct);
    }

    public async Task<(List<KlipyMediaItem> Items, bool HasNext)> SearchAsync(
        string query, int page, int perPage, Guid userId, string? locale = null, CancellationToken ct = default)
    {
        var normalizedQuery = query.Trim().ToLowerInvariant();
        var loc = NormalizeLocale(locale);
        var key = $"klipy:search:{loc}:{normalizedQuery}:{page}:{perPage}";
        var cid = ComputeCustomerId(userId);
        return await cache.GetOrCreateAsync(key,
            async token =>
            {
                var url = $"{ApiBase}/gifs/search?q={Uri.EscapeDataString(normalizedQuery)}&page={page}&per_page={perPage}&customer_id={cid}&locale={loc}";
                var resp = await CallApiAsync<KlipyPagedData<KlipyMediaItem>>(url, token);
                var paged = resp.Data;
                return (paged?.Data ?? [], paged?.HasNext ?? false);
            },
            SearchCacheOptions, cancellationToken: ct);
    }

    public async Task<List<KlipyCategory>> GetCategoriesAsync(string? locale = null, CancellationToken ct = default)
    {
        var loc = NormalizeLocale(locale);
        return await cache.GetOrCreateAsync($"klipy:categories:{loc}",
            async token =>
            {
                var url = $"{ApiBase}/gifs/categories?locale={loc}";
                var resp = await CallApiAsync<KlipyCategoriesData>(url, token);
                return resp.Data?.Categories ?? [];
            },
            CategoriesCacheOptions, cancellationToken: ct);
    }

    public async Task<KlipyMediaItem?> GetItemBySlugAsync(string slug, CancellationToken ct = default)
    {
        var url = $"{ApiBase}/gifs/items?slugs={Uri.EscapeDataString(slug)}";
        var resp = await CallApiAsync<KlipyPagedData<KlipyMediaItem>>(url, ct);
        return resp.Data?.Data?.FirstOrDefault();
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

    public async Task<(Guid FileId, int Width, int Height)?> EnsureCachedAsync(string slug, CancellationToken ct = default)
    {
        var cdnKey = ComputeCachePath(slug);
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var cachedFile = await db.Files
            .FirstOrDefaultAsync(x => x.S3Key == cdnKey && x.Finalized, ct);

        if (cachedFile is not null)
        {
            await refCount.IncrementAsync(cachedFile.Id, ct: ct);
            return (cachedFile.Id, 0, 0);
        }

        try
        {
            var mediaItem = await GetItemBySlugAsync(slug, ct);
            if (mediaItem is null) return null;

            var bestFile = mediaItem.File?.Md?.Webp
                ?? mediaItem.File?.Hd?.Webp
                ?? mediaItem.File?.Sm?.Webp
                ?? mediaItem.File?.Md?.Gif
                ?? mediaItem.File?.Sm?.Gif;

            if (bestFile?.Url is null) return null;

            var mediaBytes = await DownloadMediaAsync(bestFile.Url, ct);
            using var stream = new MemoryStream(mediaBytes);
            if (!await s3.PutObjectAsync(cdnKey, stream, "image/webp", ct))
                return null;

            var fileId = Guid.NewGuid();
            db.Files.Add(new FileEntity
            {
                Id          = fileId,
                OwnerId     = Guid.Empty,
                Purpose     = FilePurpose.Gif,
                S3Key       = cdnKey,
                BucketName  = "cdn",
                FileSize    = mediaBytes.Length,
                ContentType = "image/webp",
                Finalized   = true,
                CreatedAt   = DateTimeOffset.UtcNow,
                UpdatedAt   = DateTimeOffset.UtcNow
            });
            db.FileCounters.Add(new FileCounterEntity
            {
                Id        = fileId,
                RefCount  = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);

            return (fileId, bestFile.Width, bestFile.Height);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Klipy API error while caching GIF slug={Slug}", slug);
            return null;
        }
    }

    #region Private helpers

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

    private async Task<KlipyResponse<T>> CallApiAsync<T>(string url, CancellationToken ct)
    {
        using var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<KlipyResponse<T>>(ct);
        return result ?? new KlipyResponse<T>();
    }

    #endregion
}
