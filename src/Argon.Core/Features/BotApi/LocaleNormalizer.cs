namespace Argon.Features.BotApi;

/// <summary>
/// Normalizes the client app's locale codes into BCP-47 language tags for the public Bot API.
/// The app uses some non-standard / joke codes (jp, am, ru_pt "Pirate Russian", en_tengwar);
/// bots and TTS engines expect standard codes, so we map at the contract boundary.
/// </summary>
public static class LocaleNormalizer
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["jp"]         = "ja",
        ["am"]         = "hy",
        ["ru_pt"]      = "ru",
        ["en_tengwar"] = "en",
    };

    /// <summary>
    /// Maps a raw client app locale code to a BCP-47 language tag.
    /// Returns null for empty/unparseable input.
    /// </summary>
    public static string? ToBcp47(string? appCode)
    {
        if (string.IsNullOrWhiteSpace(appCode))
            return null;

        var code = appCode.Trim();

        if (Map.TryGetValue(code, out var mapped))
            return mapped;

        // Unknown code → take the base language subtag (before '_' or '-')
        var baseSubtag = code.Split('_', '-')[0].ToLowerInvariant();

        // Validate: a BCP-47 primary language subtag is 2–3 ASCII letters
        if (baseSubtag.Length is < 2 or > 3 || !baseSubtag.All(char.IsAsciiLetter))
            return null;

        return baseSubtag;
    }
}
