namespace Argon.Features.Admin;

using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using Argon.Entities;
using Argon.Features.Vault;
using Microsoft.AspNetCore.Authentication;

/// <summary>
/// Authentication handler that validates client TLS certificates (mTLS) against the Operators table.
/// Works with YubiKey PIV certificates presented by the browser during TLS handshake.
/// </summary>
public sealed class OperatorCertificateAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory                              loggerFactory,
    System.Text.Encodings.Web.UrlEncoder        encoder,
    IServiceScopeFactory                        scopeFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "OperatorCert";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var clientCert = await Context.Connection.GetClientCertificateAsync();

        if (clientCert is null)
            return AuthenticateResult.NoResult();

        // compute thumbprint
        var thumbprint = Convert.ToHexString(clientCert.GetCertHash(HashAlgorithmName.SHA256));

        // verify chain of trust against our CA
        await using var scope = scopeFactory.CreateAsyncScope();
        var pkiService = scope.ServiceProvider.GetRequiredService<IVaultPkiService>();
        string caPem;
        try
        {
            caPem = await pkiService.GetCaCertificateAsync();
        }
        catch (Exception e)
        {
            Logger.LogError(e, "Failed to fetch CA certificate from Vault");
            return AuthenticateResult.Fail("Unable to verify certificate chain");
        }

        if (!VerifyChainOfTrust(clientCert, caPem))
            return AuthenticateResult.Fail("Certificate not trusted by operator CA");

        // find operator in database
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var op = await db.Operators.FirstOrDefaultAsync(
            x => x.CertificateThumbprint == thumbprint && !x.IsDeleted);

        if (op is null)
            return AuthenticateResult.Fail("Certificate not associated with any operator");

        if (!op.IsActive)
            return AuthenticateResult.Fail("Operator account is inactive");

        if (string.IsNullOrEmpty(op.CertificateSerialNumber))
            return AuthenticateResult.Fail("Operator has no enrolled certificate");

        // check revocation via Vault
        var isRevoked = await pkiService.IsCertificateRevokedAsync(op.CertificateSerialNumber);
        if (isRevoked)
            return AuthenticateResult.Fail("Certificate has been revoked");

        // update last auth timestamp
        op.LastAuthAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        // build claims principal
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, op.Id.ToString()),
            new Claim("sub", op.Id.ToString()),
            new Claim("email", op.Email),
            new Claim("typ", "operator"),
            new Claim("cert_tp", thumbprint),
            new Claim("cert_serial", op.CertificateSerialNumber)
        };

        var identity  = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);

        Logger.LogInformation("Operator {OperatorId} authenticated via mTLS (cert={Thumbprint})",
            op.Id, thumbprint);

        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static bool VerifyChainOfTrust(X509Certificate2 cert, string caPem)
    {
        using var caCert = X509Certificate2.CreateFromPem(caPem);
        using var chain  = new X509Chain();

        chain.ChainPolicy.RevocationMode    = X509RevocationMode.NoCheck; // we check via Vault separately
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.TrustMode         = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);

        return chain.Build(cert);
    }
}
