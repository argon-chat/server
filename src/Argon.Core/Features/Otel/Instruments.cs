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

    /// <summary>
    /// Total number of Orleans rebalance checks performed (Counter).
    /// Increments each time the rebalancer evaluates imbalance.
    /// </summary>
    public const string OrleansRebalanceChecks = "argon-orleans-rebalance-checks";

    /// <summary>
    /// Total number of times rebalancing was accepted (Counter).
    /// Increments when imbalance is within tolerance and rebalancing proceeds.
    /// </summary>
    public const string OrleansRebalanceAccepted = "argon-orleans-rebalance-accepted";

    /// <summary>
    /// Total number of times rebalancing was rejected (Counter).
    /// Increments when imbalance exceeds tolerance or cooldown period is active.
    /// Tags: reason (threshold, cooldown)
    /// </summary>
    public const string OrleansRebalanceRejected = "argon-orleans-rebalance-rejected";

    /// <summary>
    /// Distribution of activation imbalance values (Histogram).
    /// Tracks the imbalance metric used for rebalancing decisions.
    /// </summary>
    public const string OrleansImbalanceValue = "argon-orleans-imbalance-value";

    /// <summary>
    /// Total number of phone verification codes sent (Counter).
    /// Tags: channel (telegram, prelude, twilio, null), status (success, failed)
    /// </summary>
    public const string PhoneVerificationSent = "argon-phone-verification-sent";

    /// <summary>
    /// Total number of phone verification checks performed (Counter).
    /// Tags: channel (telegram, prelude, twilio, null), status (verified, invalid, expired, too_many_attempts, error)
    /// </summary>
    public const string PhoneVerificationChecks = "argon-phone-verification-checks";

    /// <summary>
    /// Duration of phone verification send operations in milliseconds (Histogram).
    /// Tags: channel (telegram, prelude, twilio, null), status (success, failed)
    /// </summary>
    public const string PhoneVerificationSendDuration = "argon-phone-verification-send-duration";

    /// <summary>
    /// Duration of phone verification check operations in milliseconds (Histogram).
    /// Tags: channel (telegram, prelude, twilio, null)
    /// </summary>
    public const string PhoneVerificationCheckDuration = "argon-phone-verification-check-duration";

    /// <summary>
    /// Total number of Telegram send ability checks (Counter).
    /// Tags: result (can_send, insufficient_balance, error)
    /// </summary>
    public const string PhoneTelegramSendAbilityChecks = "argon-phone-telegram-send-ability-checks";

    /// <summary>
    /// Telegram Gateway remaining balance (Gauge).
    /// Tracks the remaining balance for sending messages.
    /// </summary>
    public const string PhoneTelegramBalance = "argon-phone-telegram-balance";

    /// <summary>
    /// Total cost of phone verification requests (Counter).
    /// Tags: channel (telegram, prelude, twilio)
    /// Tracks cumulative cost across all channels.
    /// </summary>
    public const string PhoneVerificationCost = "argon-phone-verification-cost";

    /// <summary>
    /// Total number of phone verification fallback events (Counter).
    /// Tags: from_channel (telegram, prelude, twilio), to_channel (prelude, twilio, null)
    /// Increments when a channel fails and fallback is attempted.
    /// </summary>
    public const string PhoneVerificationFallbacks = "argon-phone-verification-fallbacks";

    /// <summary>
    /// Total number of user authorization attempts (Counter).
    /// Tags: result (success, bad_credentials, bad_otp, required_otp), auth_mode (email_password, email_otp, email_password_otp)
    /// </summary>
    public const string AuthorizationAttempts = "argon-authorization-attempts";

    /// <summary>
    /// Duration of authorization operations in milliseconds (Histogram).
    /// Tags: result (success, failed), auth_mode (email_password, email_otp, email_password_otp)
    /// </summary>
    public const string AuthorizationDuration = "argon-authorization-duration";

    /// <summary>
    /// Total number of user registrations (Counter).
    /// Tags: result (success, email_taken, username_taken, username_reserved, error)
    /// </summary>
    public const string UserRegistrations = "argon-user-registrations";

    /// <summary>
    /// Duration of registration operations in milliseconds (Histogram).
    /// Tags: result (success, failed)
    /// </summary>
    public const string UserRegistrationDuration = "argon-user-registration-duration";

    /// <summary>
    /// Total number of password reset requests (Counter).
    /// Tags: stage (request, verify), result (success, failed)
    /// </summary>
    public const string PasswordResets = "argon-password-resets";

    /// <summary>
    /// Duration of password reset operations in milliseconds (Histogram).
    /// Tags: stage (request, verify)
    /// </summary>
    public const string PasswordResetDuration = "argon-password-reset-duration";

    /// <summary>
    /// Total number of external authorization attempts (Counter).
    /// Tags: result (success, failed), auth_mode (email_password, email_otp, email_password_otp)
    /// Tracks OAuth/external provider authorizations.
    /// </summary>
    public const string ExternalAuthorizationAttempts = "argon-external-authorization-attempts";

    /// <summary>
    /// Total number of OTP sends during authorization flow (Counter).
    /// Tags: purpose (sign_in, reset_password), method (email, phone)
    /// </summary>
    public const string AuthorizationOtpSent = "argon-authorization-otp-sent";

    /// <summary>
    /// Total number of messages sent in channels (Counter).
    /// Tags: channel_type (text, voice)
    /// </summary>
    public const string ChannelMessagesSent = "argon-channel-messages-sent";

    /// <summary>
    /// Duration of message send operations in milliseconds (Histogram).
    /// Tags: channel_type (text, voice), has_reply (true, false)
    /// </summary>
    public const string ChannelMessageSendDuration = "argon-channel-message-send-duration";

    /// <summary>
    /// Total number of voice channel joins (Counter).
    /// Tags: source (direct, meeting)
    /// </summary>
    public const string ChannelVoiceJoins = "argon-channel-voice-joins";

    /// <summary>
    /// Total number of voice channel leaves (Counter).
    /// Tags: source (direct, meeting)
    /// </summary>
    public const string ChannelVoiceLeaves = "argon-channel-voice-leaves";

    /// <summary>
    /// Duration of voice sessions in seconds (Histogram).
    /// Tracks how long users stay in voice channels.
    /// </summary>
    public const string ChannelVoiceSessionDuration = "argon-channel-voice-session-duration";

    /// <summary>
    /// Current number of users in voice channels (Gauge).
    /// Sampled per-channel on user join/leave events.
    /// </summary>
    public const string ChannelVoiceActiveUsers = "argon-channel-voice-active-users";

    /// <summary>
    /// Total number of channel recordings started (Counter).
    /// Tags: result (success, already_active)
    /// </summary>
    public const string ChannelRecordingsStarted = "argon-channel-recordings-started";

    /// <summary>
    /// Total number of channel recordings stopped (Counter).
    /// Tags: result (success, not_active)
    /// </summary>
    public const string ChannelRecordingsStopped = "argon-channel-recordings-stopped";

    /// <summary>
    /// Total number of linked meetings created (Counter).
    /// Tags: result (success, already_exists, no_permission, error)
    /// </summary>
    public const string ChannelLinkedMeetingsCreated = "argon-channel-linked-meetings-created";

    /// <summary>
    /// Total number of linked meetings ended (Counter).
    /// Tags: result (success, not_found, no_permission)
    /// </summary>
    public const string ChannelLinkedMeetingsEnded = "argon-channel-linked-meetings-ended";

    /// <summary>
    /// Total number of typing events emitted (Counter).
    /// Tags: event_type (typing, stop_typing)
    /// </summary>
    public const string ChannelTypingEvents = "argon-channel-typing-events";

    /// <summary>
    /// Total number of channel member kicks (Counter).
    /// Tags: result (success, no_permission, invalid_channel)
    /// </summary>
    public const string ChannelMemberKicks = "argon-channel-member-kicks";
}