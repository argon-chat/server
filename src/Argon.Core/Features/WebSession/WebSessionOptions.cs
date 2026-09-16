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
    /// The applications allowed to exchange a token, each with the audiences its tokens may carry.
    /// </summary>
    /// <remarks>
    /// <para>The audience is what decides whether a token may be exchanged, and not a client id,
    /// because the audience is the one thing this server puts on the token itself: the authorization
    /// endpoint sets it to the origin of the <c>redirect_uri</c> the flow came through, and a
    /// redirect_uri is checked against the application's own registration before a code is ever
    /// minted. So an audience of <c>https://app.argon.gl</c> can only have been issued to an
    /// application registered to redirect there. It is also the pin the developer console already
    /// runs on in production — see <c>AccountConsoleAuthOptions.ValidAudiences</c> — rather than a
    /// second mechanism invented here.</para>
    ///
    /// <para>The key is what lands in the <c>ner</c> field of the device cookie and therefore in
    /// every device-history row the session writes, so two web clients sharing one application id
    /// would be indistinguishable afterwards.</para>
    ///
    /// <para><b>Why the application is the key and the audience is the value</b>, which is the
    /// opposite of how this reads. A <c>:</c> in a configuration key is a section separator and
    /// nothing escapes it — so a JSON property named <c>https://app.argon.gl</c> does not become a
    /// key at all, it becomes a section called <c>https</c> containing one called
    /// <c>//app.argon.gl</c>, and binding that to a string yields nothing. Keyed the natural way
    /// round, every entry an operator wrote was silently dropped except a bare host with no scheme —
    /// which is the one spelling a token can never carry, because the authorization endpoint always
    /// writes a full origin. The feature was configured, reported no error, and refused every
    /// exchange. An application id is hexadecimal and has no such problem.</para>
    /// </remarks>
    public Dictionary<string, List<string>> TrustedApplications { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every audience any trusted application may present. The token validator's allowlist.</summary>
    public IEnumerable<string> TrustedAudiences
        => TrustedApplications.Values.SelectMany(audiences => audiences).Where(a => !string.IsNullOrWhiteSpace(a));

    /// <summary>
    /// The application an audience is registered to, or <c>null</c> if none is.
    /// </summary>
    /// <remarks>
    /// A scan rather than a reversed dictionary: the list is a handful of entries read once per
    /// sign-in, and a cache built from a mutable property is a second source of truth that a
    /// configuration reload would leave stale.
    /// </remarks>
    public string? ApplicationFor(string audience)
        => TrustedApplications
          .FirstOrDefault(pair => pair.Value.Any(a => string.Equals(a, audience, StringComparison.OrdinalIgnoreCase)))
           .Key;

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

        report.Require(TrustedApplications.Count > 0, nameof(TrustedApplications),
            "is empty, so every exchange is refused and the web client can never sign in — the " +
            "feature is registered but does nothing");

        foreach (var (appId, audiences) in TrustedApplications)
        {
            report.Require(audiences.Count > 0, $"{nameof(TrustedApplications)}:{appId}",
                "lists no audience, so no token can ever be matched to it and the entry does nothing");

            // Not checked as a URI: an audience is whatever string the token carries, and the
            // deployment lists the spellings it has seen — the same way the developer console's
            // audience list does. What IS checked is that it carries a scheme, because the only
            // spelling the authorization endpoint ever writes is a full origin, and a bare host is
            // therefore an entry that can never match anything. That was the shape production ran
            // for months while reporting itself healthy.
            foreach (var audience in audiences)
            {
                report.Require(!string.IsNullOrWhiteSpace(audience), $"{nameof(TrustedApplications)}:{appId}",
                    "lists an empty audience, which would match a token carrying no audience at all");

                report.Prefer(audience.Contains("://", StringComparison.Ordinal),
                    $"{nameof(TrustedApplications)}:{appId}",
                    $"lists '{audience}', which carries no scheme — the authorization endpoint writes "
                  + "the audience as a full origin, so an entry without one matches no token that can "
                  + "ever arrive");
            }
        }

        // One audience under two applications has no answer: whichever is found first decides the id
        // every device-history row for that browser is written under, and which one that is depends
        // on dictionary order.
        var ambiguous = TrustedApplications
           .SelectMany(pair => pair.Value.Select(audience => (audience, pair.Key)))
           .GroupBy(entry => entry.audience, StringComparer.OrdinalIgnoreCase)
           .Where(group => group.Select(entry => entry.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        foreach (var group in ambiguous)
            report.Require(false, nameof(TrustedApplications),
                $"lists audience '{group.Key}' under more than one application, and nothing decides "
              + "which of them a session arriving on it belongs to");

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
