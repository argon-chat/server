namespace Argon.Features.Cosmetics;

/// <summary>
/// What media type each asset kind accepts, checked against what the store actually received.
/// </summary>
/// <remarks>
/// Fonts are the reason this is a function rather than a prefix table. A woff2 arrives as
/// <c>font/woff2</c> from one tool, <c>application/font-woff2</c> from another and
/// <c>application/octet-stream</c> from a browser that recognised nothing — all three are the same
/// file, and a single-prefix rule would reject two of them.
/// </remarks>
public static class CosmeticAssetMedia
{
    public static bool Accepts(CosmeticAssetKind kind, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        return kind switch
        {
            CosmeticAssetKind.Image       => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            CosmeticAssetKind.SpriteSheet => contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
            CosmeticAssetKind.Video       => contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase),
            CosmeticAssetKind.Font        => IsFont(contentType),
            _                             => false
        };
    }

    public static string Describe(CosmeticAssetKind kind) => kind switch
    {
        CosmeticAssetKind.Image       => "an image",
        CosmeticAssetKind.SpriteSheet => "a sprite sheet image",
        CosmeticAssetKind.Video       => "a video",
        CosmeticAssetKind.Font        => "a font file",
        _                             => kind.ToString()
    };

    private static bool IsFont(string contentType)
        => contentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("application/font", StringComparison.OrdinalIgnoreCase)
           || contentType.StartsWith("application/x-font", StringComparison.OrdinalIgnoreCase)
           || contentType.Equals("application/vnd.ms-opentype", StringComparison.OrdinalIgnoreCase)
           || contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
}
