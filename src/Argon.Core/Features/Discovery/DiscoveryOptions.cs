namespace Argon.Features.Discovery;

/// <summary>
/// Bound from the "Discovery" configuration section. Drives the public instance manifest that the
/// desktop client fetches to learn where to send API/CDN traffic for this instance. Defaults point
/// at the official argon.gl instance; self-hosters override the whole section in their appsettings.
/// </summary>
public sealed class DiscoveryOptions
{
    public const string SectionName = "Discovery";

    public InstanceManifestOptions Manifest { get; set; } = new();
}

public sealed class InstanceManifestOptions
{
    public int    SchemaVersion = 1;

    public string InstanceId  { get; set; } = "argon-official";
    public string DisplayName { get; set; } = "Argon";
    /// <summary>official | selfhosted | managed.</summary>
    public string Kind        { get; set; } = "official";

    // Only the endpoints the client actually consumes. The voice/WebRTC endpoint is negotiated
    // per-connection by the server (LiveKit grant), and SignalR is derived from ApiUrl — neither
    // belongs in the manifest.
    public string  ApiUrl { get; set; } = "https://api.argon.gl";
    public string  CdnUrl { get; set; } = "https://cdn.argon.gl";

    public string? LogoUrl     { get; set; }
    public string? AccentColor { get; set; } = "#3B82F6";

    public bool    RegistrationEnabled { get; set; } = true;
    public bool    QrLoginEnabled      { get; set; } = true;
    public string? SsoUrl              { get; set; }

    public string? TermsUrl   { get; set; }
    public string? PrivacyUrl { get; set; }

    public string? MinClientVersion { get; set; }
}
