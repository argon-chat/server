namespace Argon.Api.Clustering;

using Argon.Core.Features.Integrations.Xsolla;
using Argon.Features.AccountConsole;
using Argon.Features.Clustering.Regions;
using Argon.Features.Logging;
using Argon.Features.Sentry;
using Argon.Features.k8s;
using Argon.Features.Vault;
using Argon.HealthChecks;
using Argon.Services;
using global::Sentry.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;

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
            // Sentry.AspNetCore has already bound the `Sentry` section into these options by the
            // time this runs, so everything the SDK understands is configurable from
            // appsettings.json under its own name whether or not ArgonSentryOptions models it.
            // What follows is only what Argon has an opinion about, and each assignment is the same
            // section read through the validated class — so an appsettings value survives it rather
            // than being overwritten by a default.

            // The connection string is where this lived before options existed; keeping it as the
            // fallback means an existing deployment does not have to move it to switch over.
            o.Dsn                 = options.Dsn ?? ctx.Configuration.GetConnectionString("Sentry");
            o.Debug               = options.Debug;
            o.AutoSessionTracking = options.AutoSessionTracking;
            o.TracesSampleRate    = options.TracesSampleRate;
            o.ProfilesSampleRate  = options.ProfilesSampleRate;
            o.SampleRate          = (float)options.SampleRate;
            o.SendDefaultPii      = options.SendDefaultPii;
            o.AttachStacktrace    = options.AttachStacktrace;
            o.MaxBreadcrumbs      = options.MaxBreadcrumbs;
            o.DiagnosticLogger    = new TraceDiagnosticLogger(SentryLevel.Debug);

            if (!string.IsNullOrWhiteSpace(options.Environment))
                o.Environment = options.Environment;

            // Which build, not just which deployment. Left to configuration when it is set, because
            // a self-hosted instance may want to name its own; otherwise the running version, which
            // is what makes an event point at a commit.
            o.Release = string.IsNullOrWhiteSpace(options.Release)
                ? $"argon@{GlobalVersion.FullSemVer}+{GlobalVersion.ShortSha}"
                : options.Release;

            // WHICH ROLE RAISED IT.
            //
            // Every role runs from one image and reports to one project, so without this an event
            // says "argon" and nothing more — and the roles fail differently enough that the first
            // question about any event is which one it came from. A tag rather than a context
            // because Sentry indexes tags: this is meant to be searched and grouped by.
            o.DefaultTags["argon.role"] = ctx.Role.Id.Value;

            // Both are opt-in in the SDK. Metrics additionally need the bridge below, which has
            // nothing to send them through unless this is on.
            o.EnableLogs    = options.EnableLogs;
            o.EnableMetrics = options.Metrics.Enabled;
        });

        if (options.Metrics.Enabled)
            ctx.Services.AddHostedService<SentryMeterBridge>();
    }
}

public sealed class VaultFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("secret material for everything below").Options<VaultOptions>("Vault");

    public void Configure(ArgonFeatureContext ctx)
    {
        var options = ctx.Options<VaultOptions>();

        ctx.Builder.AddVaultClient(options);

        // Only where a client exists. A deployment with no Vault has nothing here to probe, and
        // reporting its absence as a failure would keep every such role out of service over a
        // feature it does not use.
        if (Argon.Features.Vault.VaultFeature.ResolveAuthMode(options) is not VaultAuthMode.None)
            ctx.Services.AddDependencyCheck<VaultHealthCheck>(DependencyNames.Vault);
    }
}

public sealed class CacheFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("L1/L2 cache over Redis")
            .Requires<VaultFeature>()
            .Options<RedisProfilesOptions>("Redis");

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddArgonCacheDatabase();

        ctx.Services.AddSingleton<RedisProbeConnections>();
        ctx.Services.AddDependencyCheck<RedisHealthCheck>(DependencyNames.Redis);
    }
}

public sealed class DatabaseFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("pooled ApplicationDbContext")
            .Requires<VaultFeature>()
            .Options<DatabaseOptions>("Database")
            .Options<DatabaseRegionOptions>("Database:Regions");

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.AddPooledDatabase<ApplicationDbContext>(ctx.Options<DatabaseOptions>());

        ctx.Services.AddDependencyCheck<DatabaseHealthCheck>(DependencyNames.Database);
    }
}

public sealed class MessagePipeFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Describing("in-process pub/sub");

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddMessagePipe();
}

/// <summary>
/// What the Kubernetes probes gate on beyond the process itself.
/// </summary>
/// <remarks>
/// <para>Its own feature because two features map the probes — one per kind of role — and one
/// section cannot have two owners. Both lifecycle features require it, which is what puts it on
/// every role, and the probe endpoints they map read its options when they run.</para>
///
/// <para>The one thing it registers is the check that repeats the configuration validator's warnings
/// on <c>/health</c>. The dependency checks themselves are registered by the features that own the
/// dependency, so a role probes exactly what it uses.</para>
/// </remarks>
public sealed class ProbesFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("probes")
           .Describing("what the Kubernetes probes gate on beyond the process itself")
           .Options<ProbeOptions>(ProbeOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Services.AddHealthChecks()
           .AddCheck<ConfigurationHealthCheck>(ConfigurationHealthCheck.Name,
                failureStatus: HealthStatus.Unhealthy,
                tags: ["diagnostic", ConfigurationHealthCheck.Name]);
}

/// <summary>
/// How Kubernetes starts, probes, drains and stops a silo.
/// </summary>
/// <remarks>
/// <para>One feature rather than two because the probes and the pre-stop hook are one mechanism: the
/// hook drains, the drain flips readiness, and Kubernetes takes the pod out of the service on the
/// strength of that. Registering one without the other gives a silo that either cannot be told to
/// drain or cannot say that it has.</para>
///
/// <para>Silo roles only. A client role holds no activations, so there is nothing to hand over before
/// it stops — removing it from the service endpoints is the whole of its drain, and Kubernetes does
/// that on its own.</para>
///
/// <para>Until this existed the endpoints were written and never mapped: <c>AddSiloHealthChecks</c>
/// registered the checks, <c>MapSiloHealthChecks</c> was called from nowhere, and every probe would
/// have answered 404.</para>
/// </remarks>
public sealed class SiloLifecycleFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("silo-lifecycle")
           .Describing("Kubernetes probes and the pre-stop drain")
           .Requires<ProbesFeature>();

    public void Map(ArgonEndpointContext ctx)
    {
        ctx.App.MapSiloHealthChecks();
        ctx.App.UsePreStopHook();
    }
}

/// <summary>
/// How Kubernetes starts, probes and stops a client role.
/// </summary>
/// <remarks>
/// <para>The counterpart to <see cref="SiloLifecycleFeature"/>, and it exists because the reason for
/// not having one was wrong. A client role holds no activations — true — but it holds every client
/// websocket, and an entry point attaches an <c>IUserSessionGrain</c> to each connection. "Removing
/// it from the service endpoints is the whole of its drain" is only true if something tells
/// Kubernetes to remove it, and nothing did: <c>AddHealthChecks</c> was reached from the silo path
/// alone, so a client role had no readiness probe to fail, no liveness probe to pass, and a pre-stop
/// hook that found no drain service and stopped the process on the spot.</para>
///
/// <para>Same three probes, same paths, answered from the only thing a client knows about the
/// cluster — its own connection — plus the pre-stop wait that turns readiness off before it goes.</para>
///
/// <para><c>HostHooksFeature</c> is required rather than assumed. It is what maps the pre-stop
/// endpoint on a client role and it owns the wait's length, so probes without it would advertise a
/// graceful stop that nothing can trigger.</para>
/// </remarks>
public sealed class ClientLifecycleFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("client-lifecycle")
           .Describing("Kubernetes probes and the pre-stop wait for a client role")
           .Requires<HostHooksFeature>()
           .Requires<ProbesFeature>();

    public void Map(ArgonEndpointContext ctx)
    {
        // dev is every role in one process and is a silo, so it comes by this feature through the
        // client roles it includes. Its probes are the silo's, which answer the same questions with
        // the membership table behind them; mapping these as well would put two endpoints on one path.
        if (ctx.IsSilo)
            return;

        ctx.App.MapClientHealthChecks();
    }
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
            .Options<DeviceMatchingOptions>(DeviceMatchingOptions.SectionName)
            .Options<ClientAppsOptions>(ClientAppsOptions.SectionName)
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
            .Options<XsollaOptions>("Xsolla")
            .Controller<XsollaWebHookController>();

    public void Configure(ArgonFeatureContext ctx)
        => ctx.Builder.AddXsollaFeature();
}

/// <summary>
/// One Orleans client per configured region, kept connected in the background, and the channel a
/// region says things about itself on.
/// </summary>
/// <remarks>
/// <para>Inert until <c>Argon:Regions</c> names more than one region: with no peers the registry
/// holds no clients, starts no tasks and answers every lookup with the local cluster. That is what
/// makes it safe to give every role — the alternative is a feature only some roles have, and a
/// routing decision that therefore cannot be taken in the others.</para>
///
/// <para>The announcement half is not inert, and should not be: a drain is a property of the region,
/// so every process of it has to hear the declaration whether or not there is a second region to
/// route to. Otherwise "is this region draining" would answer differently depending on which pod was
/// asked. It costs one core-NATS subscription per process, on a bus every role is already connected
/// to.</para>
/// </remarks>
public sealed class RegionRegistryFeature : IArgonFeature
{
    public static void Describe(IFeatureDescriptor d)
        => d.Named("regions")
            .Describing("cluster clients for the other regions, and the region's own voice")
            .Options<ArgonRegionOptions>(ArgonRegionOptions.SectionName);

    public void Configure(ArgonFeatureContext ctx)
    {
        ctx.Builder.Services.AddSingleton<IRegionIntentChannel, NatsRegionIntentChannel>();
        ctx.Builder.Services.AddSingleton<IRegionIntents, RegionIntents>();
        ctx.Builder.Services.AddHostedService<RegionIntentAnnouncer>();

        ctx.Builder.Services.AddSingleton<ArgonRegionRegistry>();
        ctx.Builder.Services.AddSingleton<IArgonRegionRegistry>(sp => sp.GetRequiredService<ArgonRegionRegistry>());
        ctx.Builder.Services.AddHostedService(sp => sp.GetRequiredService<ArgonRegionRegistry>());
    }
}
