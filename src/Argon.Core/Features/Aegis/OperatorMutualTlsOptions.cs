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
        if (!report.SectionExists)
            return;

        report.Required(CertificateHeader, nameof(CertificateHeader));
        report.RequireRange(VerificationLifetime, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1),
            nameof(VerificationLifetime));
    }
}
