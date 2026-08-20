namespace ArgonComplexTest.Infrastructure;

using Argon.Features.EF;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
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
/// A three-node CockroachDB cluster, one node per region, talking to a database that has been told
/// all three names.
/// </summary>
/// <remarks>
/// <para>Three nodes rather than one, and the reason is that a single node cannot answer the question
/// this fixture exists for. CockroachDB derives the set of valid region names from the
/// <c>--locality</c> its nodes were started with, so a one-node cluster has exactly one region — and
/// the server's apply step deliberately leaves table placement alone below two, because
/// <c>LOCALITY GLOBAL</c> charges a commit-wait on every write and repays it only in cross-region
/// reads. On a one-node cluster the correct number of <c>LOCALITY</c> statements is zero, which makes
/// the placement assertions unanswerable rather than merely red.</para>
///
/// <para>The topology is the one <c>deploy/docker-compose.local.yml</c> already describes — the same
/// three region names, the same join list — so a developer reading either recognises the other. The
/// stock <see cref="CockroachDbBuilder"/> cannot express it: it starts <c>start-single-node</c>, and
/// a joining node needs <c>start</c>, an advertised address its peers can resolve, and a cluster that
/// somebody has run <c>init</c> against.</para>
///
/// <para>It costs a slower start than one node did. That price is only paid by a run that asked for
/// CockroachDB, which is already the opt-in path — the suite defaults to PostgreSQL — and
/// <c>ARGON_TEST_REUSE_CONTAINERS</c> takes most of it back for anyone iterating.</para>
/// </remarks>
public sealed class CockroachTestDatabase : ITestDatabase
{
    private const int    Port     = 26257;
    private const string Database = "defaultdb";

    /// <summary>How long the region schema-change jobs get before the fixture gives up on them.</summary>
    private static readonly TimeSpan JobWait = TimeSpan.FromMinutes(2);

    private readonly INetwork network = new NetworkBuilder()
       .WithName($"argon-crdb-{Guid.NewGuid():N}")
       .Build();

    private readonly List<IContainer> nodes = [];

    public TestDatabaseKind     Kind         => TestDatabaseKind.Cockroach;
    public DatabaseProviderKind ProviderKind => DatabaseProviderKind.CockroachDb;

    public string ConnectionString
        => new NpgsqlConnectionStringBuilder
        {
            Host     = nodes[0].Hostname,
            Port     = nodes[0].GetMappedPublicPort(Port),
            Database = Database,
            Username = "root",
            SslMode  = SslMode.Disable,
            // Turns a unique violation into a message naming the offending column instead of an
            // opaque 23505.
            IncludeErrorDetail = true
        }.ConnectionString;

    /// <summary>
    /// One node, in its region. Index zero is the gateway the suite connects through.
    /// </summary>
    /// <remarks>
    /// <c>--advertise-addr</c> as well as <c>--listen-addr</c>: a node tells its peers where to reach
    /// it, and inside the network that is the alias rather than the address the host sees. Without it
    /// the three come up and never find each other, which surfaces as an init that hangs rather than
    /// as anything naming the cause.
    /// </remarks>
    private IContainer BuildNode(int index)
    {
        var alias = $"cockroach{index + 1}";
        var join  = string.Join(",", Enumerable
           .Range(1, TestEnvironmentOptions.DatabaseRegions.Length)
           .Select(n => $"cockroach{n}:{Port}"));

        var builder = new ContainerBuilder()
           .WithImage(TestEnvironmentOptions.DatabaseImage)
           .WithNetwork(network)
           .WithNetworkAliases(alias)
           .WithCommand(
                "start", "--insecure",
                $"--listen-addr={alias}:{Port}",
                $"--advertise-addr={alias}:{Port}",
                $"--join={join}",
                $"--locality=region={TestEnvironmentOptions.DatabaseRegions[index]}")
            // The port, not the HTTP health endpoint the module waits on: a node that has joined but
            // whose cluster has not been initialised answers neither SQL nor /health, and init is the
            // very next thing this class does. A listening socket is the most that can be true before
            // then.
           .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(Port))
           .WithReuse(TestEnvironmentOptions.ReuseContainers);

        return (index == 0 ? builder.WithPortBinding(Port, true) : builder).Build();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await network.CreateAsync(ct);

        for (var index = 0; index < TestEnvironmentOptions.DatabaseRegions.Length; index++)
            nodes.Add(BuildNode(index));

        // Concurrently, because each node blocks until it can reach its peers: starting them one at a
        // time means waiting out the first one's join timeout before the second one exists to join to.
        await Task.WhenAll(nodes.Select(node => node.StartAsync(ct)));

        // One node runs init, once, for the whole cluster. Until it does, every node is listening and
        // none of them will answer a query.
        await nodes[0].ExecAsync(
            ["/cockroach/cockroach", "init", "--insecure", $"--host=cockroach1:{Port}"], ct);

        await DeclareRegionsAsync(ct);
    }

    /// <summary>
    /// Makes the cluster's database multi-region, before the server has opened it once.
    /// </summary>
    /// <remarks>
    /// <para>Here rather than in the server's warm-up, and the difference is production. The warm-up
    /// reaches the multi-region DDL only through <c>IRelationalDatabaseCreator.CreateAsync</c> — that
    /// is, only when the database is absent — and this database is never absent. Widening that
    /// condition so the server also converted an <em>existing</em> database would put
    /// <c>ALTER DATABASE … SET PRIMARY REGION</c> on the boot path of every production pod, against a
    /// live cluster, to repair a condition only a test container has. The test arranges its own
    /// preconditions instead.</para>
    ///
    /// <para>Both statements are declarative — <c>SET PRIMARY REGION</c> to the region already set,
    /// and <c>ADD REGION IF NOT EXISTS</c> — so a container kept alive by
    /// <c>ARGON_TEST_REUSE_CONTAINERS</c> re-runs them as no-ops rather than as errors.</para>
    /// </remarks>
    private async Task DeclareRegionsAsync(CancellationToken ct)
    {
        // Retried, because init returning is not the SQL layer being ready, and a race here would kill
        // the whole run in global setup for a reason that has nothing to do with any test. The cost of
        // retrying anything Npgsql throws is that a genuinely malformed statement takes the full budget
        // before surfacing — worth it, since that only happens to somebody who has just edited this.
        for (var attempt = 1;; attempt++)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync(ct);

                await using var command = connection.CreateCommand();

                // Quoted, and they must be: the region names carry a hyphen, which an unquoted
                // identifier cannot, and Cockroach folds unquoted identifiers to lower case besides.
                command.CommandText =
                    $"""ALTER DATABASE "{Database}" SET PRIMARY REGION "{TestEnvironmentOptions.DatabaseRegion}";""";
                await command.ExecuteNonQueryAsync(ct);

                foreach (var region in TestEnvironmentOptions.DatabaseRegions.Skip(1))
                {
                    command.CommandText =
                        $"""ALTER DATABASE "{Database}" ADD REGION IF NOT EXISTS "{region}";""";
                    await command.ExecuteNonQueryAsync(ct);
                }

                // ADD REGION returns before the region exists: the documentation says the statement is
                // registered as a job, and SHOW REGIONS answers from the enum the job is still
                // rewriting. A query issued straight after the last ALTER sees one region, not three —
                // measured, not assumed. The server's apply step counts exactly that and leaves
                // placement alone below two, so returning here would hand the suite a cluster that is
                // multi-region and a server that has already decided it is not.
                //
                // So wait on the job rather than polling the count. SHOW JOBS WHEN COMPLETE blocks
                // until the ones it is given finish, which is the mechanism saying it is done instead
                // of us inferring it from a side effect.
                command.CommandTimeout = (int)JobWait.TotalSeconds;
                command.CommandText    =
                    """
                    SHOW JOBS WHEN COMPLETE (
                        SELECT job_id FROM [SHOW JOBS]
                         WHERE job_type = 'TYPEDESC SCHEMA CHANGE'
                           AND status NOT IN ('succeeded', 'failed', 'canceled')
                    );
                    """;
                await command.ExecuteNonQueryAsync(ct);

                // And then assert the postcondition anyway, because it is the thing that matters and it
                // does not depend on how Cockroach happens to model the work. A mismatch here means the
                // job finished and the regions still are not there, which is worth failing loudly for
                // rather than discovering three tests later.
                command.CommandTimeout = 0;
                command.CommandText    = $"""SELECT count(*) FROM [SHOW REGIONS FROM DATABASE "{Database}"];""";

                var declared = Convert.ToInt32(await command.ExecuteScalarAsync(ct));

                if (declared != TestEnvironmentOptions.DatabaseRegions.Length)
                    throw new InvalidOperationException(
                        $"the fixture asked for {TestEnvironmentOptions.DatabaseRegions.Length} regions and the "
                      + $"database reports {declared} after its schema change jobs completed; placement will be "
                      + "skipped and every assertion about it would be vacuous");

                return;
            }
            catch (NpgsqlException) when (attempt < 60)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var node in nodes)
            await node.DisposeAsync();

        await network.DisposeAsync();
    }
}
