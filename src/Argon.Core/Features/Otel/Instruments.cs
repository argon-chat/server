namespace Argon;

using System.Diagnostics.Metrics;

public static class Instruments
{
    public static readonly Meter Meter = new("Argon");
}

/// <summary>
/// Contains all OpenTelemetry metric names used in Argon.
/// </summary>
/// <remarks>
/// <para><strong>Naming Convention:</strong></para>
/// <list type="bullet">
///   <item>All metric names start with <c>argon-</c> prefix</item>
///   <item>Use lowercase with hyphens for word separation (kebab-case)</item>
///   <item>Format: <c>argon-{feature}-{metric-name}</c></item>
///   <item>Example: <c>argon-redis-connections-allocated</c></item>
/// </list>
/// <para><strong>Metric Types:</strong></para>
/// <list type="bullet">
///   <item><strong>Counter:</strong> Monotonically increasing values (e.g., total requests)</item>
///   <item><strong>Gauge:</strong> Values that can go up and down (e.g., current connections)</item>
///   <item><strong>Histogram:</strong> Distribution of values (e.g., operation duration)</item>
/// </list>
/// <para>
/// These constants are used by instrument definitions in feature-specific classes 
/// (e.g., <c>CacheInstruments</c>) and should be referenced when recording metrics.
/// </para>
/// </remarks>
public static class InstrumentNames
{
    /// <summary>
    /// Total number of Redis connections allocated (Counter).
    /// Increments when a new connection is created.
    /// </summary>
    public const string RedisConnectionsAllocated = "argon-redis-connections-allocated";

    /// <summary>
    /// Total number of Redis connections deallocated (Counter).
    /// Increments when a connection is disposed.
    /// </summary>
    public const string RedisConnectionsDeallocated = "argon-redis-connections-deallocated";

    /// <summary>
    /// Current number of Redis connections taken from the pool (Gauge).
    /// Represents active connections currently in use.
    /// </summary>
    public const string RedisConnectionsTaken = "argon-redis-connections-taken";

    /// <summary>
    /// Current total number of Redis connections in the pool (Gauge).
    /// Includes both available and taken connections.
    /// </summary>
    public const string RedisConnectionsTotal = "argon-redis-connections-total";

    /// <summary>
    /// Total number of connection rent operations (Counter).
    /// Increments each time <c>Rent()</c> is called.
    /// </summary>
    public const string RedisConnectionsRented = "argon-redis-connections-rented";

    /// <summary>
    /// Total number of successful connection returns (Counter).
    /// Increments when a connection is returned to the pool in usable state.
    /// </summary>
    public const string RedisConnectionsReturned = "argon-redis-connections-returned";

    /// <summary>
    /// Total number of faulted connection returns (Counter).
    /// Increments when a connection is returned in unusable state and disposed.
    /// </summary>
    public const string RedisConnectionsReturnedFaulted = "argon-redis-connections-returned-faulted";

    /// <summary>
    /// Total number of pool cleanup operations (Counter).
    /// Increments when the background cleanup task runs.
    /// </summary>
    public const string RedisPoolCleanups = "argon-redis-pool-cleanups";

    /// <summary>
    /// Total number of connections removed during cleanup (Counter).
    /// Tracks excess connections disposed by the cleanup task.
    /// </summary>
    public const string RedisPoolConnectionsRemoved = "argon-redis-pool-connections-removed";

    /// <summary>
    /// Current configured maximum pool size (Gauge).
    /// May increase dynamically due to auto-scaling.
    /// </summary>
    public const string RedisPoolMaxSize = "argon-redis-pool-max-size";

    /// <summary>
    /// Total number of pool auto-scaling events (Counter).
    /// Increments when the pool size is automatically increased.
    /// </summary>
    public const string RedisPoolScaleUps = "argon-redis-pool-scale-ups";

    /// <summary>
    /// Total number of Redis operations executed (Counter).
    /// Tracks all cache operations with tags for operation type and success/failure.
    /// </summary>
    public const string RedisOperations = "argon-redis-operations";

    /// <summary>
    /// Duration of Redis operations in milliseconds (Histogram).
    /// Measures execution time for cache operations including retries.
    /// </summary>
    public const string RedisOperationDuration = "argon-redis-operation-duration";

    /// <summary>
    /// Total number of Redis operation retries (Counter).
    /// Tracks retry attempts due to replica write errors or transient failures.
    /// </summary>
    public const string RedisOperationRetries = "argon-redis-operation-retries";

    /// <summary>
    /// Total number of distributed cache operations (Counter).
    /// Tracks IDistributedCache operations (Get, Set, Refresh, Remove) with tags for operation type.
    /// </summary>
    public const string RedisDistributedCacheOperations = "argon-redis-distributed-cache-operations";

    /// <summary>
    /// Duration of distributed cache operations in milliseconds (Histogram).
    /// Measures execution time for IDistributedCache operations.
    /// </summary>
    public const string RedisDistributedCacheOperationDuration = "argon-redis-distributed-cache-operation-duration";

    /// <summary>
    /// Total number of Redis key expiration events processed (Counter).
    /// Tracks keyspace notifications for expired keys.
    /// </summary>
    public const string RedisKeyExpirationEvents = "argon-redis-key-expiration-events";
}