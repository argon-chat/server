namespace Argon.Features.Web;

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Core;

public static class KestrelFeature
{
    public static void ConfigureDefaultKestrel(this WebApplicationBuilder builder)
        => builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(5001, listenOptions =>
            {
                listenOptions.UseHttps(GenerateManualCertificate(builder));
                listenOptions.UseConnectionLogging();
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
            });
            options.AllowAlternateSchemes                    = true;
            options.Limits.KeepAliveTimeout                  = TimeSpan.FromSeconds(400);
            options.AddServerHeader                          = false;
            options.Limits.Http2.MaxStreamsPerConnection     = 100;
            options.Limits.Http2.InitialConnectionWindowSize = 65535;
            options.Limits.Http2.KeepAlivePingDelay          = TimeSpan.FromSeconds(30);
            options.Limits.Http2.KeepAlivePingTimeout        = TimeSpan.FromSeconds(5);
        });


    public static X509Certificate2 GenerateManualCertificate(WebApplicationBuilder builder)
    {
        X509Certificate2? cert  = null;
        using var         store = new X509Store("KestrelWebTransportCertificates", StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Count > 0)
        {
            cert = store.Certificates[^1];
            if (DateTime.Parse(cert.GetExpirationDateString(), null) < DateTimeOffset.UtcNow)
                cert = null;
        }

        if (cert == null)
        {
            var now = DateTimeOffset.UtcNow;

            SubjectAlternativeNameBuilder sanBuilder = new();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName("*.argon.gl");
            sanBuilder.AddDnsName("*.argon.zone");
            using var          ec  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest req = new("CN=localhost", ec, HashAlgorithmName.SHA256);
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            {
                new("1.3.6.1.5.5.7.3.1")
            }, false));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            req.CertificateExtensions.Add(sanBuilder.Build());
            using var crt = req.CreateSelfSigned(now, now.AddDays(14));
            cert = new(crt.Export(X509ContentType.Pfx));

            store.Add(cert);
        }

        var hash    = SHA256.HashData(cert.RawData);
        var certStr = Convert.ToBase64String(hash);
        builder.Configuration["Transport:CertificateFingerprint"] = certStr;
        return cert;
    }
}