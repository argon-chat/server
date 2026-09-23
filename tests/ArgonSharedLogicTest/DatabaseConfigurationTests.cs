namespace ArgonSharedLogicTest;

using Argon.Api.Clustering;
using Argon.Core.Features.EF;
using Argon.Entities;
using Argon.Features.Clustering;
using Argon.Features.EF;
using Argon.Features.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using DatabaseFeature = Argon.Features.EF.DatabaseFeature;

/// <summary>
/// What <c>AddPooledDatabase</c> hands Npgsql and EF, checked without a database.
/// </summary>
[TestFixture]
public class DatabaseConfigurationTests
{
    private const string Host = "Host=localhost;Database=argon";

    private static NpgsqlConnectionStringBuilder Build(string connectionString, DatabaseOptions? options = null)
        => DatabaseFeature.BuildConnectionString(connectionString, options ?? new DatabaseOptions(), "argon-core");

    [Test]
    public void The_options_fill_in_what_the_connection_string_leaves_out()
    {
        var built = Build(Host);

        Assert.Multiple(() =>
        {
            Assert.That(built.ApplicationName, Is.EqualTo("argon-core"));
            Assert.That(built.MaxPoolSize, Is.EqualTo(100));
            Assert.That(built.MinPoolSize, Is.EqualTo(2));
            Assert.That(built.ConnectionIdleLifetime, Is.EqualTo(300));
            Assert.That(built.ConnectionLifetime, Is.EqualTo(1800));
            Assert.That(built.CommandTimeout, Is.EqualTo(30));
            Assert.That(built.MaxAutoPrepare, Is.Zero);
            Assert.That(built.LoadBalanceHosts, Is.False);
            Assert.That(built.Multiplexing, Is.False);
            Assert.That(built.Options, Is.Null.Or.Empty, "no statement_timeout unless one is configured");
        });
    }

    [Test]
    public void The_connection_string_wins_under_any_spelling_of_the_keyword()
    {
        var built = Build(Host + ";Application Name=custom;MaxPoolSize=7;minimum pool size=1;ConnectionIdleLifetime=15;" +
                          "Connection Lifetime=60;CommandTimeout=5;Max Auto Prepare=20;Load Balance Hosts=true");

        Assert.Multiple(() =>
        {
            Assert.That(built.ApplicationName, Is.EqualTo("custom"));
            Assert.That(built.MaxPoolSize, Is.EqualTo(7));
            Assert.That(built.MinPoolSize, Is.EqualTo(1));
            Assert.That(built.ConnectionIdleLifetime, Is.EqualTo(15));
            Assert.That(built.ConnectionLifetime, Is.EqualTo(60));
            Assert.That(built.CommandTimeout, Is.EqualTo(5));
            Assert.That(built.MaxAutoPrepare, Is.EqualTo(20));
            Assert.That(built.LoadBalanceHosts, Is.True);
        });
    }

    [Test]
    public void A_pool_bound_from_the_connection_string_keeps_the_pair_valid()
    {
        Assert.Multiple(() =>
        {
            var small = Build(Host + ";Maximum Pool Size=1");
            Assert.That((small.MinPoolSize, small.MaxPoolSize), Is.EqualTo((1, 1)));

            var large = Build(Host + ";Minimum Pool Size=150");
            Assert.That((large.MinPoolSize, large.MaxPoolSize), Is.EqualTo((150, 150)));
        });
    }

    [Test]
    public void The_statement_timeout_joins_the_options_the_connection_string_already_has()
    {
        var options = new DatabaseOptions { StatementTimeout = TimeSpan.FromSeconds(20) };

        Assert.Multiple(() =>
        {
            Assert.That(Build(Host, options).Options, Is.EqualTo("-c statement_timeout=20000"));
            Assert.That(Build(Host + ";Options=-c search_path=argon", options).Options,
                Is.EqualTo("-c search_path=argon -c statement_timeout=20000"));
            Assert.That(Build(Host + ";Options=-c statement_timeout=5000", options).Options,
                Is.EqualTo("-c statement_timeout=5000"));
        });
    }

    [Test]
    public void Parameter_values_reach_logs_only_in_development()
    {
        Assert.Multiple(() =>
        {
            var production = Configure(development: false).FindExtension<CoreOptionsExtension>()!;
            Assert.That(production.IsSensitiveDataLoggingEnabled, Is.False);
            Assert.That(production.DetailedErrorsEnabled, Is.False);

            var development = Configure(development: true).FindExtension<CoreOptionsExtension>()!;
            Assert.That(development.IsSensitiveDataLoggingEnabled, Is.True);
            Assert.That(development.DetailedErrorsEnabled, Is.True);
        });
    }

    [Test]
    public void A_full_message_write_batch_is_one_round_trip()
    {
        var relational = RelationalOptionsExtension.Extract(Configure(development: false));

        Assert.Multiple(() =>
        {
            Assert.That(relational.MaxBatchSize, Is.EqualTo(256));
            Assert.That(relational.ExecutionStrategyFactory, Is.Not.Null, "retry on failure is configured");
        });
    }

    [Test]
    public void A_query_splits_only_when_it_asks_to()
    {
        var relational = RelationalOptionsExtension.Extract(Configure(development: false));

        // Unset rather than SingleQuery, so EF keeps warning about a query that loads two collections without choosing.
        Assert.That(relational.QuerySplittingBehavior, Is.Null);
    }

    [Test]
    public void The_shipped_connection_string_carries_no_error_detail()
    {
        var path = Path.Combine(TestContext.CurrentContext.TestDirectory, "appsettings.json");
        var shipped = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

        Assert.That(new NpgsqlConnectionStringBuilder(shipped.GetConnectionString("Default")).IncludeErrorDetail, Is.False,
            "error detail carries row values into exception messages and Sentry");
    }

    [Test]
    public async Task A_silo_role_without_a_pool_skips_the_warm_up()
    {
        var catalog = ArgonClusterCatalog.Build(new ClusterScanScope
        {
            Assemblies = [typeof(CoreRole).Assembly, typeof(IArgonRole).Assembly]
        });

        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(catalog.Require(ArgonRoleId.Moderation));

        await using var app = builder.Build();

        Assert.That(await app.WarmUp<ApplicationDbContext>(), Is.SameAs(app));
    }

    private static DbContextOptions Configure(bool development)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>();

        DatabaseFeature.ConfigureContext(options, Host, new DatabaseOptions(), DatabaseProviderKind.PostgreSql, development);

        return options.Options;
    }
}
