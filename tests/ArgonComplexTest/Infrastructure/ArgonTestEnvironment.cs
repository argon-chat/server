namespace ArgonComplexTest.Infrastructure;

using Argon.Features.Env;
using System.Diagnostics;
using Testcontainers.Minio;
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
    private readonly MinioContainer  _s3;

    /// <summary>The bucket every media test uploads into.</summary>
    public const string BucketName = "argon-test";

    public ArgonServerTargetHost Host       { get; private set; } = null!;
    public HttpClient            HttpClient { get; private set; } = null!;

    public TestDatabaseKind DatabaseKind => _database.Kind;

    /// <summary>
    /// The object store as <c>host:port</c>, which is the shape <c>Storage:Endpoint</c> takes.
    /// </summary>
    /// <remarks>
    /// Deliberately <c>localhost</c> rather than the container's own hostname: it has to match the
    /// <c>MINIO_DOMAIN</c> above, because that is the suffix MinIO strips to find the bucket in a
    /// virtual-host URL.
    /// </remarks>
    public string S3Endpoint => $"localhost:{_s3.GetMappedPublicPort(9000)}";

    private ArgonTestEnvironment()
    {
        _database = TestDatabaseFactory.Create(TestEnvironmentOptions.DatabaseKind);
        _redis    = new RedisBuilder(TestEnvironmentOptions.RedisImage)
           .WithReuse(TestEnvironmentOptions.ReuseContainers)
           .Build();
        _nats = new NatsBuilder(TestEnvironmentOptions.NatsImage)
           .WithReuse(TestEnvironmentOptions.ReuseContainers)
           .Build();

        // MINIO_DOMAIN is what makes the presigned URLs work at all. This server signs uploads in
        // virtual-host style -- https://{bucket}.{endpoint}/{key} -- and without a domain configured
        // MinIO reads that host as a bucket name of its own and answers 404 for every upload. Setting
        // it to `localhost` tells MinIO to strip that suffix and take the label in front as the
        // bucket, which is exactly what a real S3 endpoint does. See MediaUploadTests for the other
        // half: `bucket.localhost` does not resolve, so the test client dials the mapped port itself.
        _s3 = new MinioBuilder(TestEnvironmentOptions.MinioImage)
           .WithReuse(TestEnvironmentOptions.ReuseContainers)
           .WithEnvironment("MINIO_DOMAIN", "localhost")
           // Measured at 226 MB resident when left alone and 211 MB under this cap, still answering
           // its health probe -- so the ceiling costs nothing and is worth having on a CI runner,
           // where the .NET host and the database are already the expensive tenants and an object
           // store that grows a buffer per concurrent upload is the one that would tip it over.
           .WithEnvironment("GOMEMLIMIT", "128MiB")
           .WithEnvironment("MINIO_API_REQUESTS_MAX", "32")
           .WithCreateParameterModifier(p => p.HostConfig.Memory = 256L * 1024 * 1024)
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
            _nats.StartAsync(cts.Token),
            _s3.StartAsync(cts.Token));

        // The server never creates a bucket -- a deployment provisions one -- so the suite has to
        // stand in for the deployment here, or every upload fails on a bucket that is not there.
        await TestObjectStore.EnsureBucketAsync(S3Endpoint, _s3.GetAccessKey(), _s3.GetSecretKey(),
            BucketName, cts.Token);

        TestContext.Progress.WriteLine(
            $"[argon-tests] {_database.Kind} + redis + nats + minio up in {stopwatch.Elapsed.TotalSeconds:F1}s");

        Host = new ArgonServerTargetHost(new ArgonTestHostSettings(
            RedisConnectionString: _redis.GetConnectionString(),
            NatsConnectionString: _nats.GetConnectionString(),
            DatabaseConnectionString: _database.ConnectionString,
            DatabaseProvider: _database.ProviderKind,
            S3Endpoint: S3Endpoint,
            S3AccessKey: _s3.GetAccessKey(),
            S3SecretKey: _s3.GetSecretKey(),
            S3Bucket: BucketName));

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
            _nats.DisposeAsync().AsTask(),
            _s3.DisposeAsync().AsTask());
    }
}
