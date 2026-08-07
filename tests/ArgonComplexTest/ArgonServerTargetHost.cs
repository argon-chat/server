namespace ArgonComplexTest;

using Argon.Core.Features.Integrations.Xsolla;
using Argon.Features.EF;
using ArgonComplexTest.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <param name="DatabaseProvider">
/// Decides whether the migration pipeline emits CockroachDB-only DDL. Getting this wrong against a
/// vanilla PostgreSQL container fails at the first <c>CREATE TABLE … WITH (ttl = 'on')</c>.
/// </param>
public sealed record ArgonTestHostSettings(
    string RedisConnectionString,
    string NatsConnectionString,
    string DatabaseConnectionString,
    DatabaseProviderKind DatabaseProvider);

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
            configuration.AddInMemoryCollection(TestServerConfiguration.AccountDeletion);
        });

        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<FakeXsollaService>();
            services.AddSingleton<IXsollaService>(sp => sp.GetRequiredService<FakeXsollaService>());
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

        builder.UseSetting("CallKit:Sfu:CommandUrl", "http://localhost:7880");
        builder.UseSetting("CallKit:Sfu:ClientId", "test-api-key");
        builder.UseSetting("CallKit:Sfu:Secret", "test-secret-key-that-is-long-enough-to-be-256-bits-minimum-for-livekit");

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
