namespace Argon.Features.Aegis;

using Argon.Features.Clustering;

/// <summary>
/// Staff step-up: proving, with a hardware key, that the person signing into an internal application
/// is the operator they claim to be.
/// </summary>
/// <remarks>
/// The certificate arrives in a header rather than a socket because TLS terminates at the proxy. That
/// makes <see cref="CertificateHeader"/> a credential in a header field, and worth being blunt about:
/// the proxy must overwrite it on the mutual-TLS route and strip it everywhere else. If it only sets
/// it, anyone may send one and every operator check becomes theatre.
/// </remarks>
public sealed class OperatorMutualTlsOptions : IValidatableFeatureOptions
{
    public const string SectionName = "OperatorMutualTls";

    public string CertificateHeader { get; set; } = "X-Forwarded-Tls-Client-Cert";

    /// <summary>
    /// How long a verification stays good for.
    /// </summary>
    /// <remarks>
    /// Short on purpose, and it is consumed rather than expired: an authorization that issues a token
    /// deletes the verification, so a second internal application asks for the key again. The window
    /// only covers the gap between touching the key and finishing the flow in front of it.
    /// </remarks>
    public TimeSpan VerificationLifetime { get; set; } = TimeSpan.FromMinutes(10);

    public void Validate(IFeatureConfigurationReport report)
    {
        RequireASessionTheStepUpCanSee(report);

        if (!report.SectionExists)
            return;

        report.Required(CertificateHeader, nameof(CertificateHeader));
        report.RequireRange(VerificationLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1),
            nameof(VerificationLifetime));
    }

    /// <summary>
    /// The step-up cannot ask who you are if the browser will not show it the session.
    /// </summary>
    /// <remarks>
    /// <para><b>Written after this took the smart-card sign-in down.</b> Requiring a client
    /// certificate is a per-host TLS setting, so the step-up is reached on a host of its own —
    /// <c>mtls.</c> in front of the widget's — and a session cookie with no <c>Domain</c> is scoped to
    /// the host that issued it. The browser therefore sends nothing, and the endpoint answers
    /// <c>not_authenticated</c> before it has looked at the certificate at all. Every log line is
    /// about the session; none is about the card.</para>
    ///
    /// <para>Checked outside the section's own <c>SectionExists</c> guard on purpose. The rule is
    /// about the feature being <i>enabled</i>, not about this section being written — and the way it
    /// happened was a deployment moving to a configuration blob that had never carried the session
    /// section, so every value in it silently became a default and nothing was there to check.</para>
    /// </remarks>
    private static void RequireASessionTheStepUpCanSee(IFeatureConfigurationReport report)
    {
        var session = report.Read<AegisSessionOptions>(AegisSessionOptions.SectionName);

        if (!string.IsNullOrWhiteSpace(session.CookieDomain))
            return;

        // A warning rather than a refusal, and the reason is not timidity. The shipped defaults leave
        // this empty, and a deployment serving the widget and the step-up from one host -- or not
        // using the step-up at all -- is entitled to them; refusing here would stop every fresh
        // install from starting. What it must not do is stay quiet, which is what it did.
        report.Prefer(false, $"{AegisSessionOptions.SectionName}:CookieDomain",
            "is empty while the operator step-up is enabled, so the session cookie is scoped to the " +
            "host that issued it. The step-up is served on a host of its own — a client certificate " +
            "is required per host — so the browser sends no cookie there and the certificate is never " +
            "reached: it answers 'not_authenticated', and every log line is about the session rather " +
            "than the card. Set it to a domain covering both hosts");
    }
}
