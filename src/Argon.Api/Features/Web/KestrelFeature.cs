namespace Argon.Features.Web;

using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

public static class KestrelFeature
{
    public static void ConfigureDefaultKestrel(this WebApplicationBuilder builder)
        => builder.WebHost.UseKestrel(options =>
        {
            options.ListenLocalhost(5001, listenOptions =>
            {
                listenOptions.UseHttps(LoadLocalhostCerts(builder));
                listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
            });
        });

    private static X509Certificate2 LoadLocalhostCerts(WebApplicationBuilder builder)
    {
        var cert = X509CertificateLoader.LoadPkcs12FromFile("localhost.pfx", "changeit");

        var hash    = SHA256.HashData(cert.RawData);
        var certStr = Convert.ToBase64String(hash);

        builder.Configuration["Transport:CertificateFingerprint"] = certStr;

        return cert;
    }
}