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
    /// Base address the <c>avatarUrl</c> in <c>userinfo</c> is built on. Empty omits the field.
    /// </summary>
    /// <remarks>
    /// <para>The API rather than a storage host, and not because it is nearer: the address has to
    /// outlive whatever is behind it. <c>{api}/files/{id}</c> is answered by a redirect that picks a
    /// regional mirror per request, so the same URL keeps working when mirrors are added, moved or
    /// retired — while a URL naming a mirror directly is correct only until it is not, and by then a
    /// third-party application has it stored.</para>
    ///
    /// <para>Its own setting rather than the storage feature's <c>Storage:Cdn:PublicBaseUrl</c>
    /// because the identity server does not have that feature: it holds no storage credentials, has
    /// no database, and needs exactly one thing from all of it — a hostname. Reaching for the other
    /// section would mean giving this role an object store it has no business knowing about.</para>
    /// </remarks>
    public string AvatarBaseUrl { get; set; } = "";

    /// <summary>
    /// Directory the sign-in widget is served from, and the <c>index.html</c> a client-routed path
    /// falls back to. Empty serves no static files at all, which is what a deployment that puts the
    /// widget behind its own CDN wants — and what a local run wants, since the directory only
    /// exists inside the image.
    /// </summary>
    /// <remarks>
    /// The shipped image builds it into <c>/app/aegis</c>; see the <c>widgets</c> stage in
    /// <c>src/Argon.Api/Dockerfile</c> and <c>src/Frontend/Aegis</c>. It was <c>/app/wwwroot</c>
    /// until the developer console joined it in the same image.
    /// </remarks>
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
    /// Path prefixes served without a CSP, for anything mounted here whose payloads are not
    /// documents and to which a policy would only ever be in the way.
    /// </summary>
    /// <remarks>
    /// Empty by default. This used to be <c>["/k"]</c>, for the Sentry tunnel, which this role no
    /// longer maps — the sign-in widget it serves has no browser-side reporter to forward. A
    /// deployment still naming <c>/k</c> here is excluding a path that answers 404.
    /// </remarks>
    public List<string> CspExcludedPaths { get; set; } = [];

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        report.Prefer(!string.IsNullOrWhiteSpace(Host), nameof(Host),
            "is empty, so the issuer and redirect URLs this server emits are built from whatever " +
            "Host header the request carried");

        foreach (var path in CspExcludedPaths)
            report.Require(path.StartsWith('/'), nameof(CspExcludedPaths), $"'{path}' is not a rooted path");

        // An absolute base or nothing. A relative one composes into a URL that resolves against
        // whichever application received it, which is the one host it certainly must not mean.
        if (!string.IsNullOrWhiteSpace(AvatarBaseUrl))
            report.RequireUri(AvatarBaseUrl, nameof(AvatarBaseUrl), "https", "http");

        report.Prefer(!string.IsNullOrWhiteSpace(AvatarBaseUrl), nameof(AvatarBaseUrl),
            "is empty, so userinfo carries no avatarUrl and every application integrating with this " +
            "provider has to work out where avatars live on its own");

        if (!string.IsNullOrWhiteSpace(StaticRoot))
            report.Require(Directory.Exists(StaticRoot), nameof(StaticRoot),
                $"'{StaticRoot}' does not exist, so the widget would 404 and the fallback would throw");
    }
}
