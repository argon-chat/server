namespace Argon.Api.Clustering;

using Argon.Core.Features.Integrations.Xsolla;
using Argon.Features.AccountConsole;
using Argon.Features.Logging;
using Argon.Features.Sentry;
using Argon.Features.Vault;
using Argon.Services;
using global::Sentry.Infrastructure;

public sealed class LoggingFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("Serilog and the structured-log pipeline")
            .Before<RoutingFeature>()
            .Options<ArgonLoggingOptions>("Logging");

    public void Configure(ArgonFeatureContext ctx)
    {
        if (ctx.Options<ArgonLoggingOptions>().Structured)
            ctx.Builder.AddLogging();
    }

    public void Map(ArgonEndpointContext ctx)
    {
        if (ctx.Options<ArgonLoggingOptions>() is { Structured: true, RequestLogging: true })
            ctx.App.UseSerilogRequestLogging();
    }
}

public sealed class TelemetryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("OpenTelemetry traces and metrics")
            .Requires<LoggingFeature>()
            .Options<MetricsBasicAuthOptions>("Metrics:BasicAuth");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddOtel();
}

public sealed class SentryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<LoggingFeature>().Options<ArgonSentryOptions>("Sentry");

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<ArgonSentryOptions>();

        ctx.Builder.WebHost.UseSentry(o =>
        {
            // The connection string is where this lived before options existed; keeping it as the
            // fallback means an existing deployment does not have to move it to switch over.
            o.Dsn                 = options.Dsn ?? ctx.Configuration.GetConnectionString("Sentry");
            o.Debug               = options.Debug;
            o.AutoSessionTracking = options.AutoSessionTracking;
            o.TracesSampleRate    = options.TracesSampleRate;
            o.ProfilesSampleRate  = options.ProfilesSampleRate;
            o.DiagnosticLogger    = new TraceDiagnosticLogger(SentryLevel.Debug);
        });
    }
}

public sealed class VaultFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("secret material for everything below").Options<VaultOptions>("Vault");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddVaultClient(ctx.Options<VaultOptions>());
}

public sealed class CacheFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("L1/L2 cache over Redis")
            .Requires<VaultFeature>()
            .Options<RedisProfilesOptions>("Redis");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddArgonCacheDatabase();
}

public sealed class DatabaseFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("pooled ApplicationDbContext")
            .Requires<VaultFeature>()
            .Options<DatabaseOptions>("Database")
            .Options<DatabaseRegionOptions>("Database:Regions");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddPooledDatabase<ApplicationDbContext>(ctx.Options<DatabaseOptions>());
}

public sealed class MessagePipeFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("in-process pub/sub");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddMessagePipe();
}

public sealed class HttpClientFeature : IArgonFeature
{
    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddHttpClient();
}

public sealed class ServerTimingFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("Server-Timing response header").Before<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddServerTiming();

    public void Map(ArgonEndpointContext ctx)
        => ctx.App.UseServerTiming();
}

public sealed class JwtFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<VaultFeature>().Options<JwtOptions>("Jwt");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddJwt();
}

public sealed class ArgonAuthorizationFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("token issuing and session validation")
            .Requires<JwtFeature>()
            .Requires<CacheFeature>()
            .Options<ArgonAuthOptions>("auth")
            .Options<PasswordHashingOptions>("auth:passwordHashing")
            .Options<AnonymousRateLimitOptions>("auth:anonymousRateLimits")
            .Options<AndroidAttestationOptions>("attestation:android");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddArgonAuthorization();
}

public sealed class OperatorAuthFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("staff authentication for the admin console")
            .Requires<JwtFeature>()
            .Options<OperatorAuthOptions>(OperatorAuthOptions.SectionName)
            .Options<VaultPkiOptions>(VaultPkiOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddOperatorAuth();
}

public sealed class AccountConsoleAuthFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("account-console-auth")
            .Describing("developer authentication for the account console")
            .Options<AccountConsoleAuthOptions>(AccountConsoleAuthOptions.SectionName);

    // Nothing to register: the framework binds and validates what this feature declares.
}

public sealed class CaptchaFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<HttpClientFeature>().Options<CaptchaOptions>("Captcha");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddCaptchaFeature(ctx.Options<CaptchaOptions>().Kind);
}

public sealed class XsollaFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("payments")
            .Requires<HttpClientFeature>()
            .Requires<VaultFeature>()
            .Options<XsollaOptions>("Xsolla");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddXsollaFeature();
}
