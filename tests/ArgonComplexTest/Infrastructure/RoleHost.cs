namespace ArgonComplexTest.Infrastructure;

using Argon.Core.Features.Integrations.Xsolla;
using Argon.Features.Clustering;
using Argon.Features.EF;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Boots the server as one named role against the shared containers.
/// </summary>
/// <remarks>
/// Distinct from <see cref="ArgonServerTargetHost"/>, which boots the one co-hosted host the
/// functional suite talks to. This one exists to answer a narrower question — does this role start
/// at all — so each instance gets its own silo port and cluster id and can be disposed immediately.
/// <para>
/// No Vault container: <c>AddVaultClient</c> resolves <c>VaultAuthMode.None</c> when nothing is
/// configured and registers no client, which is the path every non-Vault deployment already takes.
/// </para>
/// </remarks>
public sealed class RoleHost(ArgonTestHostSettings settings, ArgonRoleId role, int siloPort, string clusterId)
    : WebApplicationFactory<Program>
{
    public ArgonRoleId Role => role;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // BotApiRegistration.MapBotApi resolves a scoped service from the root provider; the
        // functional host disables scope validation for the same reason.
        builder.UseDefaultServiceProvider(o => o.ValidateScopes = false);

        builder.ConfigureAppConfiguration(configuration =>
        {
            configuration.AddInMemoryCollection(TestServerConfiguration.ReportSystem);
            configuration.AddInMemoryCollection(TestServerConfiguration.AccountDeletion);
            configuration.AddInMemoryCollection(TestServerConfiguration.Messages);
        });

        builder.ConfigureServices((_, services) =>
        {
            services.AddSingleton<FakeXsollaService>();
            services.AddSingleton<IXsollaService>(sp => sp.GetRequiredService<FakeXsollaService>());
        });

        if (TestEnvironmentOptions.ServerLogsEnabled)
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddSimpleConsole(o => o.SingleLine = true);
                logging.SetMinimumLevel(TestEnvironmentOptions.ServerLogLevel);
            });
        }

        builder.UseSetting(ArgonRoleHostExtensions.RoleConfigurationKey, role.Value);

        // UseSetting, not ConfigureAppConfiguration, and the difference matters: a feature that reads
        // its options through ArgonFeatureContext.Options<T>() binds them while the container is
        // still being built, which is before WebApplicationFactory has applied its configuration
        // callbacks. The identity server's limiter is one of those — it bakes the permit counts into
        // the policy at registration — so an in-memory collection would be ignored and the shipped
        // five-attempts-a-minute would 429 the fixture halfway through.
        foreach (var (key, value) in TestServerConfiguration.Aegis)
            builder.UseSetting(key, value);

        // Each role gets its own silo port and cluster, so several can be booted in one run without
        // fighting over 11111 or accidentally joining the functional suite's cluster.
        builder.UseSetting($"{ArgonClusterEndpoints.Section}:Id", clusterId);
        builder.UseSetting($"{ArgonClusterEndpoints.Section}:SiloPort", siloPort.ToString());
        builder.UseSetting($"{ArgonClusterEndpoints.Section}:GatewayPort", (siloPort + 1).ToString());

        builder.UseSetting("ConnectionStrings:cache", settings.RedisConnectionString);
        builder.UseSetting("ConnectionStrings:nats", settings.NatsConnectionString);
        builder.UseSetting("ConnectionStrings:Default", settings.DatabaseConnectionString);
        builder.UseSetting(DatabaseFeature.ProviderConfigurationKey, settings.DatabaseProvider.ToString());

        // Per-purpose Redis profiles, all on the single test container, separated by logical database.
        foreach (var (profile, db) in new[]
                 {
                     ("Cache", 0), ("HybridCache", 10), ("OrleansStorage", 7), ("Orleans", 1), ("Backplane", 2)
                 })
        {
            builder.UseSetting($"Redis:{profile}:ConnectionString", settings.RedisConnectionString);
            builder.UseSetting($"Redis:{profile}:Database", db.ToString());
        }

        builder.UseSetting("CallKit:Sfu:CommandUrl", "http://localhost:7880");
        builder.UseSetting("CallKit:Sfu:ClientId", "test-api-key");
        builder.UseSetting("CallKit:Sfu:Secret", "test-secret-key-that-is-long-enough-to-be-256-bits-minimum-for-livekit");
        builder.UseSetting("Xsolla:ProjectId", "1");
        builder.UseSetting("Xsolla:MerchantId", "1");
        builder.UseSetting("Xsolla:ApiKey", "test-key");
    }
}
