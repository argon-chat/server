namespace ArgonSharedLogicTest;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Argon.Features.Web;

/// <summary>
/// The WebTransport listener's certificate, which Let's Encrypt renews underneath a running process.
/// </summary>
[TestFixture]
public class ReloadingPemCertificateTests
{
    [Test]
    public void A_renewed_certificate_is_presented_once_it_is_on_disk()
    {
        using var pem = new PemFiles();

        var first  = pem.Write("CN=first.argon.test");
        var source = new ReloadingPemCertificate(pem.Certificate, pem.Key, TimeSpan.Zero);

        Assert.That(source.Current.Thumbprint, Is.EqualTo(first));
        Assert.That(source.Current.HasPrivateKey, Is.True, "a certificate without its key cannot serve TLS");

        var second = pem.Write("CN=second.argon.test");

        Assert.That(source.Current.Thumbprint, Is.EqualTo(second));
    }

    [Test]
    public void A_half_written_renewal_keeps_the_certificate_in_hand()
    {
        using var pem = new PemFiles();

        var first  = pem.Write("CN=first.argon.test");
        var source = new ReloadingPemCertificate(pem.Certificate, pem.Key, TimeSpan.Zero);

        File.WriteAllText(pem.Certificate, "-----BEGIN CERTIFICATE-----\nnot yet");

        Assert.That(source.Current.Thumbprint, Is.EqualTo(first));
    }

    [Test]
    public void The_files_are_not_read_again_before_the_interval()
    {
        using var pem = new PemFiles();

        var first  = pem.Write("CN=first.argon.test");
        var source = new ReloadingPemCertificate(pem.Certificate, pem.Key, TimeSpan.FromHours(1));

        pem.Write("CN=second.argon.test");

        Assert.That(source.Current.Thumbprint, Is.EqualTo(first));
    }

    private sealed class PemFiles : IDisposable
    {
        private readonly string directory = Directory.CreateTempSubdirectory("argon-pem-").FullName;

        public string Certificate => Path.Combine(directory, "tls.crt");
        public string Key         => Path.Combine(directory, "tls.key");

        /// <returns>The thumbprint of the certificate written.</returns>
        public string Write(string subject)
        {
            using var key         = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var       request     = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

            File.WriteAllText(Certificate, certificate.ExportCertificatePem());
            File.WriteAllText(Key, key.ExportPkcs8PrivateKeyPem());

            return certificate.Thumbprint;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
