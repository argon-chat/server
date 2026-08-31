namespace Argon.Features.WebSession;

using Features.Clustering;
using Microsoft.AspNetCore.Http;

/// <summary>
/// The browser session for the first-party web client: which OAuth applications may trade an Aegis
/// token for one, and how the two cookies that carry it are scoped.
/// </summary>
/// <remarks>
/// <para>Deliberately narrow. This is not a way for applications to reach the Argon API — third
/// parties keep the OAuth tokens they already get, and those are never accepted by the Ion
/// interceptor. It exists because the web client is <i>ours</i> and needs the same session an
/// installed client has: bound to a machine, revocable, refreshable, and not sitting in
/// <c>localStorage</c> where any script on the page can read it.</para>
///
/// <para>Which applications qualify is a deployment decision rather than a property of an
/// application registration, which is why it lives here and not in the developer console.</para>
/// </remarks>
public sealed class WebSessionOptions : IValidatableFeatureOptions
{
    public const string SectionName = "WebSession";

    /// <summary>
    /// Token audiences allowed to be exchanged, each mapped to the application id the resulting
    /// session is recorded under.
    /// </summary>
    /// <remarks>
    /// <para>The audience, and not a client id, because the audience is the one thing this server
    /// puts on the token itself: the authorization endpoint sets it to the origin of the
    /// <c>redirect_uri</c> the flow came through, and a redirect_uri is checked against the
    /// application's own registration before a code is ever minted. So an audience of
    /// <c>https://app.argon.gl</c> can only have been issued to an application registered to redirect
    /// there. It is also the pin the developer console already runs on in production — see
    /// <c>AccountConsoleAuthOptions.ValidAudiences</c> — rather than a second mechanism invented
    /// here.</para>
    ///
    /// <para>The value is what lands in the <c>ner</c> field of the device cookie and therefore in
    /// every device-history row the session writes, so two web clients sharing one audience would be
    /// indistinguishable afterwards.</para>
    /// </remarks>
    public Dictionary<string, string> TrustedAudiences { get; set; } = [];

    /// <summary>Where the identity server publishes its signing keys.</summary>
    public string MetadataAddress { get; set; } = "";

    public string ValidIssuer { get; set; } = "";

    /// <summary>
    /// Name of the cookie carrying the refresh token.
    /// </summary>
    /// <remarks>
    /// The <c>__Host-</c> prefix is not decoration: it makes the browser refuse the cookie unless it
    /// is <c>Secure</c>, rooted at <c>/</c>, and carries no <c>Domain</c> — which is what confines it
    /// to the API host. Without it a script on any <c>*.argon.gl</c> subdomain could overwrite the
    /// session cookie of every user who visits it.
    /// </remarks>
    public string CookieName { get; set; } = "__Host-ArgonAuth";

    /// <summary>
    /// How long a web sign-in lasts.
    /// </summary>
    /// <remarks>
    /// This, and not the token inside it, is what bounds a web session: refresh tokens are minted
    /// with a ten-year lifetime and are not rotated, so the cookie's own expiry is the only thing
    /// that ends one on its own. Thirty days is a login, not a credential lifetime.
    /// </remarks>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// <c>SameSite</c> for both cookies.
    /// </summary>
    /// <remarks>
    /// <c>Lax</c> is correct while the web client lives under <c>argon.gl</c>: the browser then never
    /// attaches the cookie to a request started by another site, which is the cross-site request
    /// forgery defence and costs nothing, because <c>app.argon.gl</c> and <c>api.argon.gl</c> are the
    /// same site. A front-end served from anywhere else — a developer on <c>localhost</c> — is
    /// cross-site and needs <c>None</c>, which is what makes this configurable rather than fixed.
    /// </remarks>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;

    /// <summary>
    /// Domain for the device cookie, which is shared across the zone rather than host-only.
    /// </summary>
    /// <remarks>
    /// One <c>ArgonSecure</c> cookie per browser and no more. The developer console already writes it
    /// on <c>.argon.gl</c>, and a host-only one written beside it would leave two cookies of the same
    /// name in the jar with the reader taking whichever came first.
    /// </remarks>
    public string DeviceCookieDomain { get; set; } = ".argon.gl";

    /// <summary>
    /// How long the browser keeps its device identity. Long, because it is an identity and not a
    /// credential — it survives signing out, and clearing it is the user asking to look like a new
    /// machine.
    /// </summary>
    public TimeSpan DeviceLifetime { get; set; } = TimeSpan.FromDays(365);

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        report.RequireUri(MetadataAddress, nameof(MetadataAddress), "https", "http");
        report.RequireUri(ValidIssuer, nameof(ValidIssuer), "https", "http");

        report.Require(TrustedAudiences.Count > 0, nameof(TrustedAudiences),
            "is empty, so every exchange is refused and the web client can never sign in — the " +
            "feature is registered but does nothing");

        // Not checked as a URI: an audience is whatever string the token carries, and the deployment
        // lists the spellings it has seen — origin, origin with a trailing slash, bare host — the
        // same way the developer console's audience list does.
        foreach (var (audience, appId) in TrustedAudiences)
            report.Require(!string.IsNullOrWhiteSpace(appId), $"{nameof(TrustedAudiences)}:{audience}",
                "has no application id, and the session it issues would have nothing to record " +
                "itself under");

        report.Required(CookieName, nameof(CookieName));
        report.RequireRange(Lifetime, TimeSpan.FromMinutes(5), TimeSpan.FromDays(365), nameof(Lifetime));
        report.RequireRange(DeviceLifetime, TimeSpan.FromDays(1), TimeSpan.FromDays(365 * 5), nameof(DeviceLifetime));

        report.Prefer(CookieName.StartsWith("__Host-", StringComparison.Ordinal), nameof(CookieName),
            "does not carry the __Host- prefix, so nothing stops a script on another argon.gl " +
            "subdomain from overwriting the session cookie");

        report.Prefer(SameSite != SameSiteMode.Unspecified, nameof(SameSite),
            "is unspecified, which leaves the attribute off the cookie entirely and lets each " +
            "browser pick its own default");

        report.Require(DeviceLifetime > Lifetime, nameof(DeviceLifetime),
            "is shorter than the session it identifies, so a browser would lose its machine " +
            "identity while still holding a session bound to it — and every request on that " +
            "session would then fail the machine check");
    }
}
