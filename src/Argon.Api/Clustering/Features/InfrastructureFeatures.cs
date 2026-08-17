namespace Argon.Api.Clustering;

using Argon.Features.AccountConsole;
using global::Sentry.Infrastructure;

public sealed class LoggingFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("Serilog and the structured-log pipeline").Before<RoutingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddLogging();

    public void Map(ArgonEndpointContext ctx)
    {
        if (Environment.GetEnvironmentVariable("NO_STRUCTURED_LOGS") is null)
            ctx.App.UseSerilogRequestLogging();
    }
}

public sealed class TelemetryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("OpenTelemetry traces and metrics").Requires<LoggingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddOtel();
}

public sealed class SentryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<LoggingFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.WebHost.UseSentry(o =>
        {
            o.Dsn                 = ctx.Configuration.GetConnectionString("Sentry");
            o.Debug               = true;
            o.AutoSessionTracking = true;
            o.TracesSampleRate    = 1.0;
            o.ProfilesSampleRate  = 1.0;
            o.DiagnosticLogger    = new TraceDiagnosticLogger(SentryLevel.Debug);
        });
}

public sealed class VaultFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("secret material for everything below");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddVaultClient();
}

public sealed class CacheFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("L1/L2 cache over Redis").Requires<VaultFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddArgonCacheDatabase();
}

public sealed class DatabaseFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("pooled ApplicationDbContext").Requires<VaultFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddPooledDatabase<ApplicationDbContext>();
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
        => d.Requires<VaultFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddJwt();
}

public sealed class ArgonAuthorizationFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("token issuing and session validation")
            .Requires<JwtFeature>()
            .Requires<CacheFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddArgonAuthorization();
}

public sealed class OperatorAuthFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("staff authentication for the admin console").Requires<JwtFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddOperatorAuth();
}

public sealed class AccountConsoleAuthFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("account-console-auth")
            .Describing("developer authentication for the account console");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.Configure<AccountConsoleAuthOptions>(
            ctx.Configuration.GetSection(AccountConsoleAuthOptions.SectionName));
}

public sealed class CaptchaFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Requires<HttpClientFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddCaptchaFeature();
}

public sealed class XsollaFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("payments").Requires<HttpClientFeature>().Requires<VaultFeature>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddXsollaFeature();
}
