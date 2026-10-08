namespace Argon.Features.Auth;

using System.Text.RegularExpressions;

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
        => HeaderText.Sanitize(value, maxLength);

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
