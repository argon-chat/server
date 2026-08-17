namespace Argon.Api.Clustering;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography.X509Certificates;
using global::Sentry.Infrastructure;

public sealed class KestrelFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("listeners, TLS and HTTP/3");

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.WebHost.UseQuic();
        ctx.Builder.WebHost.ConfigureKestrel(options =>
        {
            options.ConfigureEndpointDefaults(lo => lo.UseConnectionLogging());

            var port = Environment.GetEnvironmentVariable("OVERRIDE_PORT") is { } p ? int.Parse(p) : 5002;

            if (Environment.GetEnvironmentVariable("USE_LOCALHOST_CERTS") is not null)
            {
                options.ListenAnyIP(port, listen =>
                {
                    listen.UseHttps(LoadLocalhostCertificate(ctx));
                    listen.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
                });
            }
            else if (File.Exists("/etc/tls/tls.crt") && File.Exists("/etc/tls/tls.key") &&
                     Environment.GetEnvironmentVariable("LEGACY_CERT_LOADING") is not null)
            {
                options.ListenAnyIP(port, listen =>
                {
                    listen.UseHttps(x => x.ServerCertificate =
                        X509Certificate2.CreateFromPemFile("/etc/tls/tls.crt", "/etc/tls/tls.key"));
                    listen.DisableAltSvcHeader = false;
                    listen.Protocols           = HttpProtocols.Http1AndHttp2AndHttp3;
                });
            }
        });
    }

    private static X509Certificate2 LoadLocalhostCertificate(ArgonFeatureContext ctx)
    {
        if (!File.Exists("localhost.pfx"))
            throw new InvalidOperationException(
                "USE_LOCALHOST_CERTS is set but localhost.pfx is missing; generate it with " +
                "'mkcert -pkcs12 -p12-file localhost.pfx localhost'");

        var certificate = X509CertificateLoader.LoadPkcs12FromFile("localhost.pfx", "changeit");

        // The transport layer pins against this fingerprint, so it has to be published back into
        // configuration before anything reads it.
        ctx.Configuration["Transport:CertificateFingerprint"] =
            Convert.ToBase64String(SHA256.HashData(certificate.RawData));

        return certificate;
    }
}

public sealed class RoutingFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("routing, CORS, authn, authz, rate limiting")
            .Requires<ArgonAuthorizationFeature>();

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddDefaultCors();
        ctx.Services.AddAuthorization();
        ctx.Services.AddAuthentication();
        ctx.Services.AddRateLimiter();
    }

    public void Map(ArgonEndpointContext ctx)
    {
        ctx.App.UseRouting();
        ctx.App.UseCors();
        ctx.App.UseAuthentication();
        ctx.App.UseAuthorization();
        ctx.App.UseRateLimiter();
    }
}

public sealed class ControllersFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("MVC controllers — webhooks and file storage")
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddControllers()
           .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()));

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapControllers();
}

public sealed class WebSocketsFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("websockets").After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddWebSockets(x =>
        {
            x.KeepAliveInterval = TimeSpan.FromMinutes(1);
            x.KeepAliveTimeout  = TimeSpan.MaxValue;
        });

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseWebSockets();
}

public sealed class RewritesFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddRewrites();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseRewrites();
}

public sealed class AppHubFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("SignalR event hub")
            .Requires<ArgonAuthorizationFeature>()
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddSignalRAppHub();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapHub<AppHub>("/w",
                options => options.Transports = HttpTransportType.ServerSentEvents
                                              | HttpTransportType.WebSockets
                                              | HttpTransportType.LongPolling)
           .RequireAuthorization(new AuthorizeAttribute
            {
                AuthenticationSchemes = "Ticket",
                Policy                = "ticket"
            });
}

public sealed class SentryTunnelFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<SentryFeature>().Before<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddSentryTunneling("sentry.argon.gl");

    public void Map(ArgonEndpointContext ctx)
    {
        ctx.App.UseSentryTunneling("/k");
        ctx.App.UseSentryTracing();
    }
}

public sealed class DiscoveryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("client bootstrap and CDN redirects").After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddDiscoveryFeature();

    public void Map(ArgonEndpointContext ctx)
    {
        ctx.App.MapDiscovery();
        ctx.App.MapCdnRedirect();
    }
}

public sealed class TemplateEngineFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("templates").Describing("Fluid templates for e-mail and web pages");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddTemplateEngine();
}

public sealed class HostHooksFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.After<RoutingFeature>();

    public void Map(ArgonEndpointContext ctx)
    {
        ctx.App.MapGet("/", () => new
        {
            version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
        });
        ctx.App.UsePreStopHook();
    }
}
