namespace Argon.Features.Web;

using System.Security.Cryptography.X509Certificates;

/// <summary>
/// A PEM certificate and key read off disk, and read again once they change — for a listener whose
/// certificate is renewed underneath it, as a mounted Let's Encrypt secret is.
/// </summary>
/// <remarks>
/// Polled rather than watched: a Kubernetes secret volume is updated by swapping a symlink, which file
/// watchers do not reliably report. A renewal caught half-written fails to parse and is looked at again
/// on the next poll, while the certificate in hand keeps serving.
/// </remarks>
public sealed class ReloadingPemCertificate
{
    private readonly string   certificatePath;
    private readonly string   keyPath;
    private readonly long     checkEveryMs;
    private readonly ILogger? logger;
    private readonly Lock     gate = new();

    private X509Certificate2 current;
    private byte[]           fingerprint;
    private long             nextCheck;

    public ReloadingPemCertificate(string certificatePath, string keyPath, TimeSpan checkEvery, ILogger? logger = null)
    {
        this.certificatePath = certificatePath;
        this.keyPath         = keyPath;
        this.logger          = logger;
        checkEveryMs         = (long)checkEvery.TotalMilliseconds;

        var (certificatePem, keyPem) = Read();
        current     = Create(certificatePem, keyPem);
        fingerprint = Fingerprint(certificatePem, keyPem);
        nextCheck   = Environment.TickCount64 + checkEveryMs;
    }

    /// <summary>The certificate to present now.</summary>
    public X509Certificate2 Current
    {
        get
        {
            if (Environment.TickCount64 < Volatile.Read(ref nextCheck))
                return current;

            lock (gate)
            {
                if (Environment.TickCount64 < nextCheck)
                    return current;

                Refresh();
                Volatile.Write(ref nextCheck, Environment.TickCount64 + checkEveryMs);
                return current;
            }
        }
    }

    private void Refresh()
    {
        try
        {
            var (certificatePem, keyPem) = Read();
            var changed = Fingerprint(certificatePem, keyPem);

            if (changed.AsSpan().SequenceEqual(fingerprint))
                return;

            // The previous one is not disposed: a handshake may still be presenting it.
            current     = Create(certificatePem, keyPem);
            fingerprint = changed;

            logger?.LogInformation("Reloaded the certificate at {Path}, now valid until {NotAfter:O}",
                certificatePath, current.NotAfter);
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Could not reload the certificate at {Path}; keeping the one valid until {NotAfter:O}",
                certificatePath, current.NotAfter);
        }
    }

    private (string Certificate, string Key) Read()
        => (File.ReadAllText(certificatePath), File.ReadAllText(keyPath));

    private static byte[] Fingerprint(string certificatePem, string keyPem)
        => SHA256.HashData(Encoding.UTF8.GetBytes(certificatePem + "\n" + keyPem));

    private static X509Certificate2 Create(string certificatePem, string keyPem)
    {
        var certificate = X509Certificate2.CreateFromPem(certificatePem, keyPem);

        // SChannel cannot use the ephemeral key a PEM load produces.
        if (!OperatingSystem.IsWindows())
            return certificate;

        using (certificate)
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null);
    }
}
