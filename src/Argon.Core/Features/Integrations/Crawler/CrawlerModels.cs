namespace Argon.Features.Integrations.Crawler;

public record CrawlRequest(string Url);

public record CrawlBatchRequest(string[] Urls);

public record InvalidateRequest(string Url);

public record InvalidateResponse(bool Ok);

public record CrawlHealthResponse(string Status, long Uptime, int CacheSize, int RedisCacheSize);

public record CrawlResult(string Url, string Title, string? Description, string? Image, string? SiteName);

public record CrawlError(string Url, string Error, string? Code);
