namespace Argon.Features.Vault;

using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Caching.Hybrid;
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
    private HybridCache Cache => provider.GetRequiredService<HybridCache>();

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
        var crlPem = await Cache.GetOrCreateAsync(
            "pki:crl",
            async cancel =>
            {
                var httpClientFactory = provider.GetRequiredService<IHttpClientFactory>();
                using var httpClient = httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(10);
                return await httpClient.GetStringAsync(_options.CrlUrl, cancel);
            },
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(5),
                LocalCacheExpiration = TimeSpan.FromMinutes(5)
            });

        var derBytes = Convert.FromBase64String(
            crlPem.Replace("-----BEGIN X509 CRL-----", "")
                   .Replace("-----END X509 CRL-----", "")
                   .ReplaceLineEndings("")
                   .Trim());

        var builder = CertificateRevocationListBuilder.Load(derBytes, out _);
        return builder.RemoveEntry(Convert.FromHexString(certificate.SerialNumber));
    }

    public async Task<string> GetCaCertificateAsync()
    {
        var ca = await Vault.V1.Secrets.PKI.ReadCACertificateAsync(
            CertificateFormat.pem, _options.MountPoint);
        return ca.CertificateContent;
    }
}
