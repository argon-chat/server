namespace Argon.Features.Auth;

using Argon.Features.Clustering;
using Argon.Features.WebSession;

/// <summary>
/// Which application ids are ours, and what each one is.
/// </summary>
/// <remarks>
/// <para>Every request carries an application id — the <c>ner</c> field of the <c>ArgonSecure</c>
/// cookie — and until now nothing on the server knew what any of them meant: device history was
/// written with <c>"unknown"</c>, every session was recorded as a Windows desktop, and the devices
/// screen guessed at names from a User-Agent. This is the table that turns an id into a name and a
/// kind, so a session can say "Argon Desktop" and a history row can say what it was.</para>
///
/// <para>The desktop id is a constant baked into the native security plugin that writes the cookie;
/// it is the same on every platform the Electron host runs on, which is why an
/// entry carries a <em>kind</em> and not an operating system: the OS comes from what the client
/// reports about itself (<see cref="ClientDescriptor"/>) and the kind says how to read that. The web
/// client's id is the OAuth <c>client_id</c> the developer console assigned, the same value
/// <see cref="WebSessionOptions.TrustedAudiences"/> files web sessions under. Both ship as defaults
/// here; a deployment that minted its own ids overrides them.</para>
///
/// <para>Configuration merges dictionaries by key, so a deployment adds the ids it minted and keeps
/// the desktop entry without restating it.</para>
/// </remarks>
public sealed class ClientAppsOptions : IValidatableFeatureOptions
{
    public const string SectionName = "auth:clientApps";

    /// <summary>
    /// The id the desktop client writes into <c>ner</c>. Shared by Windows, macOS and Linux builds.
    /// </summary>
    /// <remarks>
    /// Baked into the native security plugin (<c>SecurityExports.AppId</c> in the desktop repository),
    /// which writes the cookie. Changing it there without changing it here turns every desktop session
    /// back into an unknown application.
    /// </remarks>
    public const string DesktopAppId = "875180ED6396874C0536D95B30BB7B47";

    /// <summary>
    /// The web client's id — the OAuth <c>client_id</c> it signs in with, and the value
    /// <c>WebSession:TrustedAudiences</c> files its sessions under.
    /// </summary>
    /// <remarks>
    /// Desktop builds before September 2026 wrote this same id into their cookie, which is the
    /// collision that made a browser tab and an installed app indistinguishable. See
    /// <see cref="Find(string?, ClientDescriptor)"/> for how such a build is still told apart.
    /// </remarks>
    public const string WebAppId = "A37E7A1DB06E9610C9C0BD77C61A821B";

    /// <summary>Application id to what it is. Keys compare case-insensitively.</summary>
    public Dictionary<string, ClientAppEntry> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [DesktopAppId] = new() { Name = "Argon Desktop", Kind = ClientAppKind.Desktop },
        [WebAppId]     = new() { Name = "Argon Web", Kind = ClientAppKind.Web }
    };

    public ClientAppEntry? Find(string? appId)
        => !string.IsNullOrWhiteSpace(appId) && Apps.TryGetValue(appId.Trim(), out var entry) ? entry : null;

    /// <summary>
    /// The entry for an id, read together with what the client said about itself.
    /// </summary>
    /// <remarks>
    /// One correction on top of the plain lookup, for the transition the ids are in: a desktop build
    /// from before the ids were separated still presents the web client's id. The registry would call
    /// it a browser, but it is not one — it has no browser token and does name an Argon version — so
    /// it is read as the desktop application instead. The rule applies only to an id registered as
    /// <see cref="ClientAppKind.Web"/> and only while installed clients that old are still out there;
    /// once they have all updated it matches nothing and can go.
    /// </remarks>
    public ClientAppEntry? Find(string? appId, ClientDescriptor client)
    {
        var entry = Find(appId);

        if (entry is { Kind: ClientAppKind.Web } && !client.IsBrowser && client.AppVersion.Length > 0)
            return Find(DesktopAppId) ?? entry;

        return entry;
    }

    public bool IsFirstParty(string? appId) => Find(appId) is not null;

    public void Validate(IFeatureConfigurationReport report)
    {
        foreach (var (appId, entry) in Apps)
        {
            report.Require(!string.IsNullOrWhiteSpace(appId), nameof(Apps), "contains an entry with an empty application id");
            report.Require(!string.IsNullOrWhiteSpace(entry.Name), $"{nameof(Apps)}:{appId}",
                "has no name, so a session from it would be shown as an unknown application");
        }

        // The web client's session is filed under the id in WebSession:TrustedAudiences, so an id
        // there that is not here is a web session the devices screen cannot name.
        WebSessionOptions? web;

        try
        {
            web = report.Read<WebSessionOptions>(WebSessionOptions.SectionName);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var webAppId in web.TrustedAudiences.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(webAppId) || webAppId.Contains("<<SET"))
                continue;

            report.Prefer(Apps.ContainsKey(webAppId), nameof(Apps),
                $"does not list '{webAppId}', the id WebSession:TrustedAudiences files web sessions under — " +
                "those sessions will be shown as an unknown application");
        }
    }
}

/// <summary>What sort of thing an application is, which decides how its self-description is read.</summary>
public enum ClientAppKind
{
    Unknown,
    Desktop,
    Web,
    Mobile,
    Bot
}

public sealed class ClientAppEntry
{
    /// <summary>Shown on the devices screen: "Argon Desktop", "Argon Web".</summary>
    public string Name { get; set; } = "";

    public ClientAppKind Kind { get; set; } = ClientAppKind.Unknown;
}

/// <summary>
/// Turns an application entry plus a client's self-description into the two facts the rest of the
/// server records about a session: what to call it and what kind of device it was.
/// </summary>
public static class ClientIdentity
{
    /// <summary>
    /// The registered name if the id is ours; the browser for a web session; otherwise nothing.
    /// </summary>
    public static string AppName(ClientAppEntry? app, ClientDescriptor client)
    {
        if (app is not null && !string.IsNullOrWhiteSpace(app.Name))
            return app.Name;

        return client.IsBrowser ? client.Browser : "";
    }

    /// <summary>
    /// The device history's coarse category. The registry says the kind; the descriptor says the OS;
    /// with neither, the User-Agent's platform is the best available answer.
    /// </summary>
    public static DeviceTypeKind DeviceType(ClientAppEntry? app, ClientDescriptor client)
    {
        var kind = app?.Kind ?? ClientAppKind.Unknown;

        if (kind == ClientAppKind.Unknown)
        {
            if (client.IsBrowser)
                return DeviceTypeKind.Browser;

            kind = client.Platform switch
            {
                ClientPlatform.ANDROID or ClientPlatform.IOS => ClientAppKind.Mobile,
                ClientPlatform.WINDOWS or ClientPlatform.MACOS or ClientPlatform.LINUX => ClientAppKind.Desktop,
                _ => ClientAppKind.Unknown
            };
        }

        return kind switch
        {
            ClientAppKind.Web => DeviceTypeKind.Browser,
            ClientAppKind.Desktop => client.Platform switch
            {
                ClientPlatform.WINDOWS => DeviceTypeKind.WindowsDesktop,
                ClientPlatform.MACOS   => DeviceTypeKind.OsxDesktop,
                ClientPlatform.LINUX   => DeviceTypeKind.LinuxDesktop,
                _                      => DeviceTypeKind.Unknown
            },
            ClientAppKind.Mobile => client.Platform switch
            {
                ClientPlatform.ANDROID => DeviceTypeKind.AndroidMobile,
                ClientPlatform.IOS     => DeviceTypeKind.IosMobile,
                _                      => DeviceTypeKind.Unknown
            },
            _ => DeviceTypeKind.Unknown
        };
    }
}
