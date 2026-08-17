namespace Argon.Api.Clustering;

using Argon.Features.Sentry;
using Argon.Features.Web;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Security.Cryptography.X509Certificates;

public sealed class KestrelFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("listeners, TLS and HTTP/3").Options<ArgonKestrelOptions>("Kestrel:Argon");

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<ArgonKestrelOptions>();

        ctx.Builder.WebHost.UseQuic();
        ctx.Builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ConfigureEndpointDefaults(lo => lo.UseConnectionLogging());

            var tls = options.UseLocalhostCertificate ||
                      (options.UseFileCertificate &&
                       File.Exists(options.CertificatePath) && File.Exists(options.CertificateKeyPath));

            // Nothing to say: no port and no certificate means ASPNETCORE_URLS decides, which is what
            // the container image and the test host both rely on.
            if (options.Port is null && !tls)
                return;

            kestrel.ListenAnyIP(options.Port ?? ArgonKestrelOptions.DefaultTlsPort, listen =>
            {
                if (!tls)
                    return;

                if (options.UseLocalhostCertificate)
                    listen.UseHttps(LoadLocalhostCertificate(ctx, options));
                else
                    listen.UseHttps(https => https.ServerCertificate =
                        X509Certificate2.CreateFromPemFile(options.CertificatePath, options.CertificateKeyPath));

                listen.DisableAltSvcHeader = false;
                listen.Protocols           = HttpProtocols.Http1AndHttp2AndHttp3;
            });
        });
    }

    private static X509Certificate2 LoadLocalhostCertificate(ArgonFeatureContext ctx, ArgonKestrelOptions options)
    {
        if (!File.Exists(options.LocalhostCertificatePath))
            throw new InvalidOperationException(
                $"kestrel:argon:useLocalhostCertificate is set but '{options.LocalhostCertificatePath}' is missing; " +
                $"generate it with 'mkcert -pkcs12 -p12-file {options.LocalhostCertificatePath} localhost'");

        var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            options.LocalhostCertificatePath, options.LocalhostCertificatePassword);

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
            .Requires<ArgonAuthorizationFeature>()
            .Options<ArgonCorsOptions>("Cors");

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddDefaultCors(ctx.Options<ArgonCorsOptions>().AllowedOrigins);
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
        => d.Named("websockets").After<RoutingFeature>().Options<ArgonWebSocketOptions>("WebSockets");

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<ArgonWebSocketOptions>();

        ctx.Services.AddWebSockets(x =>
        {
            x.KeepAliveInterval = options.KeepAliveInterval;
            x.KeepAliveTimeout  = options.KeepAliveTimeout;
        });
    }

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseWebSockets();
}

public sealed class RewritesFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.After<RoutingFeature>().Options<RewriteMiddlewareOptions>("Rewriter");

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseRewrites();
}

/// <summary>
/// The realtime bus every role publishes events onto.
/// </summary>
/// <remarks>
/// A silo needs this and does not need <see cref="AppHubFeature"/>: six grain classes take
/// <c>AppHubServer</c>, and publishing goes through the Redis backplane to whichever node holds the
/// client's connection. Accepting connections is the endpoint's job, not the bus's.
/// </remarks>
public sealed class RealtimeBusFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("realtime-bus")
            .Describing("SignalR over the Redis backplane, and the replay log behind it")
            .Requires<CacheFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddRealtimeBus();
}

public sealed class AppHubFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("the client-facing end of the realtime bus")
            .Requires<RealtimeBusFeature>()
            .Requires<ArgonAuthorizationFeature>()
            .After<RoutingFeature>()
            .Options<AppHubOptions>("AppHub");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddAppHubEndpoint();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapHub<AppHub>(ctx.Options<AppHubOptions>().Path,
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

    // The tunnel is configured from the same block as Sentry itself, and SentryFeature owns it.
    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddSentryTunneling(ctx.OptionsOf<SentryFeature, ArgonSentryOptions>().TunnelHost);

    public void Map(ArgonEndpointContext ctx)
    {
        var options = ctx.App.Services.GetRequiredService<IOptions<ArgonSentryOptions>>().Value;

        ctx.App.UseSentryTunneling(options.TunnelPath);
        ctx.App.UseSentryTracing();
    }
}

public sealed class DiscoveryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("client bootstrap and CDN redirects")
            .After<RoutingFeature>()
            .Options<DiscoveryOptions>(DiscoveryOptions.SectionName);

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
        => d.Named("templates")
            .Describing("Fluid templates for e-mail and web pages")
            .Options<SmtpConfig>("Smtp");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddTemplateEngine();
}

public sealed class HostHooksFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.After<RoutingFeature>().Options<HostHooksOptions>("HostHooks");

    public void Map(ArgonEndpointContext ctx)
    {
        var options = ctx.Options<HostHooksOptions>();

        if (options.ExposeVersion)
            ctx.App.MapGet("/", () => new
            {
                version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
            });

        if (options.PreStopHook)
            ctx.App.UsePreStopHook();
    }
}
