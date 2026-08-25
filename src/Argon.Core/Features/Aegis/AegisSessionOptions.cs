namespace Argon.Features.Aegis;

using Argon.Features.Clustering;

/// <summary>
/// The browser session the sign-in widget keeps while it walks a user through an OAuth flow.
/// </summary>
/// <remarks>
/// Deliberately separate from the tokens this server issues. The cookie says who is signed in <i>to
/// the identity server</i>, so that a second application can be authorized without asking for the
/// password again; it is never what an application is handed. That is why it may be
/// <see cref="SameSiteMode.None"/> — the widget is loaded cross-site by design — and why it must
/// then be <c>Secure</c> and <c>HttpOnly</c>, which is not configurable here.
/// </remarks>
public sealed class AegisSessionOptions : IValidatableFeatureOptions
{
    public const string SectionName = "AegisSession";

    public string CookieName { get; set; } = "aegis_session";

    /// <summary>
    /// Domain the cookie is scoped to, empty for host-only. A leading dot widens it to subdomains,
    /// which is what lets the widget and the applications under the same zone share one session.
    /// </summary>
    public string CookieDomain { get; set; } = "";

    public TimeSpan Lifetime { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a signed-in session is remembered by the browser across restarts. Longer than
    /// <see cref="Lifetime"/> on purpose: the cookie's own expiry is what a returning user is
    /// measured against, and the ticket inside it slides.
    /// </summary>
    public TimeSpan RememberFor { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Name the data-protection ring is stamped with. Keys are what decrypt the session cookie, so
    /// every replica of this role must agree on it or a user is signed out whichever node they land
    /// on next.
    /// </summary>
    public string DataProtectionApplicationName { get; set; } = "Aegis";

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        report.Required(CookieName, nameof(CookieName));
        report.Required(DataProtectionApplicationName, nameof(DataProtectionApplicationName));
        report.RequireRange(Lifetime, TimeSpan.FromMinutes(5), TimeSpan.FromDays(90), nameof(Lifetime));
        report.RequireRange(RememberFor, TimeSpan.FromMinutes(5), TimeSpan.FromDays(365), nameof(RememberFor));

        report.Require(RememberFor >= Lifetime, nameof(RememberFor),
            "is shorter than the ticket it carries, so the browser would drop a session that is " +
            "still valid");
    }
}
