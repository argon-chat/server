namespace ArgonComplexTest.Infrastructure;

using Argon.Features.EF;
using Npgsql;
using Testcontainers.CockroachDb;
using Testcontainers.PostgreSql;

/// <summary>
/// A throwaway database the integration host talks to. Implementations hide the fact that
/// CockroachDB and PostgreSQL need different containers and different server-side feature flags,
/// so <see cref="ArgonTestEnvironment"/> only deals in a connection string plus a provider kind.
/// </summary>
public interface ITestDatabase : IAsyncDisposable
{
    TestDatabaseKind Kind { get; }

    /// <summary>Which <see cref="DatabaseProviderKind"/> the server must be configured with.</summary>
    DatabaseProviderKind ProviderKind { get; }

    /// <summary>Valid only after <see cref="StartAsync"/> has completed.</summary>
    string ConnectionString { get; }

    Task StartAsync(CancellationToken ct = default);
}

public static class TestDatabaseFactory
{
    public static ITestDatabase Create(TestDatabaseKind kind)
        => kind switch
        {
            TestDatabaseKind.Cockroach => new CockroachTestDatabase(),
            TestDatabaseKind.Postgres  => new PostgresTestDatabase(),
            _                          => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown test database kind")
        };
}

public sealed class PostgresTestDatabase : ITestDatabase
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(TestEnvironmentOptions.DatabaseImage)
       .WithDatabase("argon_test")
       .WithUsername("argon")
       .WithPassword("argon")
        // The bootstrap applies ~100 migrations before the first test runs; turning off durability
        // reclaims most of the wall-clock that would otherwise go into disk flushes. A container
        // that is thrown away at the end of the run has nothing to be crash-safe about.
       .WithCommand("-c", "fsync=off", "-c", "full_page_writes=off", "-c", "synchronous_commit=off")
       .WithReuse(TestEnvironmentOptions.ReuseContainers)
       .Build();

    public TestDatabaseKind     Kind         => TestDatabaseKind.Postgres;
    public DatabaseProviderKind ProviderKind => DatabaseProviderKind.PostgreSql;

    public string ConnectionString
        // Include Error Detail mirrors the production connection string so constraint violations
        // surface with the offending column instead of an opaque 23505.
        => $"{_container.GetConnectionString()};Include Error Detail=true";

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// A single CockroachDB node that knows which region it is in, talking to a database that has been
/// told the same name.
/// </summary>
/// <remarks>
/// <para>Both halves are load-bearing and neither is what the module gives you. CockroachDB derives
/// the set of valid region names from the <c>--locality</c> its nodes were started with, and a stock
/// <see cref="CockroachDbBuilder"/> starts <c>start-single-node --insecure</c> with no locality at
/// all; a database with no primary region rejects every <c>LOCALITY</c> clause besides. So without
/// this class doing both, <see cref="ArgonComplexTest.TablePlacementTests"/> could not issue the DDL
/// it exists to assert on — it failed on an unsupported statement instead of on the thing under test,
/// which is the least useful way for an acceptance test to be red.</para>
/// </remarks>
public sealed class CockroachTestDatabase : ITestDatabase
{
    // WithCommand appends to the module's own arguments rather than replacing them, so the node comes
    // up as `start-single-node --insecure --locality=region=...`. Do not "complete" this into a full
    // command line: that pins arguments the module is free to change between versions, and getting
    // them wrong shows up as a container that never passes its health check.
    private readonly CockroachDbContainer _container = new CockroachDbBuilder(TestEnvironmentOptions.DatabaseImage)
       .WithCommand($"--locality=region={TestEnvironmentOptions.DatabaseRegion}")
       .WithReuse(TestEnvironmentOptions.ReuseContainers)
       .Build();

    public TestDatabaseKind     Kind         => TestDatabaseKind.Cockroach;
    public DatabaseProviderKind ProviderKind => DatabaseProviderKind.CockroachDb;

    public string ConnectionString => $"{_container.GetConnectionString()};Include Error Detail=true";

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _container.StartAsync(ct);
        await SetPrimaryRegionAsync(ct);
    }

    /// <summary>
    /// Makes the container's database multi-region, before the server has opened it once.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in the server's warm-up, and the difference is production. The warm-up
    /// reaches the multi-region DDL only through <c>IRelationalDatabaseCreator.CreateAsync</c> — that
    /// is, only when the database is absent — and the container's database is never absent: the image
    /// creates it before the wait strategy passes, which is why that path had never once executed in a
    /// test. Widening the condition so the server also converts an <em>existing</em> database would put
    /// <c>ALTER DATABASE … SET PRIMARY REGION</c> on the boot path of every production pod, against a
    /// live cluster, to repair a condition only a test container has. The test arranges its own
    /// preconditions instead.</para>
    ///
    /// <para><c>SET PRIMARY REGION</c> is declarative, so re-running it against a container kept alive
    /// by <c>ARGON_TEST_REUSE_CONTAINERS</c> with the same region is a no-op and not an error.</para>
    ///
    /// <para>What this deliberately leaves uncovered is production's own first-boot statement,
    /// <c>CREATE DATABASE … PRIMARY REGION … SURVIVE …</c>. Naming a database the container has not
    /// created would exercise it, at the price of making every Cockroach-mode test in the suite depend
    /// on that one statement succeeding. That is a bet to take deliberately, not as a side effect of a
    /// placement fixture.</para>
    /// </remarks>
    private async Task SetPrimaryRegionAsync(CancellationToken ct)
    {
        var database = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()).Database;

        // Retried, because the module's wait strategy is an HTTP probe on the node's /health — which
        // answers before the SQL layer takes connections. The suite never noticed: its first query
        // came seconds later, once the host had finished building. This one runs the instant the
        // container reports up, and a race here would kill the whole run in global setup for a reason
        // that has nothing to do with any test. The cost of the crude "retry anything Npgsql throws"
        // is that a genuinely malformed statement takes the full budget before surfacing — worth it,
        // since that only happens to someone who has just edited the line below.
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync(ct);

                await using var command = connection.CreateCommand();
                // Quoted, and it must be: the region names carry a hyphen, which an unquoted
                // identifier cannot, and Cockroach folds unquoted identifiers to lower case besides.
                command.CommandText = $"""
                                      ALTER DATABASE "{database}" SET PRIMARY REGION "{TestEnvironmentOptions.DatabaseRegion}"
                                      """;
                await command.ExecuteNonQueryAsync(ct);
                return;
            }
            catch (NpgsqlException) when (attempt < 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
