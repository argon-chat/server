namespace Argon.Features.Vault;

using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VaultSharp;
using VaultSharp.V1.SecretsEngines.PKI;

public record SignedCertificateResult(
    string CertificatePem,
    string SerialNumber,
    string IssuingCaPem,
    string[] CaChainPem);

public interface IVaultPkiService
{
    Task<SignedCertificateResult> SignCsrAsync(string csrPem, string commonName, TimeSpan? ttl = null);
    Task RevokeCertificateAsync(string serialNumber);
    Task<bool> IsCertificateRevokedAsync(X509Certificate2 certificate, X509Certificate2 issuerCertificate);
    Task<string> GetCaCertificateAsync();
}

public sealed class VaultPkiService(IServiceProvider provider, IOptions<VaultPkiOptions> options, ILogger<VaultPkiService> logger)
    : IVaultPkiService
{
    private readonly VaultPkiOptions _options = options.Value;

    private IVaultClient Vault => provider.GetRequiredService<IVaultClient>();

    public async Task<SignedCertificateResult> SignCsrAsync(string csrPem, string commonName, TimeSpan? ttl = null)
    {
        var effectiveTtl = ttl ?? _options.DefaultTtl;

        var requestOptions = new SignCertificatesRequestOptions
        {
            Csr        = csrPem,
            CommonName = commonName,
            TimeToLive = $"{(long)effectiveTtl.TotalSeconds}s",
            CertificateFormat = CertificateFormat.pem
        };

        var result = await Vault.V1.Secrets.PKI.SignCertificateAsync(
            _options.RoleName,
            requestOptions,
            _options.MountPoint);

        logger.LogInformation("Signed certificate for CN={CommonName}, serial={Serial}",
            commonName, result.Data.SerialNumber);

        return new SignedCertificateResult(
            result.Data.CertificateContent,
            result.Data.SerialNumber,
            result.Data.IssuingCACertificateContent,
            result.Data.CAChainContent ?? []);
    }

    public async Task RevokeCertificateAsync(string serialNumber)
    {
        await Vault.V1.Secrets.PKI.RevokeCertificateAsync(serialNumber, _options.MountPoint);
        logger.LogInformation("Revoked certificate serial={Serial}", serialNumber);
    }

    public async Task<bool> IsCertificateRevokedAsync(X509Certificate2 certificate, X509Certificate2 issuerCertificate)
    {
        // OCSP check via the public PKI endpoint
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EndCertificateOnly;
        chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(issuerCertificate);

        var valid = chain.Build(certificate);

        foreach (var status in chain.ChainStatus)
        {
            if (status.Status == X509ChainStatusFlags.Revoked)
            {
                logger.LogWarning("Certificate serial={Serial} is revoked (OCSP/CRL)", certificate.SerialNumber);
                return true;
            }
        }

        if (!valid)
        {
            // Log non-revocation chain errors but don't treat as revoked
            foreach (var status in chain.ChainStatus)
                logger.LogWarning("Certificate chain status: {Status} - {Info}", status.Status, status.StatusInformation);
        }

        return false;
    }

    public async Task<string> GetCaCertificateAsync()
    {
        var ca = await Vault.V1.Secrets.PKI.ReadCACertificateAsync(
            CertificateFormat.pem, _options.MountPoint);
        return ca.CertificateContent;
    }
}
