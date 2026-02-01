namespace Argon.Services;

using System.Diagnostics.Metrics;

/// <summary>
/// OpenTelemetry metric instruments for Redis connection pool monitoring.
/// </summary>
/// <remarks>
/// All instruments use the shared <see cref="Instruments.Meter"/> and reference
/// metric names defined in <see cref="InstrumentNames"/>.
/// </remarks>
internal static class CacheInstruments
{
    /// <summary>
    /// Counter: Total Redis connections allocated.
    /// Increments when <c>EnsureNew()</c> creates a new connection.
    /// </summary>
    public static readonly Counter<long> ConnectionsAllocated =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisConnectionsAllocated,
            unit: "{connection}",
            description: "Total number of Redis connections allocated");

    /// <summary>
    /// Counter: Total Redis connections deallocated.
    /// Increments when a connection is disposed via <c>DisposeMuxAsync()</c>.
    /// </summary>
    public static readonly Counter<long> ConnectionsDeallocated =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisConnectionsDeallocated,
            unit: "{connection}",
            description: "Total number of Redis connections deallocated");

    /// <summary>
    /// Gauge: Current number of taken connections.
    /// Reflects connections actively in use by callers.
    /// </summary>
    public static readonly ObservableGauge<long> ConnectionsTaken =
        Instruments.Meter.CreateObservableGauge(
            InstrumentNames.RedisConnectionsTaken,
            observeValue: () => RedisConnectionPool.ObservableTaken,
            unit: "{connection}",
            description: "Current number of Redis connections taken from the pool");

    /// <summary>
    /// Gauge: Total connections (taken + available).
    /// Reflects the overall pool allocation state.
    /// </summary>
    public static readonly ObservableGauge<long> ConnectionsTotal =
        Instruments.Meter.CreateObservableGauge(
            InstrumentNames.RedisConnectionsTotal,
            observeValue: () => RedisConnectionPool.ObservableAllocated,
            unit: "{connection}",
            description: "Current total number of Redis connections in the pool");

    /// <summary>
    /// Counter: Total <c>Rent()</c> calls.
    /// Tracks connection acquisition attempts.
    /// </summary>
    public static readonly Counter<long> ConnectionsRented =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisConnectionsRented,
            unit: "{operation}",
            description: "Total number of connection rent operations");

    /// <summary>
    /// Counter: Successful returns (usable connections).
    /// Increments in <c>ReturnAsync()</c> when connection is still valid.
    /// </summary>
    public static readonly Counter<long> ConnectionsReturned =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisConnectionsReturned,
            unit: "{operation}",
            description: "Total number of successful connection returns");

    /// <summary>
    /// Counter: Faulted returns (unusable connections).
    /// Increments in <c>ReturnFaultedAsync()</c> or when validation fails.
    /// </summary>
    public static readonly Counter<long> ConnectionsReturnedFaulted =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisConnectionsReturnedFaulted,
            unit: "{operation}",
            description: "Total number of faulted connection returns");

    /// <summary>
    /// Counter: Pool cleanup task executions.
    /// Increments each time the periodic cleanup runs.
    /// </summary>
    public static readonly Counter<long> PoolCleanups =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisPoolCleanups,
            unit: "{operation}",
            description: "Total number of pool cleanup operations");

    /// <summary>
    /// Counter: Connections removed during cleanup.
    /// Tracks excess connections disposed to maintain pool size limits.
    /// </summary>
    public static readonly Counter<long> PoolConnectionsRemoved =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisPoolConnectionsRemoved,
            unit: "{connection}",
            description: "Total number of connections removed during cleanup");

    /// <summary>
    /// Gauge: Current configured max pool size.
    /// May change due to auto-scaling logic.
    /// </summary>
    public static readonly ObservableGauge<long> PoolMaxSize =
        Instruments.Meter.CreateObservableGauge(
            InstrumentNames.RedisPoolMaxSize,
            observeValue: () => RedisConnectionPool.ObservableMaxSize,
            unit: "{connection}",
            description: "Current configured maximum pool size");

    /// <summary>
    /// Counter: Auto-scaling events.
    /// Increments when <c>TryIncreaseMaxSize()</c> successfully increases pool capacity.
    /// </summary>
    public static readonly Counter<long> PoolScaleUps =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisPoolScaleUps,
            unit: "{operation}",
            description: "Total number of pool auto-scaling events");

    /// <summary>
    /// Counter: Total Redis operations executed.
    /// Tagged with operation name and result (success/failure).
    /// </summary>
    public static readonly Counter<long> Operations =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisOperations,
            unit: "{operation}",
            description: "Total number of Redis operations executed");

    /// <summary>
    /// Histogram: Redis operation duration.
    /// Measures execution time including retry logic.
    /// Tagged with operation name and result.
    /// </summary>
    public static readonly Histogram<double> OperationDuration =
        Instruments.Meter.CreateHistogram<double>(
            InstrumentNames.RedisOperationDuration,
            unit: "ms",
            description: "Duration of Redis operations in milliseconds");

    /// <summary>
    /// Counter: Redis operation retry attempts.
    /// Tracks retries due to replica write errors, READONLY, LOADING states.
    /// Tagged with operation name.
    /// </summary>
    public static readonly Counter<long> OperationRetries =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisOperationRetries,
            unit: "{operation}",
            description: "Total number of Redis operation retries");

    /// <summary>
    /// Counter: Distributed cache operations.
    /// Tracks IDistributedCache operations: Get, GetAsync, Set, SetAsync, Refresh, RefreshAsync, Remove, RemoveAsync.
    /// Tagged with operation type.
    /// </summary>
    public static readonly Counter<long> DistributedCacheOperations =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisDistributedCacheOperations,
            unit: "{operation}",
            description: "Total number of distributed cache operations");

    /// <summary>
    /// Histogram: Distributed cache operation duration.
    /// Measures execution time for IDistributedCache operations.
    /// Tagged with operation type.
    /// </summary>
    public static readonly Histogram<double> DistributedCacheOperationDuration =
        Instruments.Meter.CreateHistogram<double>(
            InstrumentNames.RedisDistributedCacheOperationDuration,
            unit: "ms",
            description: "Duration of distributed cache operations in milliseconds");

    /// <summary>
    /// Counter: Redis key expiration events.
    /// Increments when keyspace notification for expired key is received and processed.
    /// </summary>
    public static readonly Counter<long> KeyExpirationEvents =
        Instruments.Meter.CreateCounter<long>(
            InstrumentNames.RedisKeyExpirationEvents,
            unit: "{event}",
            description: "Total number of Redis key expiration events processed");
}