namespace Argon.Features.Discovery;

// Serialized with the ASP.NET web JSON defaults (camelCase), so the client's zod schema sees
// schemaVersion / instanceUrl etc. Keep these names in sync with
// client/src/store/system/instanceStore.ts (instanceManifestSchema).

public sealed record InstanceManifestDto(
    int                  SchemaVersion,
    ManifestInstanceDto  Instance,
    ManifestEndpointsDto Endpoints,
    ManifestBrandingDto  Branding,
    ManifestFeaturesDto  Features,
    ManifestLegalDto     Legal,
    string?              MinClientVersion);

public sealed record ManifestInstanceDto(string Id, string Name, string Kind);

// Only what the client consumes. Voice/WebRTC is granted per-connection; SignalR is derived from api.
public sealed record ManifestEndpointsDto(string Api, string Cdn);

public sealed record ManifestBrandingDto(string DisplayName, string? LogoUrl, string? AccentColor);

public sealed record ManifestFeaturesDto(bool RegistrationEnabled, bool QrLoginEnabled, string? SsoUrl);

public sealed record ManifestLegalDto(string? TermsUrl, string? PrivacyUrl);

/// <summary>kind: "official" | "managed" | "unknown". instanceUrl set only when kind == "managed".</summary>
public sealed record ResolveResultDto(string Kind, string? InstanceUrl);
