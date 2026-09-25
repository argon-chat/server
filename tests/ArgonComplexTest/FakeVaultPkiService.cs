namespace ArgonComplexTest;

using System.Collections.Concurrent;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Argon.Features.Vault;

/// <summary>
/// Vault's PKI engine, as far as the operator console uses it: sign a CSR under a CA, revoke a serial.
/// </summary>
/// <remarks>
/// <para>The suite runs no Vault, and without one <c>VaultPkiService</c> cannot be constructed into
/// doing anything — it resolves an <c>IVaultClient</c> that no test role registers. Enrolment and
/// revocation of operator certificates therefore had no test at all. This signs with a real, in-memory
/// CA, so what the grain parses (subject, validity, thumbprint) comes out of a genuine X.509
/// certificate rather than a canned string.</para>
///
/// <para>A common name containing <see cref="UnreachableMarker"/> fails the way an unreachable Vault
/// does. Keyed on the request rather than on a switch, so fixtures running in parallel cannot turn
/// each other's Vault off.</para>
/// </remarks>
public sealed class FakeVaultPkiService : IVaultPkiService
{
    public const string UnreachableMarker = "vault-unreachable";

    private readonly X509Certificate2 _ca;

    public FakeVaultPkiService()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = new CertificateRequest("CN=Argon Test Operator CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

        _ca   = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        CaPem = _ca.ExportCertificatePem();
    }

    /// <summary>The issuing CA, PEM. Vault answers with it as <c>issuing_ca</c>.</summary>
    public string CaPem { get; }

    /// <summary>Every certificate signed, by serial, with the common name it was signed for.</summary>
    public ConcurrentDictionary<string, string> Signed { get; } = new();

    /// <summary>Every revocation Vault was asked for, in order, duplicates included.</summary>
    public ConcurrentQueue<string> Revocations { get; } = new();

    public Task<SignedCertificateResult> SignCsrAsync(string csrPem, string commonName, TimeSpan? ttl = null)
    {
        if (commonName.Contains(UnreachableMarker, StringComparison.Ordinal))
            throw new HttpRequestException("Vault is unreachable");

        var csr = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256);

        var request = new CertificateRequest(new X500DistinguishedName($"CN={commonName}"), csr.PublicKey, HashAlgorithmName.SHA256);

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;

        var now = DateTimeOffset.UtcNow;

        using var certificate = request.Create(_ca, now.AddMinutes(-1), now + (ttl ?? TimeSpan.FromDays(30)), serial);

        // Vault's spelling: lower-case hex pairs separated by colons.
        var serialNumber = string.Join(":", serial.Select(b => b.ToString("x2")));

        Signed[serialNumber] = commonName;

        return Task.FromResult(new SignedCertificateResult(certificate.ExportCertificatePem(), serialNumber, CaPem, []));
    }

    public Task RevokeCertificateAsync(string serialNumber)
    {
        Revocations.Enqueue(serialNumber);
        return Task.CompletedTask;
    }

    public Task<bool> IsCertificateRevokedAsync(X509Certificate2 certificate, X509Certificate2 issuerCertificate)
        => Task.FromResult(Revocations.Any(serial =>
            string.Equals(serial.Replace(":", ""), certificate.SerialNumber, StringComparison.OrdinalIgnoreCase)));

    public Task<string> GetCaCertificateAsync() => Task.FromResult(CaPem);
}
