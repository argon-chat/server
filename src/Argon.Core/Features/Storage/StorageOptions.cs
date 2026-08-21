namespace Argon.Features.Storage;

public class StorageOptions
{
    public const string SectionName = "Storage";

    public string Endpoint   { get; set; } = "";
    public string AccessKey  { get; set; } = "";
    public string SecretKey  { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string Region     { get; set; } = "auto";
    public bool   UseSsl     { get; set; } = true;

    /// <summary>
    ///     When true, avatars are stored at the bucket root as just {fileId} (no path prefix).
    ///     This keeps AvatarFileId == S3 key == fileId GUID string.
    /// </summary>
    public bool FlatAvatarKeys { get; set; } = true;

    /// <summary>
    ///     Separate bucket for user data export archives.
    ///     Objects in this bucket should have a lifecycle rule to expire after 48 hours.
    /// </summary>
    public string ExportBucketName { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);

    public CdnOptions Cdn { get; set; } = new();
}

// CDN geo-routing.
//
// We DO NOT bake a region-specific CDN domain into the URL anymore (that was bug-prone: the resolved
// absolute URL got embedded in chat messages + aggressively cached on the client, so a transient
// VPN/region pinned a dead cross-region URL forever). Instead every file URL is by fileId and points
// at THIS instance's own API: {PublicBaseUrl}/files/{fileId}. The desktop client builds that URL from
// its OWN configured api endpoint (so self-hosted instances point at their own API), and the API 302s
// it to the nearest reachable regional mirror by CF-IPCountry — see CdnRedirectFeature. The 302 is
// (mostly) uncached so a region change self-heals on the next fetch.
//
// Config (Storage:Cdn):
//   PublicBaseUrl        public base for URLs the SERVER emits to API consumers (bot API / upload
//                        responses), e.g. https://api.argon.gl. Empty => relative "/files/...". The
//                        desktop client ignores these and builds from its own api endpoint.
//   Default              fallback regional origin when a country has no explicit Regions entry.
//   Regions[]            regional origins; each lists the ISO country codes (CF-IPCountry) it serves.
//   RedirectCacheSeconds max-age for the 302 itself; 0 => no-store (region re-evaluated every fetch).
public class CdnOptions
{
    public string                 PublicBaseUrl        { get; set; } = "";
    public CdnRegionTarget        Default              { get; set; } = new();
    public List<CdnRegionTarget>  Regions              { get; set; } = new();
    public int                    RedirectCacheSeconds { get; set; } = 0;

    // ISO country (UPPER) -> regional origin, built lazily from Regions. Idempotent build, so a
    // benign race on first access is harmless.
    private Dictionary<string, CdnRegionTarget>? _byCountry;
    private Dictionary<string, CdnRegionTarget> ByCountry =>
        _byCountry ??= Regions
           .SelectMany(r => (r.Countries ?? []).Select(c => (c, r)))
           .GroupBy(t => t.c.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
           .ToDictionary(g => g.Key, g => g.First().r, StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable by-fileId URL the API 302s (region resolved per-fetch). Relative when PublicBaseUrl is unset.</summary>
    public string BuildFileUrl(Guid fileId)
        => $"{PublicBaseUrl.TrimEnd('/')}/files/{fileId}";

    /// <summary>Stable by-key URL for keyless assets (cached GIFs, flat-keyed avatars in exports).</summary>
    public string BuildKeyUrl(string objectKey)
        => $"{PublicBaseUrl.TrimEnd('/')}/files/k/{objectKey}";

    /// <summary>Resolve the regional origin for a country (ISO from CF-IPCountry); falls back to Default.</summary>
    public CdnRegionTarget ResolveTarget(string? countryCode)
        => !string.IsNullOrEmpty(countryCode) && ByCountry.TryGetValue(countryCode, out var t) ? t : Default;

    /// <summary>The concrete regional URL the geo-redirect endpoint 302s to: {BaseUrl}/{PathPrefix}/{key}.</summary>
    public string BuildRegionalUrl(string? countryCode, string objectKey)
    {
        var t      = ResolveTarget(countryCode);
        var prefix = string.IsNullOrEmpty(t.PathPrefix) ? "" : $"/{t.PathPrefix.Trim('/')}";
        return $"{t.BaseUrl.TrimEnd('/')}{prefix}/{objectKey}";
    }
}

public class CdnRegionTarget
{
    public string   Name       { get; set; } = "";
    public string   BaseUrl    { get; set; } = "";
    public string   PathPrefix { get; set; } = "";
    public string[] Countries  { get; set; } = [];
}

public class FileLimitsOptions
{
    public const string SectionName = "Storage:Limits";

    /// <summary>
    ///     Avatar max size in bytes (default 5 MB)
    /// </summary>
    public long AvatarMaxBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>
    ///     Channel attachment max for base tier (default 15 MB)
    /// </summary>
    public long AttachmentBaseMaxBytes { get; set; } = 15 * 1024 * 1024;

    /// <summary>
    ///     Channel attachment max for Ultima subscribers (default 50 MB)
    /// </summary>
    public long AttachmentUltimaMaxBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    ///     Channel attachment max for boost level 2 spaces (default 50 MB)
    /// </summary>
    public long AttachmentBoostLevel2MaxBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>
    ///     Channel attachment max for boost level 3 spaces (default 100 MB)
    /// </summary>
    public long AttachmentBoostLevel3MaxBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    ///     Emoji max size (default 1 MB)
    /// </summary>
    public long EmojiMaxBytes { get; set; } = 1 * 1024 * 1024;

    /// <summary>
    ///     Sticker max size (default 2 MB)
    /// </summary>
    public long StickerMaxBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>
    ///     Banner max size (default 10 MB)
    /// </summary>
    public long BannerMaxBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    ///     Video max size (default 100 MB)
    /// </summary>
    public long VideoMaxBytes { get; set; } = 100 * 1024 * 1024;

    /// <summary>
    ///     Upload blob TTL in seconds (default 600 = 10 min)
    /// </summary>
    public int BlobTtlSeconds { get; set; } = 600;
}
