namespace Argon.Features.Expressions;

public enum StatusIconKind
{
    None,
    Unicode,
    CustomEmoji,
    Invalid
}

/// <summary>
/// What a custom status icon id names: nothing (null or empty), a custom emoji as <c>ce:&lt;itemId&gt;</c>,
/// or else a unicode emoji.
/// </summary>
public static class StatusIcon
{
    public const string CustomEmojiPrefix = "ce:";
    public const int    MaxUnicodeLength  = 16;

    public static (StatusIconKind Kind, Guid ItemId) Parse(string? iconId)
    {
        if (string.IsNullOrEmpty(iconId))
            return (StatusIconKind.None, Guid.Empty);

        if (iconId.StartsWith(CustomEmojiPrefix, StringComparison.Ordinal))
            return Guid.TryParse(iconId.AsSpan(CustomEmojiPrefix.Length), out var itemId) && itemId != Guid.Empty
                ? (StatusIconKind.CustomEmoji, itemId)
                : (StatusIconKind.Invalid, Guid.Empty);

        return iconId.Length <= MaxUnicodeLength && !iconId.Contains(':')
            ? (StatusIconKind.Unicode, Guid.Empty)
            : (StatusIconKind.Invalid, Guid.Empty);
    }

    public static string CustomEmoji(Guid itemId) => CustomEmojiPrefix + itemId.ToString("D");
}
