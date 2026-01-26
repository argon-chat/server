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
/// <para><strong>Note on metric names in queries:</strong></para>
/// <para>
/// OpenTelemetry exports metrics with underscores instead of hyphens.
/// Example: <c>argon-redis-connections-allocated</c> becomes <c>argon_redis_connections_allocated</c>.
/// </para>
/// <para>
/// The <c></c> suffix for counters depends on the exporter and backend:
/// <list type="bullet">
///   <item>Prometheus scrape endpoint: adds <c></c> suffix automatically</item>
///   <item>OTLP to VictoriaMetrics: typically does NOT add <c></c> suffix</item>
/// </list>
/// Check your actual metric names in VictoriaMetrics/Grafana to confirm.
/// </para>
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
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_connections_allocated[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_connections_allocated[$__rate_interval])</c></item>
    ///   <item>Total: <c>sum(increase(argon_redis_connections_allocated[$__range]))</c></item>
    ///   <item>By instance: <c>sum by (instance) (rate(argon_redis_connections_allocated[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisConnectionsAllocated = "argon-redis-connections-allocated";

    /// <summary>
    /// Total number of Redis connections deallocated (Counter).
    /// Increments when a connection is disposed.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_connections_deallocated[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_connections_deallocated[$__rate_interval])</c></item>
    ///   <item>Connection churn: <c>rate(argon_redis_connections_allocated[$__rate_interval]) + rate(argon_redis_connections_deallocated[$__rate_interval])</c></item>
    /// </list>
    /// </remarks>
    public const string RedisConnectionsDeallocated = "argon-redis-connections-deallocated";

    /// <summary>
    /// Current number of Redis connections taken from the pool (Gauge).
    /// Represents active connections currently in use.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>argon_redis_connections_taken</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Current: <c>argon_redis_connections_taken</c></item>
    ///   <item>Pool utilization %: <c>argon_redis_connections_taken / argon_redis_connections * 100</c></item>
    ///   <item>Avg over time: <c>avg_over_time(argon_redis_connections_taken[$__range])</c></item>
    /// </list>
    /// </remarks>
    public const string RedisConnectionsTaken = "argon-redis-connections-taken";

    /// <summary>
    /// Current total number of Redis connections in the pool (Gauge).
    /// Includes both available and taken connections.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>argon_redis_connections</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Current: <c>argon_redis_connections</c></item>
    ///   <item>Available connections: <c>argon_redis_connections - argon_redis_connections_taken</c></item>
    ///   <item>Max over time: <c>max_over_time(argon_redis_connections[$__range])</c></item>
    /// </list>
    /// </remarks>
    public const string RedisConnectionsTotal = "argon-redis-connections-total";

    /// <summary>
    /// Total number of connection rent operations (Counter).
    /// Increments each time <c>Rent()</c> is called.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_connections_rented[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_connections_rented[$__rate_interval])</c></item>
    ///   <item>Total: <c>sum(increase(argon_redis_connections_rented[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisConnectionsRented = "argon-redis-connections-rented";

    /// <summary>
    /// Total number of successful connection returns (Counter).
    /// Increments when a connection is returned to the pool in usable state.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_connections_returned[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_connections_returned[$__rate_interval])</c></item>
    ///   <item>Return success rate %: <c>rate(argon_redis_connections_returned[$__rate_interval]) / (rate(argon_redis_connections_returned[$__rate_interval]) + rate(argon_redis_connections_returned_faulted[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string RedisConnectionsReturned = "argon-redis-connections-returned";

    /// <summary>
    /// Total number of faulted connection returns (Counter).
    /// Increments when a connection is returned in unusable state and disposed.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_connections_returned_faulted[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_connections_returned_faulted[$__rate_interval])</c></item>
    ///   <item>Fault ratio %: <c>rate(argon_redis_connections_returned_faulted[$__rate_interval]) / rate(argon_redis_connections_rented[$__rate_interval]) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string RedisConnectionsReturnedFaulted = "argon-redis-connections-returned-faulted";

    /// <summary>
    /// Total number of pool cleanup operations (Counter).
    /// Increments when the background cleanup task runs.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_pool_cleanups[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_pool_cleanups[$__rate_interval])</c></item>
    ///   <item>Total cleanups: <c>sum(increase(argon_redis_pool_cleanups[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisPoolCleanups = "argon-redis-pool-cleanups";

    /// <summary>
    /// Total number of connections removed during cleanup (Counter).
    /// Tracks excess connections disposed by the cleanup task.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_pool_connections_removed[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_pool_connections_removed[$__rate_interval])</c></item>
    ///   <item>Avg removed per cleanup: <c>rate(argon_redis_pool_connections_removed[$__rate_interval]) / rate(argon_redis_pool_cleanups[$__rate_interval])</c></item>
    /// </list>
    /// </remarks>
    public const string RedisPoolConnectionsRemoved = "argon-redis-pool-connections-removed";

    /// <summary>
    /// Current configured maximum pool size (Gauge).
    /// May increase dynamically due to auto-scaling.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>argon_redis_pool_max_size</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Current: <c>argon_redis_pool_max_size</c></item>
    ///   <item>Headroom: <c>argon_redis_pool_max_size - argon_redis_connections</c></item>
    ///   <item>Utilization %: <c>argon_redis_connections / argon_redis_pool_max_size * 100</c></item>
    /// </list>
    /// </remarks>
    public const string RedisPoolMaxSize = "argon-redis-pool-max-size";

    /// <summary>
    /// Total number of pool auto-scaling events (Counter).
    /// Increments when the pool size is automatically increased.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_pool_scale_ups[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_pool_scale_ups[$__rate_interval])</c></item>
    ///   <item>Total scale-ups: <c>sum(increase(argon_redis_pool_scale_ups[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisPoolScaleUps = "argon-redis-pool-scale-ups";

    /// <summary>
    /// Total number of Redis operations executed (Counter).
    /// Tracks all cache operations with tags for operation type and success/failure.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_redis_operations[5m])) by (operation, status)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by operation: <c>sum by (operation) (rate(argon_redis_operations[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_redis_operations{status="success"}[$__rate_interval])) / sum(rate(argon_redis_operations[$__rate_interval])) * 100</c></item>
    ///   <item>Failed operations: <c>sum(rate(argon_redis_operations{status="failed"}[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisOperations = "argon-redis-operations";

    /// <summary>
    /// Duration of Redis operations in milliseconds (Histogram).
    /// Measures execution time for cache operations including retries.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_redis_operation_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P50: <c>histogram_quantile(0.5, sum(rate(argon_redis_operation_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_redis_operation_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P99: <c>histogram_quantile(0.99, sum(rate(argon_redis_operation_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_redis_operation_duration_sum[$__rate_interval])) / sum(rate(argon_redis_operation_duration_count[$__rate_interval]))</c></item>
    ///   <item>By operation P95: <c>histogram_quantile(0.95, sum by (operation, le) (rate(argon_redis_operation_duration_bucket[$__rate_interval])))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisOperationDuration = "argon-redis-operation-duration";

    /// <summary>
    /// Total number of Redis operation retries (Counter).
    /// Tracks retry attempts due to replica write errors or transient failures.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_operation_retries[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_operation_retries[$__rate_interval])</c></item>
    ///   <item>Retry ratio: <c>rate(argon_redis_operation_retries[$__rate_interval]) / rate(argon_redis_operations[$__rate_interval])</c></item>
    /// </list>
    /// </remarks>
    public const string RedisOperationRetries = "argon-redis-operation-retries";

    /// <summary>
    /// Total number of distributed cache operations (Counter).
    /// Tracks IDistributedCache operations (Get, Set, Refresh, Remove) with tags for operation type.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_redis_distributed_cache_operations[5m])) by (operation)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by operation: <c>sum by (operation) (rate(argon_redis_distributed_cache_operations[$__rate_interval]))</c></item>
    ///   <item>Total: <c>sum(rate(argon_redis_distributed_cache_operations[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisDistributedCacheOperations = "argon-redis-distributed-cache-operations";

    /// <summary>
    /// Duration of distributed cache operations in milliseconds (Histogram).
    /// Measures execution time for IDistributedCache operations.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_redis_distributed_cache_operation_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P50: <c>histogram_quantile(0.5, sum(rate(argon_redis_distributed_cache_operation_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_redis_distributed_cache_operation_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>Avg by operation: <c>sum by (operation) (rate(argon_redis_distributed_cache_operation_duration_sum[$__rate_interval])) / sum by (operation) (rate(argon_redis_distributed_cache_operation_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisDistributedCacheOperationDuration = "argon-redis-distributed-cache-operation-duration";

    /// <summary>
    /// Total number of Redis key expiration events processed (Counter).
    /// Tracks keyspace notifications for expired keys.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_redis_key_expiration_events[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_redis_key_expiration_events[$__rate_interval])</c></item>
    ///   <item>Total: <c>sum(increase(argon_redis_key_expiration_events[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string RedisKeyExpirationEvents = "argon-redis-key-expiration-events";

    /// <summary>
    /// Total number of Orleans rebalance checks performed (Counter).
    /// Increments each time the rebalancer evaluates imbalance.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_orleans_rebalance_checks[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_orleans_rebalance_checks[$__rate_interval])</c></item>
    ///   <item>Total: <c>sum(increase(argon_orleans_rebalance_checks[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string OrleansRebalanceChecks = "argon-orleans-rebalance-checks";

    /// <summary>
    /// Total number of times rebalancing was accepted (Counter).
    /// Increments when imbalance is within tolerance and rebalancing proceeds.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_orleans_rebalance_accepted[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_orleans_rebalance_accepted[$__rate_interval])</c></item>
    ///   <item>Accept ratio %: <c>rate(argon_orleans_rebalance_accepted[$__rate_interval]) / rate(argon_orleans_rebalance_checks[$__rate_interval]) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string OrleansRebalanceAccepted = "argon-orleans-rebalance-accepted";

    /// <summary>
    /// Total number of times rebalancing was rejected (Counter).
    /// Increments when imbalance exceeds tolerance or cooldown period is active.
    /// Tags: reason (threshold, cooldown)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_orleans_rebalance_rejected[5m])) by (reason)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by reason: <c>sum by (reason) (rate(argon_orleans_rebalance_rejected[$__rate_interval]))</c></item>
    ///   <item>Rejection ratio %: <c>rate(argon_orleans_rebalance_rejected[$__rate_interval]) / rate(argon_orleans_rebalance_checks[$__rate_interval]) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string OrleansRebalanceRejected = "argon-orleans-rebalance-rejected";

    /// <summary>
    /// Distribution of activation imbalance values (Histogram).
    /// Tracks the imbalance metric used for rebalancing decisions.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_orleans_imbalance_value_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P50: <c>histogram_quantile(0.5, sum(rate(argon_orleans_imbalance_value_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_orleans_imbalance_value_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_orleans_imbalance_value_sum[$__rate_interval])) / sum(rate(argon_orleans_imbalance_value_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string OrleansImbalanceValue = "argon-orleans-imbalance-value";

    /// <summary>
    /// Total number of phone verification codes sent (Counter).
    /// Tags: channel (telegram, prelude, twilio, null), status (success, failed)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_phone_verification_sent[5m])) by (channel, status)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by channel: <c>sum by (channel) (rate(argon_phone_verification_sent[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_phone_verification_sent{status="success"}[$__rate_interval])) / sum(rate(argon_phone_verification_sent[$__rate_interval])) * 100</c></item>
    ///   <item>Failures by channel: <c>sum by (channel) (rate(argon_phone_verification_sent{status="failed"}[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneVerificationSent = "argon-phone-verification-sent";

    /// <summary>
    /// Total number of phone verification checks performed (Counter).
    /// Tags: channel (telegram, prelude, twilio, null), status (verified, invalid, expired, too_many_attempts, error)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_phone_verification_checks[5m])) by (channel, status)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by status: <c>sum by (status) (rate(argon_phone_verification_checks[$__rate_interval]))</c></item>
    ///   <item>Verification success rate %: <c>sum(rate(argon_phone_verification_checks{status="verified"}[$__rate_interval])) / sum(rate(argon_phone_verification_checks[$__rate_interval])) * 100</c></item>
    ///   <item>Failures breakdown: <c>sum by (status) (rate(argon_phone_verification_checks{status=~"invalid|expired|too_many_attempts|error"}[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneVerificationChecks = "argon-phone-verification-checks";

    /// <summary>
    /// Duration of phone verification send operations in milliseconds (Histogram).
    /// Tags: channel (telegram, prelude, twilio, null), status (success, failed)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_phone_verification_send_duration_bucket[5m])) by (le, channel))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P95 by channel: <c>histogram_quantile(0.95, sum by (channel, le) (rate(argon_phone_verification_send_duration_bucket[$__rate_interval])))</c></item>
    ///   <item>Avg by channel: <c>sum by (channel) (rate(argon_phone_verification_send_duration_sum[$__rate_interval])) / sum by (channel) (rate(argon_phone_verification_send_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneVerificationSendDuration = "argon-phone-verification-send-duration";

    /// <summary>
    /// Duration of phone verification check operations in milliseconds (Histogram).
    /// Tags: channel (telegram, prelude, twilio, null)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_phone_verification_check_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_phone_verification_check_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_phone_verification_check_duration_sum[$__rate_interval])) / sum(rate(argon_phone_verification_check_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneVerificationCheckDuration = "argon-phone-verification-check-duration";

    /// <summary>
    /// Total number of Telegram send ability checks (Counter).
    /// Tags: result (can_send, insufficient_balance, error)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_phone_telegram_send_ability_checks[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_phone_telegram_send_ability_checks[$__rate_interval]))</c></item>
    ///   <item>Can send rate %: <c>sum(rate(argon_phone_telegram_send_ability_checks{result="can_send"}[$__rate_interval])) / sum(rate(argon_phone_telegram_send_ability_checks[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneTelegramSendAbilityChecks = "argon-phone-telegram-send-ability-checks";

    /// <summary>
    /// Telegram Gateway remaining balance (Gauge).
    /// Tracks the remaining balance for sending messages.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>argon_phone_telegram_balance</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Current: <c>argon_phone_telegram_balance</c></item>
    ///   <item>Balance trend: <c>deriv(argon_phone_telegram_balance[$__range])</c></item>
    ///   <item>Min over time: <c>min_over_time(argon_phone_telegram_balance[$__range])</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneTelegramBalance = "argon-phone-telegram-balance";

    /// <summary>
    /// Total cost of phone verification requests (Counter).
    /// Tags: channel (telegram, prelude, twilio)
    /// Tracks cumulative cost across all channels.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_phone_verification_cost[5m])) by (channel)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Cost rate by channel: <c>sum by (channel) (rate(argon_phone_verification_cost[$__rate_interval]))</c></item>
    ///   <item>Total cost: <c>sum(increase(argon_phone_verification_cost[$__range]))</c></item>
    ///   <item>Avg cost per send: <c>rate(argon_phone_verification_cost[$__rate_interval]) / rate(argon_phone_verification_sent{status="success"}[$__rate_interval])</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneVerificationCost = "argon-phone-verification-cost";

    /// <summary>
    /// Total number of phone verification fallback events (Counter).
    /// Tags: from_channel (telegram, prelude, twilio), to_channel (prelude, twilio, null)
    /// Increments when a channel fails and fallback is attempted.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_phone_verification_fallbacks[5m])) by (from_channel, to_channel)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by channels: <c>sum by (from_channel, to_channel) (rate(argon_phone_verification_fallbacks[$__rate_interval]))</c></item>
    ///   <item>Fallback rate %: <c>sum(rate(argon_phone_verification_fallbacks[$__rate_interval])) / sum(rate(argon_phone_verification_sent[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string PhoneVerificationFallbacks = "argon-phone-verification-fallbacks";

    /// <summary>
    /// Total number of user authorization attempts (Counter).
    /// Tags: result (success, bad_credentials, bad_otp, required_otp), auth_mode (email_password, email_otp, email_password_otp)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_authorization_attempts[5m])) by (result, auth_mode)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_authorization_attempts[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_authorization_attempts{result="success"}[$__rate_interval])) / sum(rate(argon_authorization_attempts[$__rate_interval])) * 100</c></item>
    ///   <item>By auth mode: <c>sum by (auth_mode) (rate(argon_authorization_attempts[$__rate_interval]))</c></item>
    ///   <item>Failed attempts: <c>sum(rate(argon_authorization_attempts{result=~"bad_credentials|bad_otp"}[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string AuthorizationAttempts = "argon-authorization-attempts";

    /// <summary>
    /// Duration of authorization operations in milliseconds (Histogram).
    /// Tags: result (success, failed), auth_mode (email_password, email_otp, email_password_otp)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_authorization_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_authorization_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P95 by auth_mode: <c>histogram_quantile(0.95, sum by (auth_mode, le) (rate(argon_authorization_duration_bucket[$__rate_interval])))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_authorization_duration_sum[$__rate_interval])) / sum(rate(argon_authorization_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string AuthorizationDuration = "argon-authorization-duration";

    /// <summary>
    /// Total number of user registrations (Counter).
    /// Tags: result (success, email_taken, username_taken, username_reserved, error)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_user_registrations[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_user_registrations[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_user_registrations{result="success"}[$__rate_interval])) / sum(rate(argon_user_registrations[$__rate_interval])) * 100</c></item>
    ///   <item>New users total: <c>sum(increase(argon_user_registrations{result="success"}[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string UserRegistrations = "argon-user-registrations";

    /// <summary>
    /// Duration of registration operations in milliseconds (Histogram).
    /// Tags: result (success, failed)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_user_registration_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_user_registration_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_user_registration_duration_sum[$__rate_interval])) / sum(rate(argon_user_registration_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string UserRegistrationDuration = "argon-user-registration-duration";

    /// <summary>
    /// Total number of password reset requests (Counter).
    /// Tags: stage (request, verify), result (success, failed)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_password_resets[5m])) by (stage, result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by stage: <c>sum by (stage) (rate(argon_password_resets[$__rate_interval]))</c></item>
    ///   <item>Completion rate %: <c>sum(rate(argon_password_resets{stage="verify", result="success"}[$__rate_interval])) / sum(rate(argon_password_resets{stage="request", result="success"}[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string PasswordResets = "argon-password-resets";

    /// <summary>
    /// Duration of password reset operations in milliseconds (Histogram).
    /// Tags: stage (request, verify)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_password_reset_duration_bucket[5m])) by (le, stage))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P95 by stage: <c>histogram_quantile(0.95, sum by (stage, le) (rate(argon_password_reset_duration_bucket[$__rate_interval])))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_password_reset_duration_sum[$__rate_interval])) / sum(rate(argon_password_reset_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string PasswordResetDuration = "argon-password-reset-duration";

    /// <summary>
    /// Total number of external authorization attempts (Counter).
    /// Tags: result (success, failed), auth_mode (email_password, email_otp, email_password_otp)
    /// Tracks OAuth/external provider authorizations.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_external_authorization_attempts[5m])) by (result, auth_mode)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_external_authorization_attempts[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_external_authorization_attempts{result="success"}[$__rate_interval])) / sum(rate(argon_external_authorization_attempts[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string ExternalAuthorizationAttempts = "argon-external-authorization-attempts";

    /// <summary>
    /// Total number of OTP sends during authorization flow (Counter).
    /// Tags: purpose (sign_in, reset_password), method (email, phone)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_authorization_otp_sent[5m])) by (purpose, method)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by purpose: <c>sum by (purpose) (rate(argon_authorization_otp_sent[$__rate_interval]))</c></item>
    ///   <item>Rate by method: <c>sum by (method) (rate(argon_authorization_otp_sent[$__rate_interval]))</c></item>
    ///   <item>Total: <c>sum(increase(argon_authorization_otp_sent[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string AuthorizationOtpSent = "argon-authorization-otp-sent";

    /// <summary>
    /// Total number of messages sent in channels (Counter).
    /// Tags: channel_type (text, voice)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_messages_sent[5m])) by (channel_type)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by type: <c>sum by (channel_type) (rate(argon_channel_messages_sent[$__rate_interval]))</c></item>
    ///   <item>Total messages: <c>sum(increase(argon_channel_messages_sent[$__range]))</c></item>
    ///   <item>Messages per minute: <c>sum(rate(argon_channel_messages_sent[$__rate_interval])) * 60</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelMessagesSent = "argon-channel-messages-sent";

    /// <summary>
    /// Duration of message send operations in milliseconds (Histogram).
    /// Tags: channel_type (text, voice), has_reply (true, false)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_channel_message_send_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_channel_message_send_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P95 by type: <c>histogram_quantile(0.95, sum by (channel_type, le) (rate(argon_channel_message_send_duration_bucket[$__rate_interval])))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_channel_message_send_duration_sum[$__rate_interval])) / sum(rate(argon_channel_message_send_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelMessageSendDuration = "argon-channel-message-send-duration";

    /// <summary>
    /// Total number of voice channel joins (Counter).
    /// Tags: source (direct, meeting)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_voice_joins[5m])) by (source)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by source: <c>sum by (source) (rate(argon_channel_voice_joins[$__rate_interval]))</c></item>
    ///   <item>Total joins: <c>sum(increase(argon_channel_voice_joins[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelVoiceJoins = "argon-channel-voice-joins";

    /// <summary>
    /// Total number of voice channel leaves (Counter).
    /// Tags: source (direct, meeting)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_voice_leaves[5m])) by (source)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by source: <c>sum by (source) (rate(argon_channel_voice_leaves[$__rate_interval]))</c></item>
    ///   <item>Net change: <c>sum(rate(argon_channel_voice_joins[$__rate_interval])) - sum(rate(argon_channel_voice_leaves[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelVoiceLeaves = "argon-channel-voice-leaves";

    /// <summary>
    /// Duration of voice sessions in seconds (Histogram).
    /// Tracks how long users stay in voice channels.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_channel_voice_session_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P50: <c>histogram_quantile(0.5, sum(rate(argon_channel_voice_session_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_channel_voice_session_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_channel_voice_session_duration_sum[$__rate_interval])) / sum(rate(argon_channel_voice_session_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelVoiceSessionDuration = "argon-channel-voice-session-duration";

    /// <summary>
    /// Current number of users in voice channels (Gauge).
    /// Sampled per-channel on user join/leave events.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(argon_channel_voice_active_users)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Total: <c>sum(argon_channel_voice_active_users)</c></item>
    ///   <item>Max over time: <c>max_over_time(sum(argon_channel_voice_active_users)[$__range])</c></item>
    ///   <item>Avg over time: <c>avg_over_time(sum(argon_channel_voice_active_users)[$__range])</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelVoiceActiveUsers = "argon-channel-voice-active-users";

    /// <summary>
    /// Total number of channel recordings started (Counter).
    /// Tags: result (success, already_active)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_recordings_started[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_channel_recordings_started[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_channel_recordings_started{result="success"}[$__rate_interval])) / sum(rate(argon_channel_recordings_started[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelRecordingsStarted = "argon-channel-recordings-started";

    /// <summary>
    /// Total number of channel recordings stopped (Counter).
    /// Tags: result (success, not_active)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_recordings_stopped[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_channel_recordings_stopped[$__rate_interval]))</c></item>
    ///   <item>Total: <c>sum(increase(argon_channel_recordings_stopped[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelRecordingsStopped = "argon-channel-recordings-stopped";

    /// <summary>
    /// Total number of linked meetings created (Counter).
    /// Tags: result (success, already_exists, no_permission, error)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_linked_meetings_created[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_channel_linked_meetings_created[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_channel_linked_meetings_created{result="success"}[$__rate_interval])) / sum(rate(argon_channel_linked_meetings_created[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelLinkedMeetingsCreated = "argon-channel-linked-meetings-created";

    /// <summary>
    /// Total number of linked meetings ended (Counter).
    /// Tags: result (success, not_found, no_permission)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_linked_meetings_ended[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_channel_linked_meetings_ended[$__rate_interval]))</c></item>
    ///   <item>Total: <c>sum(increase(argon_channel_linked_meetings_ended[$__range]))</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelLinkedMeetingsEnded = "argon-channel-linked-meetings-ended";

    /// <summary>
    /// Total number of typing events emitted (Counter).
    /// Tags: event_type (typing, stop_typing)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_typing_events[5m])) by (event_type)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by type: <c>sum by (event_type) (rate(argon_channel_typing_events[$__rate_interval]))</c></item>
    ///   <item>Total: <c>sum(rate(argon_channel_typing_events[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelTypingEvents = "argon-channel-typing-events";

    /// <summary>
    /// Total number of channel member kicks (Counter).
    /// Tags: result (success, no_permission, invalid_channel)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_channel_member_kicks[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_channel_member_kicks[$__rate_interval]))</c></item>
    ///   <item>Success rate %: <c>sum(rate(argon_channel_member_kicks{result="success"}[$__rate_interval])) / sum(rate(argon_channel_member_kicks[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string ChannelMemberKicks = "argon-channel-member-kicks";

    /// <summary>
    /// Total number of user sessions started (Counter).
    /// Increments when BeginRealtimeSession is called.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>rate(argon_user_sessions_started[5m])</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate: <c>rate(argon_user_sessions_started[$__rate_interval])</c></item>
    ///   <item>Total: <c>sum(increase(argon_user_sessions_started[$__range]))</c></item>
    ///   <item>By instance: <c>sum by (instance) (rate(argon_user_sessions_started[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string UserSessionsStarted = "argon-user-sessions-started";

    /// <summary>
    /// Total number of user sessions ended (Counter).
    /// Tags: reason (graceful, expired, error)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_user_sessions_ended[5m])) by (reason)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by reason: <c>sum by (reason) (rate(argon_user_sessions_ended[$__rate_interval]))</c></item>
    ///   <item>Graceful %: <c>sum(rate(argon_user_sessions_ended{reason="graceful"}[$__rate_interval])) / sum(rate(argon_user_sessions_ended[$__rate_interval])) * 100</c></item>
    ///   <item>Error rate: <c>sum(rate(argon_user_sessions_ended{reason="error"}[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string UserSessionsEnded = "argon-user-sessions-ended";

    /// <summary>
    /// Total number of heartbeats received (Counter).
    /// Tags: status (online, idle, dnd)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_user_session_heartbeats[5m])) by (status)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by status: <c>sum by (status) (rate(argon_user_session_heartbeats[$__rate_interval]))</c></item>
    ///   <item>Total rate: <c>sum(rate(argon_user_session_heartbeats[$__rate_interval]))</c></item>
    ///   <item>Heartbeats per minute: <c>sum(rate(argon_user_session_heartbeats[$__rate_interval])) * 60</c></item>
    /// </list>
    /// </remarks>
    public const string UserSessionHeartbeats = "argon-user-session-heartbeats";

    /// <summary>
    /// Current number of online users across all sessions (ObservableGauge).
    /// Measured by querying IUserPresenceService directly to ensure accuracy.
    /// This is aggregate across all silo instances.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>argon_user_online_count</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Current: <c>argon_user_online_count</c></item>
    ///   <item>Max over time: <c>max_over_time(argon_user_online_count[$__range])</c></item>
    ///   <item>Avg over time: <c>avg_over_time(argon_user_online_count[$__range])</c></item>
    ///   <item>Trend: <c>deriv(argon_user_online_count[$__range])</c></item>
    /// </list>
    /// </remarks>
    public const string UserOnlineCount = "argon-user-online-count";

    /// <summary>
    /// Current number of active sessions on this silo (Gauge).
    /// Tracks local session grain activations.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(argon_user_sessions_active)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Total: <c>sum(argon_user_sessions_active)</c></item>
    ///   <item>By instance: <c>argon_user_sessions_active</c></item>
    ///   <item>Max over time: <c>max_over_time(sum(argon_user_sessions_active)[$__range])</c></item>
    /// </list>
    /// </remarks>
    public const string UserSessionsActive = "argon-user-sessions-active";

    /// <summary>
    /// Duration of user sessions in seconds (Histogram).
    /// Measured from BeginRealtimeSession to EndRealtimeSession/deactivation.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>histogram_quantile(0.95, sum(rate(argon_user_session_duration_bucket[5m])) by (le))</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>P50: <c>histogram_quantile(0.5, sum(rate(argon_user_session_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>P95: <c>histogram_quantile(0.95, sum(rate(argon_user_session_duration_bucket[$__rate_interval])) by (le))</c></item>
    ///   <item>Avg: <c>sum(rate(argon_user_session_duration_sum[$__rate_interval])) / sum(rate(argon_user_session_duration_count[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string UserSessionDuration = "argon-user-session-duration";

    /// <summary>
    /// Total number of session expirations detected (Counter).
    /// Tags: result (offline, switch_session)
    /// Increments when OnKeyExpired is triggered.
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_user_session_expirations[5m])) by (result)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by result: <c>sum by (result) (rate(argon_user_session_expirations[$__rate_interval]))</c></item>
    ///   <item>Offline rate: <c>sum(rate(argon_user_session_expirations{result="offline"}[$__rate_interval]))</c></item>
    ///   <item>Switch ratio %: <c>sum(rate(argon_user_session_expirations{result="switch_session"}[$__rate_interval])) / sum(rate(argon_user_session_expirations[$__rate_interval])) * 100</c></item>
    /// </list>
    /// </remarks>
    public const string UserSessionExpirations = "argon-user-session-expirations";

    /// <summary>
    /// Total number of status changes (Counter).
    /// Tags: from_status (online, idle, dnd), to_status (online, idle, dnd)
    /// </summary>
    /// <remarks>
    /// <para><strong>VictoriaMetrics:</strong></para>
    /// <code>sum(rate(argon_user_status_changes[5m])) by (from_status, to_status)</code>
    /// <para><strong>Grafana:</strong></para>
    /// <list type="bullet">
    ///   <item>Rate by transition: <c>sum by (from_status, to_status) (rate(argon_user_status_changes[$__rate_interval]))</c></item>
    ///   <item>Total rate: <c>sum(rate(argon_user_status_changes[$__rate_interval]))</c></item>
    ///   <item>To idle: <c>sum(rate(argon_user_status_changes{to_status="idle"}[$__rate_interval]))</c></item>
    /// </list>
    /// </remarks>
    public const string UserStatusChanges = "argon-user-status-changes";
}
