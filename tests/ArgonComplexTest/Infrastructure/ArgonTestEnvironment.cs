namespace ArgonComplexTest.Infrastructure;

using Argon.Features.Env;
using System.Diagnostics;
using Testcontainers.Nats;
using Testcontainers.Redis;

/// <summary>
/// The one and only Argon server the integration suite runs against, plus the containers behind it.
/// <para>
/// Everything here is created exactly once per test assembly by <see cref="GlobalTestSetup"/> and
/// shared by every fixture. Before, each fixture inherited a <c>[OneTimeSetUp]</c> that started its
/// own CockroachDB + Redis + NATS trio and its own <see cref="ArgonServerTargetHost"/> — twelve
/// fixtures meant twelve container stacks and twelve runs of the ~100-migration bootstrap, all
/// strictly serialised because no two of them could afford to overlap. Hoisting the stack up to the
/// assembly pays that cost once and lets fixtures run concurrently.
/// </para>
/// <para>
/// State that used to live on <see cref="TestBase"/> and could not survive concurrency — the bearer
/// token in particular — now lives per fixture instance (see <see cref="TestBase"/>), so parallel
/// fixtures cannot clobber each other's identity.
/// </para>
/// </summary>
public sealed class ArgonTestEnvironment : IAsyncDisposable
{
    private static ArgonTestEnvironment? _instance;

    /// <summary>The environment for the current run. Throws if the global setup has not run yet.</summary>
    public static ArgonTestEnvironment Instance
        => _instance ?? throw new InvalidOperationException(
            $"{nameof(ArgonTestEnvironment)} is not initialised. It is created by {nameof(GlobalTestSetup)}; " +
            "a fixture deriving from TestBase must live in an assembly where that SetUpFixture runs.");

    public static bool IsInitialised => _instance is not null;

    private readonly ITestDatabase   _database;
    private readonly RedisContainer  _redis;
    private readonly NatsContainer   _nats;

    public ArgonServerTargetHost Host       { get; private set; } = null!;
    public HttpClient            HttpClient { get; private set; } = null!;

    public TestDatabaseKind DatabaseKind => _database.Kind;

    private ArgonTestEnvironment()
    {
        _database = TestDatabaseFactory.Create(TestEnvironmentOptions.DatabaseKind);
        _redis    = new RedisBuilder(TestEnvironmentOptions.RedisImage)
           .WithReuse(TestEnvironmentOptions.ReuseContainers)
           .Build();
        _nats = new NatsBuilder(TestEnvironmentOptions.NatsImage)
           .WithReuse(TestEnvironmentOptions.ReuseContainers)
           .Build();
    }

    public async static Task<ArgonTestEnvironment> StartAsync()
    {
        if (_instance is not null)
            return _instance;

        var environment = new ArgonTestEnvironment();
        await environment.InitialiseAsync();
        _instance = environment;
        return environment;
    }

    private async Task InitialiseAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        using var cts = new CancellationTokenSource(TestEnvironmentOptions.StartupTimeout);

        // The role is named through configuration rather than the command line: WebApplicationFactory
        // invokes the entry point with no arguments. See IntegrationTestRole for why the suite gets
        // its own composed role instead of a product one.

        // Independent containers, so pull and boot them together rather than in sequence.
        await Task.WhenAll(
            _database.StartAsync(cts.Token),
            _redis.StartAsync(cts.Token),
            _nats.StartAsync(cts.Token));

        TestContext.Progress.WriteLine(
            $"[argon-tests] {_database.Kind} + redis + nats up in {stopwatch.Elapsed.TotalSeconds:F1}s");

        Host = new ArgonServerTargetHost(new ArgonTestHostSettings(
            RedisConnectionString: _redis.GetConnectionString(),
            NatsConnectionString: _nats.GetConnectionString(),
            DatabaseConnectionString: _database.ConnectionString,
            DatabaseProvider: _database.ProviderKind));

        HttpClient = Host.CreateClient();

        // CreateClient builds the host lazily; migrations run as part of that. Touching the root
        // endpoint forces the whole pipeline up now, so the cost lands in the global setup instead
        // of being charged to whichever test happened to run first.
        using var response = await HttpClient.GetAsync("/", cts.Token);
        response.EnsureSuccessStatusCode();

        TestContext.Progress.WriteLine(
            $"[argon-tests] argon host ready in {stopwatch.Elapsed.TotalSeconds:F1}s " +
            $"(db={_database.Kind}, provider={_database.ProviderKind})");
    }

    public async ValueTask DisposeAsync()
    {
        _instance = null;

        HttpClient?.Dispose();

        if (Host is not null)
            await Host.DisposeAsync();

        await Task.WhenAll(
            _database.DisposeAsync().AsTask(),
            _redis.DisposeAsync().AsTask(),
            _nats.DisposeAsync().AsTask());
    }
}
