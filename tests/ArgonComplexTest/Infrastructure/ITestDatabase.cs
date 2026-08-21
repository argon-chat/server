namespace ArgonComplexTest.Infrastructure;

using Argon.Features.EF;
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

public sealed class CockroachTestDatabase : ITestDatabase
{
    private readonly CockroachDbContainer _container = new CockroachDbBuilder(TestEnvironmentOptions.DatabaseImage)
       .WithReuse(TestEnvironmentOptions.ReuseContainers)
       .Build();

    public TestDatabaseKind     Kind         => TestDatabaseKind.Cockroach;
    public DatabaseProviderKind ProviderKind => DatabaseProviderKind.CockroachDb;

    public string ConnectionString => $"{_container.GetConnectionString()};Include Error Detail=true";

    public Task StartAsync(CancellationToken ct = default) => _container.StartAsync(ct);

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
