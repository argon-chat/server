namespace Argon.Features.EF;

using Argon.Core.Features.EF;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Vault;

/// <summary>
/// Which flavour of the PostgreSQL wire protocol the server talks to.
/// <para>
/// Argon runs on CockroachDB in production, and the migration pipeline emits Cockroach-only DDL
/// (<c>LOCALITY</c>, row-level TTL jobs, <c>PRIMARY REGION</c>). Vanilla PostgreSQL understands none of
/// that, so the flavour also decides whether the Cockroach migrations SQL generator is installed.
/// </para>
/// </summary>
public enum DatabaseProviderKind
{
    /// <summary>CockroachDB — the production target. Emits multi-region and TTL DDL.</summary>
    CockroachDb,

    /// <summary>Vanilla PostgreSQL — used by tests and local development. Cockroach-only DDL is suppressed.</summary>
    PostgreSql
}

public static class DatabaseFeature
{
    public const string ProviderConfigurationKey = "Database:Provider";

    /// <summary>
    /// Reads <c>Database:Provider</c>. Unset/unparsable falls back to <see cref="DatabaseProviderKind.CockroachDb"/>
    /// so existing deployments keep their behaviour without a config change.
    /// </summary>
    public static DatabaseProviderKind GetDatabaseProviderKind(this IConfiguration configuration)
        => Enum.TryParse<DatabaseProviderKind>(configuration[ProviderConfigurationKey], ignoreCase: true, out var kind)
            ? kind
            : DatabaseProviderKind.CockroachDb;

    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder, DatabaseOptions database)
        where T : DbContext
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
        DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);
        builder.Services.AddSingleton<IVaultDbCredentialsProvider, VaultDbCredentialsProvider>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<IVaultDbCredentialsProvider>());

        var connectionString = string.IsNullOrWhiteSpace(database.ConnectionString)
            ? builder.Configuration.GetConnectionString("Default")
            : database.ConnectionString;

        var providerKind = builder.Configuration.GetDatabaseProviderKind();
        builder.Services.AddSingleton(new DatabaseProvider(providerKind));

        builder.Services.AddPooledDbContextFactory<T>((_, options) =>
        {
            options.EnableDetailedErrors()
               .EnableSensitiveDataLogging()
               .UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.UseNodaTime();
                    npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5,
                        maxRetryDelay: TimeSpan.FromSeconds(2),
                        errorCodesToAdd: ["40001"]);
                    npgsql.MaxBatchSize(50);
                    npgsql.ConfigureDataSource(q => q.EnableDynamicJson().UseJsonNet());
                    npgsql.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                })
               .ReplaceService<IHistoryRepository, NoLockHistoryRepository>()
               .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
               .AddInterceptors(new TimeStampAndSoftDeleteInterceptor());

            // The multiregional generator appends CockroachDB-only clauses to CREATE TABLE / CREATE
            // DATABASE. On vanilla PostgreSQL we keep Npgsql's stock generator, which simply ignores
            // the "Regional:*" / "Job:Expiration" annotations the model carries.
            if (providerKind is DatabaseProviderKind.CockroachDb)
                options.UseMultiregionalCompatibility();
        }, 512);
    }
}

/// <summary>Ambient marker of the database flavour the process is wired against.</summary>
public sealed record DatabaseProvider(DatabaseProviderKind Kind)
{
    public bool IsCockroach => Kind is DatabaseProviderKind.CockroachDb;
}

/// <summary>
/// Where the database's regional table placement puts data. Read once, by
/// <c>ApplicationDbContext.OnModelCreating</c>.
/// </summary>
/// <remarks>
/// Carried an <c>IsMultiregionalDisabled</c> flag that nothing ever read and no configuration ever
/// set; validating the <c>required</c> keyword is what made that visible, so it is gone.
/// </remarks>
public class DatabaseRegionOptions
{
    public required string PrimaryRegion { get; set; }

    public required string[] ReplicateRegion { get; set; }
}
