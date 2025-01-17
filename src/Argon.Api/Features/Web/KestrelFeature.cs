namespace Argon.Features.Web;

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

public static class KestrelFeature
{
    public static void ConfigureDefaultKestrel(this WebApplicationBuilder builder)
        => builder.WebHost.ConfigureKestrel(options =>
        {
            #if DEBUG
            options.ListenAnyIP(5001, listenOptions =>
            {
                listenOptions.UseHttps(GenerateManualCertificate());
                listenOptions.UseConnectionLogging();
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
            });
            #endif
            options.Limits.KeepAliveTimeout                  = TimeSpan.FromSeconds(400);
            options.AddServerHeader                          = false;
            options.Limits.Http2.MaxStreamsPerConnection     = 100;
            options.Limits.Http2.InitialConnectionWindowSize = 65535;
            options.Limits.Http2.KeepAlivePingDelay          = TimeSpan.FromSeconds(30);
            options.Limits.Http2.KeepAlivePingTimeout        = TimeSpan.FromSeconds(5);
        });


    static X509Certificate2 GenerateManualCertificate()
    {
        X509Certificate2 cert  = null;
        var              store = new X509Store("KestrelWebTransportCertificates", StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Count > 0)
        {
            cert = store.Certificates[^1];

            // rotate key after it expires
            if (DateTime.Parse(cert.GetExpirationDateString(), null) < DateTimeOffset.UtcNow)
            {
                cert = null;
            }
        }
        if (cert == null)
        {
            // generate a new cert
            var                           now        = DateTimeOffset.UtcNow;
            SubjectAlternativeNameBuilder sanBuilder = new();
            sanBuilder.AddDnsName("localhost");
            using var          ec  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest req = new("CN=localhost", ec, HashAlgorithmName.SHA256);
            // Adds purpose
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1") // serverAuth
            }, false));
            // Adds usage
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            // Adds subject alternate names
            req.CertificateExtensions.Add(sanBuilder.Build());
            // Sign
            using var crt = req.CreateSelfSigned(now, now.AddDays(14)); // 14 days is the max duration of a certificate for this
            cert = new(crt.Export(X509ContentType.Pfx));

            // Save
            store.Add(cert);
        }
        store.Close();

        var hash    = SHA256.HashData(cert.RawData);
        var certStr = Convert.ToBase64String(hash);
        Console.WriteLine($"\n\n\n\n\nCertificate: {certStr}\n\n\n\n"); // <-- you will need to put this output into the JS API call to allow the connection
        return cert;
    }
}