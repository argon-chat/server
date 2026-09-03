namespace Argon.Api.Clustering;

using Argon.Features.AccountConsole;
using Argon.Features.Aegis;
using Argon.Features.Sentry;
using Argon.Features.Storage;
using Argon.Features.Web;
using Argon.Features.WebSession;
using Microsoft.AspNetCore.Mvc.Controllers;
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

/// <summary>
/// MVC, carrying only the controllers the role's own features claimed.
/// </summary>
/// <remarks>
/// <para><b>The filtering is the point.</b> <c>AddControllers()</c> discovers every
/// <see cref="ControllerBase"/> in the loaded assemblies, which is one assembly graph for the whole
/// product — so every role that mapped controllers served all of them. The identity server's
/// <c>api/auth</c>, <c>api/users</c> and <c>api/email</c> answered on the entrypoint role, which has
/// no Aegis feature and therefore none of the services those controllers are built from: each
/// request reached routing, failed to activate, and came back a 500 from a URL that was never meant
/// to be there.</para>
///
/// <para>Which controller belongs to which feature is now written down at the feature —
/// <c>Controller&lt;T&gt;()</c> — and a controller no feature claims is refused by the graph rather
/// than silently dropped, so this cannot turn one quiet failure into another.</para>
/// </remarks>
public sealed class ControllersFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("MVC controllers claimed by the role's features")
            .After<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
    {
        var claimed = ctx.Role.Features.Ordered
           .SelectMany(feature => feature.Controllers)
           .ToHashSet();

        ctx.Services.AddControllers()
           .AddNewtonsoftJson(x => x.SerializerSettings.Converters.Add(new StringEnumConverter()))
           .ConfigureApplicationPartManager(parts =>
            {
                // Replaced rather than added to: the providers are additive, so leaving the default
                // in place would have it contribute the full set alongside ours and change nothing.
                foreach (var provider in parts.FeatureProviders.OfType<ControllerFeatureProvider>().ToArray())
                    parts.FeatureProviders.Remove(provider);

                parts.FeatureProviders.Add(new ClaimedControllerFeatureProvider(claimed));
            });
    }

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapControllers();
}

/// <summary>
/// Admits a controller only if a feature of this role claimed it.
/// </summary>
/// <remarks>
/// Still defers to the base rule for what <i>is</i> a controller — the naming convention, the
/// attributes, the exclusions. This narrows the set; it does not redefine the category.
/// </remarks>
internal sealed class ClaimedControllerFeatureProvider(IReadOnlySet<Type> claimed) : ControllerFeatureProvider
{
    protected override bool IsController(TypeInfo typeInfo)
        => claimed.Contains(typeInfo.AsType()) && base.IsController(typeInfo);
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
    /// <summary>
    /// Any origin at all, which is the point of a tunnel.
    /// </summary>
    /// <remarks>
    /// <para>The default policy is an allowlist of <c>argon.gl</c> and its subdomains, and it carries
    /// credentials. Neither fits here. What posts to this path is the error reporter of whatever page
    /// happens to be running — the official client, a self-hosted one, a developer on
    /// <c>https://localhost:5005</c> — and the request is an opaque envelope forwarded to Sentry. It
    /// reads nothing, returns nothing, and needs no cookie, so there is nothing for an origin to
    /// abuse by being allowed.</para>
    ///
    /// <para>Separate from <c>DiscoveryFeature.OpenPublicPolicy</c>, which is otherwise the same
    /// idea, because that one permits GET and OPTIONS and this is a POST.</para>
    ///
    /// <para><c>AllowAnyOrigin</c> and <c>AllowCredentials</c> are mutually exclusive in ASP.NET, so
    /// this policy cannot be folded into the default one even if the allowlist were widened.</para>
    /// </remarks>
    public const string OpenTunnelPolicy = "SentryTunnelOpen";

    public static void Describe(IFeatureDescriptor d)
        => d.Requires<SentryFeature>().Before<RoutingFeature>();

    // The tunnel is configured from the same block as Sentry itself, and SentryFeature owns it.
    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Services.AddSentryTunneling(ctx.OptionsOf<SentryFeature, ArgonSentryOptions>().TunnelHost);

        ctx.Services.AddCors(o => o.AddPolicy(OpenTunnelPolicy, p =>
            p.AllowAnyOrigin()
             .AllowAnyHeader()
             .WithMethods("POST", "OPTIONS")));
    }

    public void Map(ArgonEndpointContext ctx)
    {
        var options = ctx.App.Services.GetRequiredService<IOptions<ArgonSentryOptions>>().Value;

        // CORS FOR THIS PATH ONLY, AND AHEAD OF THE TUNNEL.
        //
        // This feature maps Before<RoutingFeature>, so the tunnel middleware already ran by the time
        // RoutingFeature's UseCors is reached — and the tunnel answers and stops. That is why a
        // browser saw `200 OK` with no `Access-Control-Allow-Origin` on it and blocked the response
        // anyway: nothing had put the header there, and a 200 the browser refuses to hand to the page
        // is indistinguishable from a network failure in the console.
        //
        // Branched on the path rather than placed in front of everything, because a second CORS
        // middleware covering the whole pipeline would apply this permissive policy to every request
        // and then collide with the credentialed default one downstream. The branch rejoins the main
        // pipeline, so the tunnel below still sees the request.
        //
        // The preflight was never the broken half. Sentry's own tunnel middleware answers OPTIONS on
        // this path already — measured: `200` with the full CORS set on it, where every other path
        // gives this origin a bare `204`. It just does nothing for the POST that follows, which is
        // the one the page has to read. After this branch the preflight is answered here instead,
        // one middleware earlier and as a `204` without `Access-Control-Allow-Credentials`; the
        // tunnel never sees it. That costs nothing, because a policy built on AllowAnyOrigin could
        // not honour credentials anyway.
        ctx.App.UseWhen(
            http => http.Request.Path.StartsWithSegments(options.TunnelPath),
            branch => branch.UseCors(OpenTunnelPolicy));

        ctx.App.UseSentryTunneling(options.TunnelPath);
        ctx.App.UseSentryTracing();
    }
}

/// <summary>
/// The cookie session the first-party web client signs in with.
/// </summary>
/// <remarks>
/// <para>Registered on the role that serves the web client and nowhere else. It adds one way in —
/// an audience-gated exchange of an Aegis token for an Argon session — and changes nothing about how
/// any other OAuth application is authenticated: those keep their own tokens and their own
/// interceptors, and no path here widens what they can reach.</para>
///
/// <para>After <c>RoutingFeature</c> because both endpoints are ordinary routed, CORS-covered
/// requests; the browser calls them cross-origin from the web client with credentials, which the
/// default policy already permits for <c>argon.gl</c>.</para>
/// </remarks>
public sealed class WebSessionFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("web-session")
            .Describing("cookie sessions for the first-party web client")
            .Requires<JwtFeature>()
            .Requires<CacheFeature>()
            .After<RoutingFeature>()
            .Options<WebSessionOptions>(WebSessionOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddWebSession();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapWebSession();
}

public sealed class DiscoveryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("client bootstrap")
            .After<RoutingFeature>()
            .Options<DiscoveryOptions>(DiscoveryOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddDiscoveryFeature();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapDiscovery();
}

/// <summary>
/// The public address of a file, and the object-storage settings that describe where files live.
/// </summary>
/// <remarks>
/// <para><b>Its own feature because the endpoint and the settings have to travel together.</b> The
/// redirect used to be mapped by <c>DiscoveryFeature</c>, which declared only its own section — so on
/// a role that did not also run the storage feature the handler read a default-constructed
/// <c>StorageOptions</c>: no regional origins, so it answered with the bare object key as a relative
/// <c>Location</c> that callers resolved against the API and got a 404, and no cache window, so every
/// image paid for the round trip again. The process started, stayed ready, and logged nothing.</para>
///
/// <para>Owning the section rather than declaring it twice: one section has one owner, or a
/// <c>conf.d</c> file has no unambiguous home. <c>FileStorageFeature</c> requires this one and reads
/// the same settings, which is how a silo that stores files and an entry point that only addresses
/// them agree about where they are without the entry point taking on a database and S3 credentials
/// it has no use for.</para>
/// </remarks>
public sealed class CdnFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("cdn")
            .Describing("public file addresses and the storage settings behind them")
            .After<RoutingFeature>()
            .Options<StorageOptions>(StorageOptions.SectionName)
            .Options<FileLimitsOptions>(FileLimitsOptions.SectionName)

            // The client-facing half of the same surface: this feature publishes where a file can be
            // read from, and `/api/files` is how one gets there in the first place. It belongs with
            // the public addresses rather than with `file-storage`, which is the storage grain and
            // its S3 client — those live on the roles that hold the credentials, and this controller
            // holds none: it takes a cluster client and asks the grain, wherever that grain runs.
            .Controller<FileStorageController>();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapCdnRedirect();
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

        if (options.ExposeVersion && !ServesASiteAtRoot(ctx))
            ctx.App.MapGet("/", () => new
            {
                version = $"{GlobalVersion.FullSemVer}.{GlobalVersion.ShortSha}"
            });

        if (options.PreStopHook)
            ctx.App.UsePreStopHook();
    }

    /// <summary>
    /// Whether something else on this role answers <c>/</c> with a page.
    /// </summary>
    /// <remarks>
    /// <para>An endpoint mapped here would win, and silently. <c>UseStaticFiles</c> and the SPA
    /// fallback both stand down once routing has already selected an endpoint, and a literal
    /// <c>/</c> route outranks the fallback's catch-all — so the identity server would answer its
    /// own front door with a version document while every deep link into the widget rendered
    /// normally. That is a confusing shape of broken, and the version is still one <c>GET</c> away
    /// on every other role.</para>
    ///
    /// <para>Two roles serve a site: the identity server the sign-in widget, the account role the
    /// developer console. Both do it only when a static root is configured, and the identity server
    /// only when it is the whole process — under the co-hosted development role <c>AegisFeature</c>
    /// maps no static files at all, so <c>/</c> is free there and keeps reporting the build.</para>
    /// </remarks>
    private static bool ServesASiteAtRoot(ArgonEndpointContext ctx)
    {
        if (ctx.Role.Id == ArgonRoleId.Aegis)
            return Configured(ctx.App.Services.GetService<IOptions<AegisOptions>>()?.Value.StaticRoot);

        if (ctx.Role.Id == ArgonRoleId.Account)
            return Configured(ctx.App.Services.GetService<IOptions<AccountConsoleOptions>>()?.Value.StaticRoot);

        return false;

        static bool Configured(string? root) => !string.IsNullOrWhiteSpace(root);
    }
}
