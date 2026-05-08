namespace Argon.Features.Vault;

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
    Task<bool> IsCertificateRevokedAsync(string serialNumber);
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

    public async Task<bool> IsCertificateRevokedAsync(string serialNumber)
    {
        try
        {
            var revoked = await Vault.V1.Secrets.PKI.ListRevokedCertificatesAsync(_options.MountPoint);
            return revoked.Data.Keys?.Contains(serialNumber) == true;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to check revocation status for serial={Serial}", serialNumber);
            return true; // fail-closed: treat as revoked if we can't verify
        }
    }

    public async Task<string> GetCaCertificateAsync()
    {
        var ca = await Vault.V1.Secrets.PKI.ReadCACertificateAsync(
            CertificateFormat.pem, _options.MountPoint);
        return ca.CertificateContent;
    }
}
