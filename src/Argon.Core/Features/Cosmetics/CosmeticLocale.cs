namespace Argon.Features.Cosmetics;

using System.Text.RegularExpressions;

/// <summary>
/// The language a cosmetic's name is written in.
/// </summary>
public static partial class CosmeticLocale
{
    /// <summary>
    /// The language every client can read, and therefore the one a published row must be named in.
    /// It matches the client's own <c>fallbackLocale</c>; changing one without the other leaves
    /// somebody reading a slug.
    /// </summary>
    public const string Fallback = "en";

    /// <summary>
    /// Two letters, optionally an underscore and a region. Exactly two rather than BCP-47's two or
    /// three, because the app spells its languages in two even where the standard would not —
    /// <c>am</c> for Armenian, <c>jp</c> for Japanese — and this matches what the client will ask
    /// for rather than what a standard says it should.
    /// </summary>
    [GeneratedRegex("^[a-z]{2}(_[a-z0-9]{2,8})?$")]
    private static partial Regex Shape();

    /// <summary>
    /// Trims, lowercases and turns a dash into an underscore, then checks the shape. Deliberately
    /// not a membership test — see <c>CosmeticLocaleTests</c>.
    /// </summary>
    public static bool TryNormalize(string? raw, out string locale, out string? error)
    {
        locale = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A locale is required";
            return false;
        }

        var candidate = raw.Trim().ToLowerInvariant().Replace('-', '_');

        if (!Shape().IsMatch(candidate))
        {
            error = $"'{raw.Trim()}' is not a locale code — expected something like 'en' or 'ru_pt'";
            return false;
        }

        locale = candidate;
        error  = null;
        return true;
    }
}
