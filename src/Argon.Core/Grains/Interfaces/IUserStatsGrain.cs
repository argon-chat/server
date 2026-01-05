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
    /// Records time spent in voice channel.
    /// Should be called when user leaves a voice channel.
    /// </summary>
    [Alias(nameof(RecordVoiceTimeAsync))]
    [OneWay]
    ValueTask RecordVoiceTimeAsync(int durationSeconds, Guid channelId, Guid spaceId);

    /// <summary>
    /// Increments the call counter for today.
    /// Called when user joins a voice channel.
    /// </summary>
    [Alias(nameof(IncrementCallsAsync))]
    [OneWay]
    ValueTask IncrementCallsAsync();

    /// <summary>
    /// Increments the message counter for today.
    /// Called when user sends a message.
    /// </summary>
    [Alias(nameof(IncrementMessagesAsync))]
    [OneWay]
    ValueTask IncrementMessagesAsync();

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
