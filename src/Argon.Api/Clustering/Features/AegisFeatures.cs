namespace Argon.Api.Clustering;

using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Argon.Api.Features.Aegis;
using Argon.Features.Aegis;
using Argon.Features.Middlewares;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using OpenIddict.Abstractions;
using OpenIddict.Server;

/// <summary>
/// Believing the proxy in front about where a request came from.
/// </summary>
public sealed class ForwardedHeadersFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("forwarded-headers")
            .Describing("trusts X-Forwarded-* from the known proxy hops")
            .Before<RoutingFeature>()
            .Options<ArgonForwardedHeadersOptions>(ArgonForwardedHeadersOptions.SectionName);

    public void Map(ArgonEndpointContext ctx)
    {
        var options = ctx.Options<ArgonForwardedHeadersOptions>();

        ctx.App.UseConfiguredForwardedHeaders(options.KnownNetworks, options.KnownProxies);
    }
}

/// <summary>
/// The browser session the sign-in widget keeps while walking a user through a flow.
/// </summary>
/// <remarks>
/// Data protection comes with it and is not optional: the keys are what encrypt the cookie, so every
/// replica of the role has to agree on the ring or a user is signed out depending on which node they
/// land on. The application name is what they agree by.
/// </remarks>
public sealed class AegisSessionFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("aegis-session")
            .Describing("cookie session for the sign-in widget")
            .Requires<RoutingFeature>()
            .Options<AegisSessionOptions>(AegisSessionOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<AegisSessionOptions>();

        ctx.Services.AddDataProtection().SetApplicationName(options.DataProtectionApplicationName);

        // Cookies as the default scheme, because the authentication middleware fills HttpContext.User
        // from the default and the whole widget flow reads it. Nothing else in the product relies on
        // a default — every other scheme is named at the point it is required — so declaring one here
        // does not change what the roles beside it do under the co-hosted development role.
        ctx.Services.AddAuthentication(AegisSession.Scheme)
           .AddCookie(AegisSession.Scheme, cookie =>
            {
                cookie.Cookie.Name = options.CookieName;

                if (!string.IsNullOrWhiteSpace(options.CookieDomain))
                    cookie.Cookie.Domain = options.CookieDomain;

                // Not configurable, and the three go together: the widget is framed by sites that are
                // not ours, so the cookie has to cross sites; a cookie that crosses sites must be
                // Secure, and a session cookie has no business being readable from script.
                cookie.Cookie.HttpOnly     = true;
                cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                cookie.Cookie.SameSite     = SameSiteMode.None;

                cookie.ExpireTimeSpan    = options.Lifetime;
                cookie.SlidingExpiration = true;
            });

        ctx.Services.AddHttpContextAccessor();
        ctx.Services.AddScoped<AegisSession>();
    }
}

/// <summary>
/// What one address may do to the identity server per minute.
/// </summary>
/// <remarks>
/// Two named policies the endpoints carry themselves, plus a global partition by remote address.
/// <c>AddRateLimiter</c> is additive, so this contributes policies to the limiter
/// <see cref="RoutingFeature"/> already registered rather than replacing it.
/// </remarks>
public sealed class AegisRateLimitFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("aegis-rate-limits")
            .Describing("per-address limits on the credential and token endpoints")
            .Requires<RoutingFeature>()
            .Options<AegisRateLimitOptions>(AegisRateLimitOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<AegisRateLimitOptions>();

        ctx.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            Fixed(AegisRateLimitOptions.AuthPolicy, options.Auth);
            Fixed(AegisRateLimitOptions.TokenPolicy, options.Token);

            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
                RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = options.Global.Permits,
                        Window      = options.Global.Window
                    }));

            void Fixed(string name, RateLimitWindow window)
                => limiter.AddFixedWindowLimiter(name, o =>
                {
                    o.PermitLimit = window.Permits;
                    o.Window      = window.Window;

                    // No queue: a caller over the limit is told so now rather than held open, which
                    // is what stops a burst of guesses from also being a way to tie up connections.
                    o.QueueLimit = 0;
                });
        });
    }
}

/// <summary>
/// Staff step-up over mutual TLS.
/// </summary>
/// <remarks>
/// TLS terminates at the proxy, so the client certificate arrives as a header and is turned back
/// into a certificate here. Both encodings the proxy may send are accepted, because which one it is
/// depends on how the proxy was configured rather than on anything this end controls.
/// </remarks>
public sealed class OperatorMutualTlsFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("operator-mtls")
            .Describing("hardware-key step-up for staff, over a mutual-TLS route")
            .Requires<CacheFeature>()
            .Before<RoutingFeature>()
            .Options<OperatorMutualTlsOptions>(OperatorMutualTlsOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<OperatorMutualTlsOptions>();

        ctx.Services.AddSingleton<IOperatorVerificationStore, OperatorVerificationStore>();

        ctx.Services.AddCertificateForwarding(forwarding =>
        {
            forwarding.CertificateHeader = options.CertificateHeader;
            forwarding.HeaderConverter   = ReadCertificate;
        });
    }

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseCertificateForwarding();

    /// <summary>
    /// URL-encoded, and PEM or bare base64 DER depending on the proxy's <c>passTLSClientCert</c>
    /// settings.
    /// </summary>
    private static X509Certificate2 ReadCertificate(string header)
    {
        var decoded = Uri.UnescapeDataString(header);

        if (decoded.Contains("-----BEGIN CERTIFICATE-----"))
            return X509Certificate2.CreateFromPem(decoded);

        return X509CertificateLoader.LoadCertificate(
            Convert.FromBase64String(decoded.Replace("\n", "").Replace("\r", "").Replace(" ", "").Trim()));
    }
}

/// <summary>
/// The OpenID Connect provider itself.
/// </summary>
/// <remarks>
/// Degraded mode: OpenIddict keeps no store of its own, and every question it would have asked a
/// store — does this client exist, is this redirect its own, may it have these scopes — is answered
/// by the handlers registered here against the applications a developer registered in the console.
/// Two databases of applications would be one too many.
/// <para>
/// The keys are placeholders at this point. Real ones come from Vault, which cannot be reached while
/// the container is still being built, so <see cref="AegisSigningKeys"/> swaps them in afterwards.
/// </para>
/// </remarks>
public sealed class OpenIdProviderFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("openid")
            .Describing("the OAuth 2.0 / OpenID Connect provider")
            .Requires<JwtFeature>()
            .Requires<AegisSessionFeature>()
            .Requires<ControllersFeature>()
            .After<RoutingFeature>()
            .Options<OpenIdProviderOptions>(OpenIdProviderOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<OpenIdProviderOptions>();

        ctx.Services.AddOpenIddict()
           .AddServer(server =>
            {
                server
                   .SetAuthorizationEndpointUris(options.AuthorizationEndpoint)
                   .SetTokenEndpointUris(options.TokenEndpoint)
                   .SetUserInfoEndpointUris(options.UserInfoEndpoint)
                   .AllowClientCredentialsFlow()
                   .AllowTokenExchangeFlow()
                   .AllowAuthorizationCodeFlow()
                   .AllowRefreshTokenFlow()
                   .EnableDegradedMode()
                   .RegisterScopes([.. options.Scopes, OpenIddictConstants.Scopes.OfflineAccess]);

                if (options.RequireProofKeyForCodeExchange)
                    server.RequireProofKeyForCodeExchange();

                server.UseAspNetCore()
                   .EnableAuthorizationEndpointPassthrough()
                   .EnableTokenEndpointPassthrough();

                server.AddEventHandler<OpenIddictServerEvents.ValidateAuthorizationRequestContext>(
                    b => b.UseScopedHandler<ValidateAuthorizationHandler>());
                server.AddEventHandler<OpenIddictServerEvents.ValidateTokenRequestContext>(
                    b => b.UseScopedHandler<ValidateTokenHandler>());
                server.AddEventHandler<OpenIddictServerEvents.HandleUserInfoRequestContext>(
                    b => b.UseScopedHandler<UserInfoHandler>());

                server.RegisterClaims(
                    OpenIddictConstants.Claims.Name,
                    OpenIddictConstants.Claims.Email,
                    OpenIddictConstants.Claims.EmailVerified,
                    OpenIddictConstants.Claims.PreferredUsername,
                    AegisClaims.Roles,
                    AegisClaims.OperatorId,
                    AegisClaims.AuthenticationMethod);

                // Access tokens are verified by resource servers as ordinary signed JWTs. Encrypting
                // them would make the claims unreadable to every one of them.
                if (!options.EncryptAccessTokens)
                    server.DisableAccessTokenEncryption();

                server.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
            })
           .AddValidation(validation =>
            {
                validation.UseAspNetCore();
                validation.UseLocalServer();
            });

        ctx.Services.AddSingleton<IPostConfigureOptions<OpenIddictServerOptions>, AegisSigningKeys>();
    }

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.MapAegisTokenEndpoint(ctx.Options<OpenIdProviderOptions>().TokenEndpoint);
}

/// <summary>
/// The identity server's own surface: host pinning, security headers, dynamic CORS, and the widget.
/// </summary>
/// <remarks>
/// CORS is the interesting part. Argon's own hosts are a fixed list, but every other allowed origin
/// is a site somebody registered a redirect for, and that answer lives in the database. ASP.NET's
/// origin predicate is synchronous and cannot go and ask, so the registered policy accepts every
/// origin and the real decision is made by a middleware that runs before it — which strips the
/// header back off, and refuses the preflight outright, when the answer is no.
/// </remarks>
public sealed class AegisFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("aegis")
            .Describing("the identity server: OAuth widget, consent, operator step-up")
            .Requires<OpenIdProviderFeature>()
            .Requires<OperatorMutualTlsFeature>()
            .Requires<AegisRateLimitFeature>()
            .Requires<ForwardedHeadersFeature>()
            .Requires<CacheFeature>()
            .After<RoutingFeature>()
            .Options<AegisOptions>(AegisOptions.SectionName)
            .GrainRoots(g =>
            {
                g.AddCallRoot<AegisDirectory>();
                g.AddCallRoot<AuthController>();
                g.AddCallRoot<OperatorAuthController>();
                g.AddCallRoot<EmailSendController>();
                g.AddCallRoot<UserInfoHandler>();
            });

    /// <summary>
    /// Whether this process is the identity server rather than a composite that merely contains it.
    /// </summary>
    /// <remarks>
    /// Two of the decisions below are about the whole HTTP pipeline, not about these endpoints, so
    /// they are only the identity server's to make when it <i>is</i> the process. Under the
    /// co-hosted development role it shares a pipeline with the entry point, and taking the pipeline
    /// over there would break the roles beside it — see each guard for what it would break.
    /// </remarks>
    private static bool OwnsThePipeline(RoleDescriptor role)
        => role.Id == ArgonRoleId.Aegis;

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Services.AddScoped<IAegisDirectory, AegisDirectory>();
        ctx.Services.AddScoped<AegisCorsPolicy>();

        if (!OwnsThePipeline(ctx.Role))
            return;

        // MVC would otherwise map every controller in the assembly onto this role, including the
        // entry point's webhook and file endpoints — a surface the identity server never asked for,
        // backed by services it does not register. Restricting it in a composite would unmap the
        // entry point's own controllers instead, which is the opposite of the intent.
        //
        // The grain-graph scanner does not see this narrowing: it mirrors AddControllers' convention
        // over every ControllerBase in the assembly, so --explain aegis over-reports the interfaces
        // this role can reach. Over-reporting is the safe direction — it can only ask for a grain to
        // be hosted that need not be — but the list is wider than what actually runs.
        ctx.Services.AddControllers().RestrictTo(typeof(AuthController).Namespace!);

        // Every origin is accepted here and the real check happens in Map, because the answer needs
        // a round trip and this predicate cannot await one. In a composite the static policy that
        // RoutingFeature registered stands instead, so an application's own site is not a permitted
        // origin there — a difference worth knowing before debugging a local consent screen.
        ctx.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
           .SetIsOriginAllowed(_ => true)
           .AllowAnyHeader()
           .AllowAnyMethod()
           .AllowCredentials()
           .WithExposedHeaders("X-Wt-Upgrade", "X-Wt-Fingerprint", "X-Wt-AAT")
           .SetPreflightMaxAge(TimeSpan.FromDays(1))));
    }

    public void Map(ArgonEndpointContext ctx)
    {
        var options = ctx.Options<AegisOptions>();

        if (!OwnsThePipeline(ctx.Role))
            return;

        if (!string.IsNullOrWhiteSpace(options.Host))
            ctx.App.Use(async (http, next) =>
            {
                // Pinned rather than validated: the issuer, the audiences and the redirects this
                // server emits are all built from the host, so a forged header would have it mint
                // links pointing at the forger.
                http.Request.Host = new HostString(options.Host);
                await next();
            });

        if (options.SecurityHeaders)
            ctx.App.Use(async (http, next) =>
            {
                var headers = http.Response.Headers;

                headers["X-Content-Type-Options"]              = "nosniff";
                headers["X-Frame-Options"]                     = "DENY";
                headers["Referrer-Policy"]                     = "strict-origin-when-cross-origin";
                headers["X-Permitted-Cross-Domain-Policies"]   = "none";
                headers["Permissions-Policy"]                  = "camera=(), microphone=(), geolocation=()";

                if (!string.IsNullOrWhiteSpace(options.ContentSecurityPolicy) &&
                    !options.CspExcludedPaths.Any(path => http.Request.Path.StartsWithSegments(path)))
                    headers["Content-Security-Policy"] = options.ContentSecurityPolicy;

                await next();
            });

        ctx.App.UseHsts();

        ctx.App.Use(async (http, next) =>
        {
            var origin = http.Request.Headers.Origin.ToString();

            if (!string.IsNullOrEmpty(origin))
            {
                var policy = http.RequestServices.GetRequiredService<AegisCorsPolicy>();

                if (!await policy.IsAllowedAsync(origin, http.RequestAborted))
                {
                    http.Response.Headers.Remove("Access-Control-Allow-Origin");

                    if (HttpMethods.IsOptions(http.Request.Method))
                    {
                        http.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;
                    }
                }
            }

            await next();
        });

        if (string.IsNullOrWhiteSpace(options.StaticRoot))
            return;

        var files = new PhysicalFileProvider(Path.GetFullPath(options.StaticRoot));

        ctx.App.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider     = files,
            DefaultFileNames = ["index.html"]
        });

        ctx.App.UseStaticFiles(new StaticFileOptions
        {
            FileProvider          = files,
            ServeUnknownFileTypes = false,
            ContentTypeProvider   = new FileExtensionContentTypeProvider()
        });

        // The widget routes client-side, so a deep link has to be answered with the shell rather
        // than a 404.
        ctx.App.MapFallback(async http =>
        {
            http.Response.ContentType = "text/html";
            await http.Response.SendFileAsync(Path.Combine(Path.GetFullPath(options.StaticRoot), "index.html"));
        });
    }
}
