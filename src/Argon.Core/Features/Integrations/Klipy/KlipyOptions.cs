namespace Argon.Features.Integrations.Klipy;

public class KlipyOptions
{
    public const string SectionName = "Klipy";

    public string ApiKey  { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.klipy.com";

    /// <summary>HMAC key used to sign user-facing GIF tokens and CDN cache paths.</summary>
    public string HmacKey { get; set; } = string.Empty;

    /// <summary>Visible saved-GIF limit for free users.</summary>
    public int SavedGifLimitFree    { get; set; } = 200;
    /// <summary>Hard slot count for free users (soft buffer).</summary>
    public int SavedGifSlotsFree    { get; set; } = 300;

    /// <summary>Visible saved-GIF limit for premium users.</summary>
    public int SavedGifLimitPremium { get; set; } = 400;
    /// <summary>Hard slot count for premium users (soft buffer).</summary>
    public int SavedGifSlotsPremium { get; set; } = 500;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(HmacKey);
}
