namespace Argon.Grains.Interfaces;

using Orleans.Concurrency;

/// <summary>
/// Grain for tracking user daily statistics.
/// Keyed by UserId.
/// Uses Redis for hot data with periodic flush to database.
/// </summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IUserStatsGrain)}")]
public interface IUserStatsGrain : IGrainWithGuidKey
{
    /// <summary>
    /// Records time spent in voice channel (fire-and-forget for production).
    /// </summary>
    [Alias(nameof(RecordVoiceTimeAsync))]
    [OneWay]
    ValueTask RecordVoiceTimeAsync(int durationSeconds, Guid channelId, Guid spaceId);

    /// <summary>
    /// Increments the call counter for today (fire-and-forget for production).
    /// </summary>
    [Alias(nameof(IncrementCallsAsync))]
    [OneWay]
    ValueTask IncrementCallsAsync();

    /// <summary>
    /// Increments the message counter for today (fire-and-forget for production).
    /// </summary>
    [Alias(nameof(IncrementMessagesAsync))]
    [OneWay]
    ValueTask IncrementMessagesAsync();

    /// <summary>
    /// Records voice time and waits for completion (for testing).
    /// </summary>
    [Alias(nameof(RecordVoiceTimeAndWaitAsync))]
    ValueTask RecordVoiceTimeAndWaitAsync(int durationSeconds, Guid channelId, Guid spaceId);

    /// <summary>
    /// Increments calls and waits for completion (for testing).
    /// </summary>
    [Alias(nameof(IncrementCallsAndWaitAsync))]
    ValueTask IncrementCallsAndWaitAsync();

    /// <summary>
    /// Increments messages and waits for completion (for testing).
    /// </summary>
    [Alias(nameof(IncrementMessagesAndWaitAsync))]
    ValueTask IncrementMessagesAndWaitAsync();

    /// <summary>
    /// Gets statistics for today.
    /// </summary>
    [Alias(nameof(GetTodayStatsAsync))]
    ValueTask<TodayStats> GetTodayStatsAsync();

    /// <summary>
    /// Forces flush of current stats to database.
    /// Called periodically by a timer or on grain deactivation.
    /// </summary>
    [Alias(nameof(FlushToDatabaseAsync))]
    ValueTask FlushToDatabaseAsync();
}
