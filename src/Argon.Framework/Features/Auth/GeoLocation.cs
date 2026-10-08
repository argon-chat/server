namespace Argon.Features.Auth;

/// <summary>
/// Where a request came from geographically, as far as the edge in front of this process said.
/// </summary>
/// <remarks>
/// <para>Two edges write these headers and they disagree on names: Traefik's geoip2 plugin writes
/// <c>X-GeoIP2-Country</c>/<c>-Region</c>/<c>-City</c> and Cloudflare writes <c>cf-ipcountry</c>,
/// <c>cf-region</c>, <c>cf-ipcity</c>. Both use a placeholder rather than an absent header when the
/// lookup failed — <c>XX</c> for the plugin, <c>XX</c>/<c>T1</c> for Cloudflare — and this type is
/// where those are turned back into "unknown" so nobody downstream shows a user "XX, XX".</para>
///
/// <para><see cref="Country"/> keeps the historical <c>"00"</c> sentinel for unknown because the CDN
/// router and the registration validator already key on it; the two optional parts are null when
/// unknown, which is what a display layer wants.</para>
/// </remarks>
public readonly record struct GeoLocation(string Country, string? Region, string? City)
{
    public const string UnknownCountry = "00";

    public static GeoLocation Unknown => new(UnknownCountry, null, null);

    public bool HasCountry => Country != UnknownCountry;

    /// <summary>The value edges write when they know nothing, in every spelling seen so far.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "XX", "00", "T1", "unknown", "-"
    };

    public static GeoLocation Of(string? country, string? region, string? city)
    {
        var iso = Clean(country, 8);

        return new GeoLocation(iso is null ? UnknownCountry : iso.ToUpperInvariant(), Clean(region, 64), Clean(city, 64));
    }

    /// <summary>Trims, drops placeholders and control characters, caps the length. Null means unknown.</summary>
    public static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = HeaderText.Sanitize(value, maxLength);

        return Placeholders.Contains(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// Caller-written header text on its way to a screen another user reads: no control characters,
/// no more than the cap.
/// </summary>
public static class HeaderText
{
    /// <summary>Drops control characters, caps the length.</summary>
    public static string Sanitize(string value, int maxLength)
    {
        var sb = new StringBuilder(Math.Min(value.Length, maxLength));

        foreach (var ch in value.Trim())
        {
            if (char.IsControl(ch))
                continue;

            sb.Append(ch);

            if (sb.Length >= maxLength)
                break;
        }

        return sb.ToString().Trim();
    }
}
