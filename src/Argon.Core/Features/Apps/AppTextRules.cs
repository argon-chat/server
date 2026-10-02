namespace Argon.Features.Apps;

using System.Globalization;
using System.Text;
using AccountContracts;
using Argon.Core.Entities.Data;
using Argon.Features.Cosmetics;
using ArgonContracts;

/// <summary>A localized string an application may have, and the rules its values follow.</summary>
/// <param name="Enabled">False for a key whose name and limits are settled but which nobody writes yet.</param>
public sealed record AppTextKey(string Name, int MaxLength, bool SingleLine, bool BotsOnly, bool Enabled = true);

public static class AppTextKeys
{
    /// <summary>A bot's line under the DM header.</summary>
    public static readonly AppTextKey Motd = new("motd", 100, SingleLine: true, BotsOnly: true);

    /// <summary>Reserved for app and bot profiles.</summary>
    public static readonly AppTextKey Description = new("description", 400, SingleLine: false, BotsOnly: false, Enabled: false);

    public static readonly IReadOnlyList<AppTextKey> All = [Motd, Description];

    public static AppTextKey? Find(string key, DevAppType type)
        => All.FirstOrDefault(k => k.Name == key && k.Enabled && (!k.BotsOnly || type == DevAppType.Bot));
}

public static class AppTextRules
{
    public const int MaxLocales = 32;

    public static string CacheKey(IAppRef app) => app switch
    {
        AppById byId       => $"app:texts:{byId.appId:N}",
        AppByBotUser byBot => $"app:texts:bot:{byBot.userId:N}",
        _                  => throw new ArgumentOutOfRangeException(nameof(app))
    };

    /// <summary>
    /// Locales normalized, values trimmed and (for a one-line key) flattened, empty ones dropped.
    /// Length is counted in characters as a person counts them: an emoji is one.
    /// </summary>
    public static (AppTextError Error, string? Locale, SortedDictionary<string, string> Values) Normalize(
        AppTextKey key, IReadOnlyCollection<LocaleValue> values)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);

        if (values.Count > MaxLocales)
            return (AppTextError.TOO_MANY_LOCALES, null, result);

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in values)
        {
            if (!CosmeticLocale.TryNormalize(entry.locale, out var locale, out _))
                return (AppTextError.INVALID_LOCALE, entry.locale, result);

            if (!seen.Add(locale))
                return (AppTextError.DUPLICATE_LOCALE, locale, result);

            // Checked on the raw length first so a megabyte of input is refused before it is walked.
            if (entry.value.Length > key.MaxLength * 16)
                return (AppTextError.TOO_LONG, locale, result);

            var value = key.SingleLine ? Flatten(entry.value) : Clean(entry.value);

            if (new StringInfo(value).LengthInTextElements > key.MaxLength)
                return (AppTextError.TOO_LONG, locale, result);

            if (value.Length > 0)
                result[locale] = value;
        }

        if (result.Count > 0 && !result.ContainsKey(CosmeticLocale.Fallback))
            return (AppTextError.ENGLISH_REQUIRED, CosmeticLocale.Fallback, result);

        return (AppTextError.NONE, null, result);
    }

    /// <summary>Line breaks and other control characters become spaces, runs of spaces become one.</summary>
    public static string Flatten(string raw)
    {
        var builder = new StringBuilder(raw.Length);
        var space   = false;

        foreach (var c in raw)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
                builder.Append(' ');

            space = false;
            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>Line breaks kept as <c>\n</c>, other control characters dropped, the ends trimmed.</summary>
    public static string Clean(string raw)
    {
        var builder = new StringBuilder(raw.Length);

        foreach (var c in raw.ReplaceLineEndings("\n"))
            if (c == '\n' || !char.IsControl(c))
                builder.Append(c);

        return builder.ToString().Trim();
    }
}
