namespace ArgonComplexTest.Infrastructure;

using Microsoft.Extensions.Logging;

/// <summary>Which database engine the integration suite is pointed at.</summary>
public enum TestDatabaseKind
{
    /// <summary>
    /// Vanilla PostgreSQL. The default: it boots in a couple of seconds where CockroachDB needs
    /// tens of seconds, and every Argon migration is plain PostgreSQL DDL once the Cockroach-only
    /// multiregional generator is switched off.
    /// </summary>
    Postgres,

    /// <summary>
    /// CockroachDB — what production actually runs. Slower to start, but the only way to exercise
    /// the multi-region / row-TTL DDL and Cockroach's serializable retry behaviour.
    /// </summary>
    Cockroach
}

/// <summary>
/// Every knob the integration suite reads from the environment, in one place.
/// <para>
/// Nothing here is required: the defaults give a working local run. CI overrides
/// <see cref="DatabaseKindVariable"/> to fan the same suite out over both engines.
/// </para>
/// </summary>
public static class TestEnvironmentOptions
{
    /// <summary>"postgres" (default) or "cockroach".</summary>
    public const string DatabaseKindVariable = "ARGON_TEST_DB";

    /// <summary>Overrides the container image for the selected database engine.</summary>
    public const string DatabaseImageVariable = "ARGON_TEST_DB_IMAGE";

    public const string RedisImageVariable = "ARGON_TEST_REDIS_IMAGE";
    public const string NatsImageVariable = "ARGON_TEST_NATS_IMAGE";

    /// <summary>
    /// Set to <c>1</c>/<c>true</c> to keep containers alive between runs (Testcontainers reuse).
    /// Requires <c>testcontainers.reuse.enable=true</c> in <c>~/.testcontainers.properties</c>.
    /// </summary>
    public const string ReuseContainersVariable = "ARGON_TEST_REUSE_CONTAINERS";

    /// <summary>How long to wait for the whole infrastructure stack to come up, in seconds.</summary>
    public const string StartupTimeoutVariable = "ARGON_TEST_STARTUP_TIMEOUT";

    /// <summary>Set to <c>1</c> to write the server's own logs to the test output.</summary>
    public const string ServerLogsVariable = "ARGON_TEST_LOGS";

    /// <summary>Minimum level for those logs. Default <c>Warning</c>.</summary>
    public const string ServerLogLevelVariable = "ARGON_TEST_LOG_LEVEL";

    public static TestDatabaseKind DatabaseKind
        => Enum.TryParse<TestDatabaseKind>(Read(DatabaseKindVariable), ignoreCase: true, out var kind)
            ? kind
            : TestDatabaseKind.Postgres;

    public static string DatabaseImage
        => Read(DatabaseImageVariable) ?? DatabaseKind switch
        {
            TestDatabaseKind.Cockroach => "cockroachdb/cockroach:latest-v24.3",
            _                          => "postgres:17-alpine"
        };

    /// <summary>
    /// The region name the CockroachDB node advertises, and the primary region its database is given.
    /// </summary>
    /// <remarks>
    /// <para>It has to be the same string the server is configured with —
    /// <c>Database:Regions:PrimaryRegion</c> in <c>appsettings.json</c> — because CockroachDB matches a
    /// database's primary region against the region names its nodes declared, by name and nothing else.
    /// A mismatch is not a degradation: every statement carrying a <c>LOCALITY</c> clause is rejected
    /// outright, and the placement fixture then reports on this file instead of on the migrations.</para>
    ///
    /// <para>A constant rather than something read out of the server's configuration, because the
    /// container has to start before any host exists to read configuration from. If the shipped primary
    /// region is ever renamed, this moves with it.</para>
    /// </remarks>
    public const string DatabaseRegion = "ru-central";

    public static string RedisImage => Read(RedisImageVariable) ?? "redis:7-alpine";

    public static string NatsImage => Read(NatsImageVariable) ?? "nats:2.10-alpine";

    public static bool ReuseContainers => IsTruthy(Read(ReuseContainersVariable));

    public static TimeSpan StartupTimeout
        => int.TryParse(Read(StartupTimeoutVariable), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromMinutes(5);

    public static bool ServerLogsEnabled => IsTruthy(Read(ServerLogsVariable));

    public static LogLevel ServerLogLevel
        => Enum.TryParse<LogLevel>(Read(ServerLogLevelVariable), ignoreCase: true, out var level)
            ? level
            : LogLevel.Warning;

    private static string? Read(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value.Trim() : null;

    private static bool IsTruthy(string? value)
        => value is not null && (value.Equals("1", StringComparison.Ordinal) ||
                                 value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                                 value.Equals("yes", StringComparison.OrdinalIgnoreCase));
}
