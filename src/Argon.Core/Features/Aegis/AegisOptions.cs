namespace Argon.Features.Aegis;

using Argon.Features.Clustering;

/// <summary>
/// The identity server's own surface: the host it answers as, and the browser-facing hardening
/// around it.
/// </summary>
/// <remarks>
/// <see cref="Host"/> is a pin, not a hint. Everything the OAuth provider emits — the issuer in a
/// discovery document, the audience in a token, the redirect back to an application — is built from
/// the request's host, so a request arriving with a forged <c>Host</c> header would have the server
/// mint links pointing at the forger. Overwriting the header before anything reads it is what stops
/// that; the proxy in front is the only thing entitled to decide which name this is.
/// </remarks>
public sealed class AegisOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Aegis";

    /// <summary>The host every request is treated as having arrived at. Empty leaves it alone.</summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// Directory the sign-in widget is served from, and the <c>index.html</c> a client-routed path
    /// falls back to. Empty serves no static files at all, which is what a deployment that puts the
    /// widget behind its own CDN wants.
    /// </summary>
    public string StaticRoot { get; set; } = "";

    public bool SecurityHeaders { get; set; } = true;

    /// <summary>
    /// Content-Security-Policy for pages, or empty for none. Paths excluded by
    /// <see cref="CspExcludedPaths"/> never get one.
    /// </summary>
    public string ContentSecurityPolicy { get; set; } =
        "default-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "connect-src 'self' https://*.aegis.argon.gl https://*.argon.gl; " +
        "img-src 'self' data: https://*.cdn.argon.gl https://*.argon.gl; " +
        "worker-src 'self' blob:; frame-ancestors 'none';";

    /// <summary>
    /// Path prefixes served without a CSP — the Sentry tunnel, whose payloads are not documents and
    /// whose policy would only ever be in the way.
    /// </summary>
    public List<string> CspExcludedPaths { get; set; } = ["/k"];

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        report.Prefer(!string.IsNullOrWhiteSpace(Host), nameof(Host),
            "is empty, so the issuer and redirect URLs this server emits are built from whatever " +
            "Host header the request carried");

        foreach (var path in CspExcludedPaths)
            report.Require(path.StartsWith('/'), nameof(CspExcludedPaths), $"'{path}' is not a rooted path");

        if (!string.IsNullOrWhiteSpace(StaticRoot))
            report.Require(Directory.Exists(StaticRoot), nameof(StaticRoot),
                $"'{StaticRoot}' does not exist, so the widget would 404 and the fallback would throw");
    }
}
