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

    public static void AddPooledDatabase<T>(this WebApplicationBuilder builder, DatabaseOptions database, string applicationName)
        where T : DbContext
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", false);
        DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Utc);

        var connection = BuildConnectionString(
            string.IsNullOrWhiteSpace(database.ConnectionString)
                ? builder.Configuration.GetConnectionString("Default")
                : database.ConnectionString,
            database,
            applicationName);

        var providerKind = builder.Configuration.GetDatabaseProviderKind();
        var development  = builder.Environment.IsDevelopment();
        builder.Services.AddSingleton(new DatabaseProvider(providerKind));

        // The other half of Job:Expiration. SchemaDeclarations makes CockroachDB honour it on the boot
        // path; TtlSweepGrain makes PostgreSQL honour it, since row-level TTL is syntax that engine
        // does not have. Registered unconditionally rather than behind an engine check, because the
        // most useful thing this can say on CockroachDB is "not applicable, the database is doing it
        // itself" — and because this is also what gives the grain the TtlSweepState it takes in its
        // constructor, on exactly the roles that have an IDbContextFactory to sweep with.
        builder.Services.AddTtlSweepDiagnostics();

        builder.Services.AddPooledDbContextFactory<T>(
            (_, options) => ConfigureContext(options, connection.ConnectionString, database, providerKind, development),
            connection.MaxPoolSize);
    }

    public static void ConfigureContext(
        DbContextOptionsBuilder options,
        string                  connectionString,
        DatabaseOptions         database,
        DatabaseProviderKind    providerKind,
        bool                    development)
    {
        // Both put parameter values (e-mails, password digests, TOTP secrets) into logs and exception messages.
        if (development)
            options.EnableDetailedErrors().EnableSensitiveDataLogging();

        options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.UseNodaTime();
                npgsql.EnableRetryOnFailure(
                    maxRetryCount: database.MaxRetryCount,
                    maxRetryDelay: database.MaxRetryDelay,
                    errorCodesToAdd: ["40001"]);
                npgsql.MaxBatchSize(database.MaxBatchSize);
                npgsql.ConfigureDataSource(q => q.EnableDynamicJson().UseJsonNet());
            })
           .ReplaceService<IHistoryRepository, NoLockHistoryRepository>()
           .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
           .AddInterceptors(new TimeStampAndSoftDeleteInterceptor());

        options.UseArgonSchemaAnnotations();

        // The multiregional generator appends CockroachDB-only clauses to CREATE TABLE / CREATE
        // DATABASE. On vanilla PostgreSQL we keep Npgsql's stock generator, which simply ignores
        // the "Regional:*" / "Job:Expiration" / "Cockroach:*" annotations the model carries.
        if (providerKind is DatabaseProviderKind.CockroachDb)
            options.UseMultiregionalCompatibility();
    }

    /// <summary>
    /// Fills in the pool and timeout settings from <paramref name="database"/> wherever the connection
    /// string does not set them itself.
    /// </summary>
    public static NpgsqlConnectionStringBuilder BuildConnectionString(
        string? connectionString, DatabaseOptions database, string applicationName)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString);

        // Keys lists only what the string set, under Npgsql's canonical names; ContainsKey is true for any known keyword.
        var set = csb.Keys.Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!set.Contains("Application Name"))
            csb.ApplicationName = applicationName;

        // Clamped against whichever bound the string did set, so the pair stays valid.
        if (!set.Contains("Maximum Pool Size"))
            csb.MaxPoolSize = Math.Max(database.MaxPoolSize, csb.MinPoolSize);

        if (!set.Contains("Minimum Pool Size"))
            csb.MinPoolSize = Math.Min(database.MinPoolSize, csb.MaxPoolSize);

        if (!set.Contains("Connection Idle Lifetime"))
            csb.ConnectionIdleLifetime = (int)database.ConnectionIdleLifetime.TotalSeconds;

        if (!set.Contains("Connection Lifetime"))
            csb.ConnectionLifetime = (int)database.ConnectionLifetime.TotalSeconds;

        if (!set.Contains("Command Timeout"))
            csb.CommandTimeout = (int)database.CommandTimeout.TotalSeconds;

        if (!set.Contains("Max Auto Prepare"))
            csb.MaxAutoPrepare = database.MaxAutoPrepare;

        if (!set.Contains("Load Balance Hosts"))
            csb.LoadBalanceHosts = database.LoadBalanceHosts;

        if (database.StatementTimeout is { } statementTimeout &&
            csb.Options?.Contains("statement_timeout", StringComparison.OrdinalIgnoreCase) is not true)
        {
            var timeout = $"-c statement_timeout={(long)statementTimeout.TotalMilliseconds}";

            csb.Options = string.IsNullOrWhiteSpace(csb.Options) ? timeout : $"{csb.Options} {timeout}";
        }

        return csb;
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
