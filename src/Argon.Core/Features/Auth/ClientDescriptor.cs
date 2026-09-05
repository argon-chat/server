namespace Argon.Features.Auth;

using System.Text.RegularExpressions;

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

        var trimmed = ClientDescriptor.Sanitize(value, maxLength);

        return Placeholders.Contains(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// What a client says about itself, and what its User-Agent implies.
/// </summary>
/// <remarks>
/// <para>First-party clients send <c>X-Argon-Client</c>: <c>platform=windows; os=Windows%2011;
/// app=1.4.0; device=DESKTOP-7F2; arch=x64</c> — semicolon-separated <c>key=value</c> pairs with
/// percent-encoded values. Everything else, browsers above all, is read off the User-Agent, which
/// is why <see cref="From"/> takes both: the header wins field by field and the UA fills the rest.</para>
///
/// <para>Every byte here came from the caller. It names a session on the devices screen and in the
/// device history, and that is all it is good for — nothing is authorised, matched or banned on it.
/// Values are trimmed, stripped of control characters and capped so a hostile client cannot put
/// kilobytes or terminal escapes into a screen another user reads.</para>
/// </remarks>
public sealed partial record ClientDescriptor(
    ClientPlatform Platform,
    string OsName,
    string AppVersion,
    string DeviceName,
    string Browser,
    bool IsBrowser)
{
    public const string HeaderName = "X-Argon-Client";

    public static ClientDescriptor Unknown { get; } = new(ClientPlatform.UNKNOWN, "", "", "", "", false);

    public bool IsEmpty => this == Unknown;

    /// <summary>Reads the descriptor header, falling back to the User-Agent for anything it lacks.</summary>
    public static ClientDescriptor From(string? header, string? userAgent)
    {
        var fromUa = FromUserAgent(userAgent);

        if (string.IsNullOrWhiteSpace(header))
            return fromUa;

        var fields = ParseFields(header);

        var platform = fields.TryGetValue("platform", out var p) ? ParsePlatform(p) : ClientPlatform.UNKNOWN;
        var os       = fields.GetValueOrDefault("os") ?? "";
        var osv      = fields.GetValueOrDefault("osv") ?? "";
        var app      = fields.GetValueOrDefault("app") ?? "";
        var device   = fields.GetValueOrDefault("device") ?? "";
        var browser  = fields.GetValueOrDefault("browser") ?? "";
        var web      = fields.TryGetValue("web", out var w) && w is "1" or "true";

        // "Windows 11 Pro" over "10.0.26100", but the build alone is better than nothing.
        var osName = os.Length > 0 ? os : osv;

        return new ClientDescriptor(
            platform == ClientPlatform.UNKNOWN ? fromUa.Platform : platform,
            osName.Length > 0 ? osName : fromUa.OsName,
            app.Length > 0 ? app : fromUa.AppVersion,
            device,
            browser.Length > 0 ? browser : fromUa.Browser,
            web || (browser.Length == 0 && fromUa.IsBrowser && app.Length == 0));
    }

    /// <summary>
    /// The same shape the header uses, for carrying the descriptor across a grain call without a
    /// second serialiser. <see cref="FromTransport"/> reads it back.
    /// </summary>
    public string ToTransport()
        => string.Join("; ",
            $"platform={Platform.ToString().ToLowerInvariant()}",
            $"os={Uri.EscapeDataString(OsName)}",
            $"app={Uri.EscapeDataString(AppVersion)}",
            $"device={Uri.EscapeDataString(DeviceName)}",
            $"browser={Uri.EscapeDataString(Browser)}",
            $"web={(IsBrowser ? "1" : "0")}");

    public static ClientDescriptor FromTransport(string? transport)
        => string.IsNullOrWhiteSpace(transport) ? Unknown : From(transport, null);

    // ── header ────────────────────────────────────────────────────────────

    private const int MaxHeaderLength = 1024;

    private static Dictionary<string, string> ParseFields(string header)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (header.Length > MaxHeaderLength)
            header = header[..MaxHeaderLength];

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');

            if (eq <= 0)
                continue;

            var key = part[..eq].Trim().ToLowerInvariant();
            var raw = part[(eq + 1)..].Trim();

            string value;

            try
            {
                value = Uri.UnescapeDataString(raw);
            }
            catch (Exception)
            {
                value = raw;
            }

            fields[key] = Sanitize(value, key == "app" ? 32 : 64);
        }

        return fields;
    }

    private static ClientPlatform ParsePlatform(string value) => value.Trim().ToLowerInvariant() switch
    {
        "windows" or "win32" or "win"          => ClientPlatform.WINDOWS,
        "macos" or "darwin" or "mac" or "osx"  => ClientPlatform.MACOS,
        "linux"                                => ClientPlatform.LINUX,
        "android"                              => ClientPlatform.ANDROID,
        "ios" or "iphone" or "ipad"            => ClientPlatform.IOS,
        _                                      => ClientPlatform.UNKNOWN
    };

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

    // ── user agent ────────────────────────────────────────────────────────

    /// <summary>
    /// The little a User-Agent can be made to say: the OS family, the browser if it is one, and the
    /// version of an Argon client that names itself in it.
    /// </summary>
    /// <remarks>
    /// The Electron host reports <c>ArgonChat/1.4.0 … Electron/…</c>; the mobile apps report
    /// <c>ArgonChat-Android/…</c>. Neither is a browser even though both say "Chrome" somewhere, so
    /// the Argon token is checked first. Browsers are matched most-specific first because an Edge
    /// agent also says Chrome and a Chrome agent also says Safari.
    /// </remarks>
    public static ClientDescriptor FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
            return Unknown;

        var ua = Sanitize(userAgent, 512);

        var platform = ua switch
        {
            _ when AndroidRx().IsMatch(ua) => ClientPlatform.ANDROID,
            _ when IosRx().IsMatch(ua)     => ClientPlatform.IOS,
            _ when WindowsRx().IsMatch(ua) => ClientPlatform.WINDOWS,
            _ when MacRx().IsMatch(ua)     => ClientPlatform.MACOS,
            _ when LinuxRx().IsMatch(ua)   => ClientPlatform.LINUX,
            _                              => ClientPlatform.UNKNOWN
        };

        var osName = platform switch
        {
            ClientPlatform.ANDROID => AndroidRx().Match(ua) is { Success: true } a && a.Groups[1].Success
                ? $"Android {a.Groups[1].Value}"
                : "Android",
            ClientPlatform.IOS => IosVersionRx().Match(ua) is { Success: true } i
                ? $"iOS {i.Groups[1].Value.Replace('_', '.')}"
                : "iOS",
            ClientPlatform.WINDOWS => "Windows",
            ClientPlatform.MACOS   => "macOS",
            ClientPlatform.LINUX   => "Linux",
            _                      => ""
        };

        var argon = ArgonTokenRx().Match(ua);

        if (argon.Success)
            return new ClientDescriptor(platform, osName, Sanitize(argon.Groups[2].Value, 32), "", "", false);

        var browser = Browsers.FirstOrDefault(b => b.Match.IsMatch(ua));

        // Electron and other embedded shells are not browsers a person chose, and calling them
        // "Chrome" would be wrong on exactly the screen where wrong is expensive.
        if (browser is null || ElectronRx().IsMatch(ua))
            return new ClientDescriptor(platform, osName, "", "", "", false);

        return new ClientDescriptor(platform, osName, "", "", browser.Name, true);
    }

    private sealed record BrowserRule(Regex Match, string Name);

    private static readonly BrowserRule[] Browsers =
    [
        new(EdgeRx(), "Microsoft Edge"),
        new(YandexRx(), "Yandex Browser"),
        new(OperaRx(), "Opera"),
        new(FirefoxRx(), "Firefox"),
        new(ChromeRx(), "Chrome"),
        new(SafariRx(), "Safari")
    ];

    [GeneratedRegex(@"\bAndroid(?:[ /](\d+(?:\.\d+)?))?", RegexOptions.IgnoreCase)]
    private static partial Regex AndroidRx();

    [GeneratedRegex(@"\b(?:iPhone|iPad|iPod)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IosRx();

    [GeneratedRegex(@"\bOS (\d+(?:_\d+)+)\b")]
    private static partial Regex IosVersionRx();

    [GeneratedRegex(@"\bWindows(?: NT)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsRx();

    [GeneratedRegex(@"\b(?:Macintosh|Mac OS X|macOS)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MacRx();

    [GeneratedRegex(@"\b(?:Linux|X11|CrOS|Ubuntu)\b", RegexOptions.IgnoreCase)]
    private static partial Regex LinuxRx();

    /// <summary>The Electron host and the mobile apps name themselves like <c>ArgonChat/1.4.0</c>.</summary>
    [GeneratedRegex(@"\b(ArgonChat(?:-[A-Za-z]+)?|Argon(?:Desktop|Mobile)?)/([\w.+-]+)")]
    private static partial Regex ArgonTokenRx();

    [GeneratedRegex(@"\bElectron/", RegexOptions.IgnoreCase)]
    private static partial Regex ElectronRx();

    [GeneratedRegex(@"\bEdg[eA]?/", RegexOptions.IgnoreCase)]
    private static partial Regex EdgeRx();

    [GeneratedRegex(@"\bYaBrowser/", RegexOptions.IgnoreCase)]
    private static partial Regex YandexRx();

    [GeneratedRegex(@"\b(?:OPR|Opera)/", RegexOptions.IgnoreCase)]
    private static partial Regex OperaRx();

    [GeneratedRegex(@"\bFirefox/", RegexOptions.IgnoreCase)]
    private static partial Regex FirefoxRx();

    [GeneratedRegex(@"\b(?:Chrome|CriOS)/", RegexOptions.IgnoreCase)]
    private static partial Regex ChromeRx();

    [GeneratedRegex(@"\bSafari/", RegexOptions.IgnoreCase)]
    private static partial Regex SafariRx();
}
