namespace Argon.Features.Expressions;

/// <summary>File and metadata limits for stickers and custom emoji (Telegram's numbers).</summary>
public static class ExpressionLimits
{
    public const int StickerSide = 512;
    public const int EmojiSide   = 100;

    public const int StickerStaticMaxBytes = 512 * 1024;
    public const int StickerLottieMaxBytes = 64 * 1024;
    public const int StickerVideoMaxBytes  = 256 * 1024;

    public const int EmojiStaticMaxBytes = 128 * 1024;
    public const int EmojiLottieMaxBytes = 64 * 1024;
    public const int EmojiVideoMaxBytes  = 256 * 1024;

    public const int ThumbMaxBytes   = 128 * 1024;
    public const int OutlineMaxBytes = 1024;

    public const double MaxDurationSeconds = 3.0;
    public const double LottieMaxFps       = 60;
    public const double VideoMaxFps        = 30;

    /// <summary>Decompressed Lottie JSON cap; also the cap on a plain-JSON upload.</summary>
    public const int LottieMaxJsonBytes = 4 * 1024 * 1024;
    public const int LottieMaxDepth     = 64;

    public const int EmojiNameMinLength   = 2;
    public const int EmojiNameMaxLength   = 32;
    public const int StickerNameMinLength = 2;
    public const int StickerNameMaxLength = 30;

    public const int MaxAssociatedEmoji     = 20;
    public const int MaxKeywords            = 20;
    public const int MaxKeywordsTotalLength = 64;

    public const int PackTitleMinLength = 1;
    public const int PackTitleMaxLength = 64;
    public const int PackSlugMinLength  = 1;
    public const int PackSlugMaxLength  = 64;

    public static int MaxBytes(ExpressionKind kind, ExpressionFormat format)
        => (kind, format) switch
        {
            (ExpressionKind.Sticker, ExpressionFormat.Static) => StickerStaticMaxBytes,
            (ExpressionKind.Sticker, ExpressionFormat.Lottie) => StickerLottieMaxBytes,
            (ExpressionKind.Sticker, ExpressionFormat.Video)  => StickerVideoMaxBytes,
            (ExpressionKind.Emoji, ExpressionFormat.Static)   => EmojiStaticMaxBytes,
            (ExpressionKind.Emoji, ExpressionFormat.Lottie)   => EmojiLottieMaxBytes,
            (ExpressionKind.Emoji, ExpressionFormat.Video)    => EmojiVideoMaxBytes,
            _                                                 => 0
        };

    /// <summary>
    /// Emoji are exactly 100×100; a Lottie sticker is 512×512; a static or video sticker has one side
    /// of exactly 512 and the other at most 512.
    /// </summary>
    public static bool DimensionsFit(ExpressionKind kind, ExpressionFormat format, int width, int height)
        => kind switch
        {
            ExpressionKind.Emoji => width == EmojiSide && height == EmojiSide,
            ExpressionKind.Sticker when format == ExpressionFormat.Lottie
                => width == StickerSide && height == StickerSide,
            ExpressionKind.Sticker
                => (width == StickerSide && height is >= 1 and <= StickerSide)
                || (height == StickerSide && width is >= 1 and <= StickerSide),
            _ => false
        };

    public static bool IsValidName(ExpressionKind kind, string? name)
        => kind switch
        {
            ExpressionKind.Emoji   => IsSlugLike(name, EmojiNameMinLength, EmojiNameMaxLength),
            ExpressionKind.Sticker => IsPlainText(name, StickerNameMinLength, StickerNameMaxLength),
            _                      => false
        };

    public static bool IsValidPackTitle(string? title)
        => IsPlainText(title, PackTitleMinLength, PackTitleMaxLength);

    public static bool IsValidPackSlug(string? slug)
        => IsSlugLike(slug, PackSlugMinLength, PackSlugMaxLength);

    public static bool AreValidAssociatedEmoji(IReadOnlyCollection<string>? emoji)
        => emoji is { Count: <= MaxAssociatedEmoji }
        && emoji.All(e => !string.IsNullOrWhiteSpace(e) && e.Length <= 32);

    public static bool AreValidKeywords(IReadOnlyCollection<string>? keywords)
        => keywords is null
        || (keywords.Count <= MaxKeywords
         && keywords.All(k => !string.IsNullOrWhiteSpace(k))
         && keywords.Sum(k => k.Length) <= MaxKeywordsTotalLength);

    private static bool IsSlugLike(string? value, int min, int max)
    {
        if (value is null || value.Length < min || value.Length > max)
            return false;

        foreach (var c in value)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
                return false;

        return true;
    }

    private static bool IsPlainText(string? value, int min, int max)
        => value is not null
        && value.Length >= min
        && value.Length <= max
        && !string.IsNullOrWhiteSpace(value)
        && value.Trim().Length == value.Length
        && !value.Any(char.IsControl);
}
