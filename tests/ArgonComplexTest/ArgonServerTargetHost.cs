namespace ArgonComplexTest;

using Argon.Core.Features.Integrations.Xsolla;
using Argon.Features.Clustering;
using Argon.Features.EF;
using Argon.Features.Testing;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;

/// <param name="DatabaseProvider">
/// Decides whether the migration pipeline emits CockroachDB-only DDL. Getting this wrong against a
/// vanilla PostgreSQL container fails at the first <c>CREATE TABLE … WITH (ttl = 'on')</c>.
/// </param>
/// <param name="S3Endpoint">
/// Host and port of the object store, without a scheme — the shape <c>Storage:Endpoint</c> takes.
/// </param>
public sealed record ArgonTestHostSettings(
    string RedisConnectionString,
    string NatsConnectionString,
    string DatabaseConnectionString,
    DatabaseProviderKind DatabaseProvider,
    string S3Endpoint,
    string S3AccessKey,
    string S3SecretKey,
    string S3Bucket);

public class ArgonServerTargetHost(ArgonTestHostSettings settings) : WebApplicationFactory<Program>
{
    public ArgonTestHostSettings Settings => settings;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseDefaultServiceProvider(options =>
        {
            // BotApiRegistration.MapBotApi resolves scoped InteractionResponsePusher from root provider;
            // disable scope validation so the test host can start
            options.ValidateScopes = false;
        });

        builder.ConfigureAppConfiguration(configuration =>
        {
            // Whole subsystems that are inert unless configured. Supplied as configuration rather
            // than as service overrides so the production wiring — including the start-up option
            // validators — is what the tests actually exercise.
            configuration.AddInMemoryCollection(TestServerConfiguration.ReportSystem);
            configuration.AddInMemoryCollection(TestServerConfiguration.Messages);
        });

        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<FakeXsollaService>();
            services.AddSingleton<IXsollaService>(sp => sp.GetRequiredService<FakeXsollaService>());

            // Somewhere for outgoing mail to land. There is no SMTP server in the suite and every
            // test address is under .local, which resolves to nothing, so EmailManager drops each
            // message after a log line — and the whole user-visible output of a scheduled deletion,
            // a reminder, a cancellation and a finished export is exactly that message. Registered
            // here and nowhere else: EmailManager takes IEnumerable<IEmailSink>, which resolves to
            // an empty sequence on every shipped role, so nothing observes mail in production.
            services.AddSingleton<RecordingEmailSink>();
            services.AddSingleton<IEmailSink>(sp => sp.GetRequiredService<RecordingEmailSink>());

            // The one presence clock that is not ours. UserSessionGrain arms its grace as an Orleans
            // reminder, and Orleans refuses to register one below ReminderOptions.MinimumReminderPeriod
            // — a minute out of the box. PresenceFeature now applies Presence:ReminderFloor to that
            // option itself, so the MinimumReminderPeriod line below is belt-and-braces rather than
            // the thing that makes the compressed grace work; it stays because
            // PresenceHarnessSmokeTests.The_host_runs_on_the_compressed_presence_clocks reads the
            // value back and the two must agree whichever path set it.
            //
            // RefreshReminderListPeriod is five minutes by default: that is how long a silo may take
            // to notice a reminder some other silo wrote. One silo runs in this suite so it should
            // never matter, but a five-minute worst case inside a two-minute test budget is not worth
            // the ambiguity in a failure.
            services.Configure<ReminderOptions>(o =>
            {
                o.MinimumReminderPeriod     = TestPresenceTimings.ReminderFloor;
                o.RefreshReminderListPeriod = TimeSpan.FromSeconds(5);
            });
        });

        // Server-side logs are off by default — a full run would bury the test output — but an Ion
        // call that comes back as a bare "UPSTREAM_ERROR: Internal Server Error" is undiagnosable
        // without them. ARGON_TEST_LOGS=1 (optionally ARGON_TEST_LOG_LEVEL=Debug) turns them on.
        if (TestEnvironmentOptions.ServerLogsEnabled)
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole(o => o.SingleLine = true);
                logging.SetMinimumLevel(TestEnvironmentOptions.ServerLogLevel);
            });
        }

        // WebApplicationFactory invokes the entry point with no arguments, so the role is named
        // through configuration rather than --role.
        builder.UseSetting(ArgonRoleHostExtensions.RoleConfigurationKey, IntegrationTestRole.Id.Value);

        builder.UseSetting("ConnectionStrings:cache", settings.RedisConnectionString);
        builder.UseSetting("ConnectionStrings:nats", settings.NatsConnectionString);
        builder.UseSetting("ConnectionStrings:Default", settings.DatabaseConnectionString);
        builder.UseSetting(DatabaseFeature.ProviderConfigurationKey, settings.DatabaseProvider.ToString());

        // Per-purpose Redis profiles (all on the single test container, separated by logical database).
        foreach (var (profile, db) in new[]
                 {
                     ("Cache", 0), ("HybridCache", 10), ("OrleansStorage", 7), ("Orleans", 1), ("Backplane", 2)
                 })
        {
            builder.UseSetting($"Redis:{profile}:ConnectionString", settings.RedisConnectionString);
            builder.UseSetting($"Redis:{profile}:Database", db.ToString());
        }

        // Bot API rate limits exist to protect production from a runaway bot; in a test run they
        // just mean the twentieth assertion against /api/bot gets a 429 instead of an answer.
        // IEvents in particular allows five SSE connections a minute, and the streaming tests open
        // more than that. Raise every window rather than have tests sleep around the limiter —
        // the limiter itself is covered by its own dedicated tests.
        builder.UseSetting("BotApi:RateLimits:MaxConcurrency", "512");
        foreach (var botInterface in new[]
                 {
                     "IMessages", "IInteractions", "ICommands", "IChannels", "ISpaces",
                     "IMembers", "IVoice", "IBotSelf", "ICalls", "IVoiceEgress", "IEvents"
                 })
            builder.UseSetting($"BotApi:RateLimits:Interfaces:{botInterface}:PermitLimit", "100000");

        // Object storage. Configured through settings rather than by substituting a fake service, so
        // the presigned URL the client is handed is the one production would generate and the upload
        // is a real PUT against a real S3 — which is the half of this that has no other way of being
        // wrong in a way a test could see.
        builder.UseSetting("Storage:Endpoint", settings.S3Endpoint);
        builder.UseSetting("Storage:AccessKey", settings.S3AccessKey);
        builder.UseSetting("Storage:SecretKey", settings.S3SecretKey);
        builder.UseSetting("Storage:BucketName", settings.S3Bucket);
        builder.UseSetting("Storage:ExportBucketName", settings.S3Bucket);
        builder.UseSetting("Storage:Region", "us-east-1");

        // The CDN half. Configured because the redirect endpoint reads it, and a default-constructed
        // one sends a relative Location that the caller resolves against the API itself.
        builder.UseSetting("Storage:Cdn:PublicBaseUrl", "https://api.test.local");
        builder.UseSetting("Storage:Cdn:Default:BaseUrl", "https://cdn.test.local");
        builder.UseSetting("Storage:Cdn:RedirectCacheSeconds", "300");
        builder.UseSetting("Storage:UseSsl", "false");

        builder.UseSetting("CallKit:Sfu:CommandUrl", "http://localhost:7880");
        builder.UseSetting("CallKit:Sfu:ClientId", "test-api-key");
        builder.UseSetting("CallKit:Sfu:Secret", "test-secret-key-that-is-long-enough-to-be-256-bits-minimum-for-livekit");

        // Nothing answers at that URL; see RoleHost for why the SFU check is kept off the startup probe.
        builder.UseSetting("Probes:Dependencies:Overrides:sfu:Startup", "Degrade");

        // Presence timings. The product's are sized for a person on a laptop — a two-minute session
        // TTL, a fifteen-second tick, a one-minute grace — and every presence fixture asserts what
        // happens on the far side of one of them, so at shipped values the suite spends over twenty
        // minutes doing nothing but waiting. These are the same numbers an order of magnitude down,
        // and the fixtures derive every wait from them (PresenceProbe.Timings), so what is asserted is
        // unchanged: the ratios the assertions rest on — a tick fits several times in a TTL, the
        // grace outlasts the TTL, the activity outlives both — are the ones PresenceTimingOptions
        // validates, and they hold here too.
        foreach (var (setting, value) in TestPresenceTimings.Settings)
            builder.UseSetting(setting, value);

        // Account-lifecycle timings, on the same argument and by the same mechanism: a thirty-day
        // grace with reminders a week and a day out cannot be waited on by any test, and a
        // thirty-day export rate limit cannot be waited past at all. UseSetting rather than the
        // in-memory collection these used to arrive in — see TestServerConfiguration.AccountDeletion
        // for why, and note that the old collection set GracePeriodDays, which takes precedence over
        // the duration form and would pin the host back to thirty days if it were still applied.
        foreach (var (setting, value) in TestServerConfiguration.AccountDeletion)
            builder.UseSetting(setting, value);

        foreach (var (setting, value) in TestServerConfiguration.DataExport)
            builder.UseSetting(setting, value);

        builder.UseSetting("Xsolla:ProjectId", "1");
        builder.UseSetting("Xsolla:MerchantId", "1");
        builder.UseSetting("Xsolla:ApiKey", "test-key");
        builder.UseSetting("Xsolla:WebhookSecret", "test-secret");
        builder.UseSetting("Xsolla:IsSandbox", "true");
        builder.UseSetting("Xsolla:LoginProjectId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("Xsolla:ServerOAuthClientId", "1");
        builder.UseSetting("Xsolla:ServerOAuthClientSecret", "test-oauth-secret");
    }
}
