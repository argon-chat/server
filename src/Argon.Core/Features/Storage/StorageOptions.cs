namespace Argon.Features.Storage;

public class StorageOptions
{
    public const string SectionName = "Storage";

    public string Endpoint    { get; set; } = "";
    public string AccessKey   { get; set; } = "";
    public string SecretKey   { get; set; } = "";
    public string BucketName  { get; set; } = "";
    public string Region      { get; set; } = "auto";
    public bool   UseSsl      { get; set; } = true;

    /// <summary>
    ///     Base URL for public-read files (e.g. https://cdn.argon.gl)
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";
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
