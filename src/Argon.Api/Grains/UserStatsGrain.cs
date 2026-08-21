namespace Argon.Grains;

using Argon.Core.Entities.Data;
using Microsoft.EntityFrameworkCore;
using Orleans.Providers;
using Persistence.States;

/// <summary>
/// Tracks daily statistics for a user.
/// Uses Redis for hot data with periodic flush to PostgreSQL.
/// 
/// Design decisions:
/// - Stats accumulate in Redis for fast writes
/// - Periodic timer flushes to database every 5 minutes
/// - On deactivation, final flush occurs
/// - On day change, old stats are flushed and new day starts
/// </summary>
public class UserStatsGrain(
    [PersistentState("user-stats-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<UserStatsGrainState> state,
    IDbContextFactory<ApplicationDbContext> context,
    IGrainFactory grainFactory,
    ILogger<UserStatsGrain> logger) : Grain, IUserStatsGrain
{
    private IDisposable? _flushTimer;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// XP per minute in voice channel.
    /// With ~500 hours/year = 30,000 minutes, at 2 XP/min = 60,000 XP
    /// Total XP for 100 levels ≈ 75,000 (achievable with active use).
    /// </summary>
    private const int XpPerVoiceMinute = 2;

    /// <summary>
    /// XP per message sent.
    /// Capped at 50 XP per day from messages to prevent spam farming.
    /// </summary>
    private const int XpPerMessage = 1;
    private const int MaxMessageXpPerDay = 50;
    private const int MessagesPerXp = 10;

    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await state.ReadStateAsync(cancellationToken);

        // Check if day changed
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (state.State.CurrentDate != today)
        {
            // Flush old stats and reset for new day
            if (state.State.IsDirty)
            {
                await FlushToDatabaseInternalAsync();
            }

            state.State = new UserStatsGrainState { CurrentDate = today };
            await state.WriteStateAsync(cancellationToken);
        }

        // Set up periodic flush timer
        _flushTimer = this.RegisterGrainTimer(
            static async (grain, _) => await grain.FlushToDatabaseAsync(),
            this,
            FlushInterval,
            FlushInterval);
    }

    public async override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _flushTimer?.Dispose();

        if (state.State.IsDirty)
        {
            await FlushToDatabaseInternalAsync();
        }
    }

    public async ValueTask RecordVoiceTimeAsync(int durationSeconds, Guid channelId, Guid spaceId)
        => await RecordVoiceTimeInternalAsync(durationSeconds, channelId, spaceId);

    public async ValueTask RecordVoiceTimeAndWaitAsync(int durationSeconds, Guid channelId, Guid spaceId)
        => await RecordVoiceTimeInternalAsync(durationSeconds, channelId, spaceId);

    private async ValueTask RecordVoiceTimeInternalAsync(int durationSeconds, Guid channelId, Guid spaceId)
    {
        EnsureCorrectDay();

        state.State.TimeInVoiceSeconds += durationSeconds;
        state.State.IsDirty = true;

        // Award XP for voice time
        var minutes = durationSeconds / 60;
        if (minutes > 0)
        {
            var xpToAward = minutes * XpPerVoiceMinute;
            state.State.XpEarnedToday += xpToAward;

            // Fire and forget XP award to level grain
            var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(this.GetPrimaryKey());
            await levelGrain.AwardXpAsync(xpToAward, XpSource.Voice);
        }

        await state.WriteStateAsync();
    }

    public async ValueTask IncrementCallsAsync()
        => await IncrementCallsInternalAsync();

    public async ValueTask IncrementCallsAndWaitAsync()
        => await IncrementCallsInternalAsync();

    private async ValueTask IncrementCallsInternalAsync()
    {
        EnsureCorrectDay();

        state.State.CallsMade++;
        state.State.IsDirty = true;

        await state.WriteStateAsync();
    }

    public async ValueTask IncrementMessagesAsync()
        => await IncrementMessagesInternalAsync();

    public async ValueTask IncrementMessagesAndWaitAsync()
        => await IncrementMessagesInternalAsync();

    private async ValueTask IncrementMessagesInternalAsync()
    {
        EnsureCorrectDay();

        state.State.MessagesSent++;
        state.State.IsDirty = true;

        // Small XP bonus for messages (1 XP per 10 messages, capped at 50/day from messages)
        // This prevents spam farming while still rewarding engagement
        if (state.State.MessageXpEarnedToday < MaxMessageXpPerDay && state.State.MessagesSent % MessagesPerXp == 0)
        {
            var xpToAward = XpPerMessage;
            
            // Check if we would exceed the cap
            var potentialTotal = state.State.MessageXpEarnedToday + xpToAward;
            if (potentialTotal > MaxMessageXpPerDay)
            {
                xpToAward = MaxMessageXpPerDay - state.State.MessageXpEarnedToday;
            }

            if (xpToAward > 0)
            {
                state.State.MessageXpEarnedToday += xpToAward;
                state.State.XpEarnedToday += xpToAward;

                var levelGrain = grainFactory.GetGrain<IUserLevelGrain>(this.GetPrimaryKey());
                await levelGrain.AwardXpAsync(xpToAward, XpSource.Message);
            }
        }
        
        await state.WriteStateAsync();
    }

    public ValueTask<TodayStats> GetTodayStatsAsync()
    {
        EnsureCorrectDay();

        // Convert seconds to minutes for display
        var timeInVoiceMinutes = state.State.TimeInVoiceSeconds / 60;

        return ValueTask.FromResult(new TodayStats(
            timeInVoiceMinutes,
            state.State.CallsMade,
            state.State.MessagesSent));
    }

    public async ValueTask FlushToDatabaseAsync()
    {
        if (!state.State.IsDirty)
            return;

        await FlushToDatabaseInternalAsync();
    }

    private async Task FlushToDatabaseInternalAsync()
    {
        try
        {
            await using var ctx = await context.CreateDbContextAsync();

            var userId = this.GetPrimaryKey();
            var date = state.State.CurrentDate;

            var existingStats = await ctx.UserDailyStats
                .FirstOrDefaultAsync(x => x.UserId == userId && x.Date == date);

            if (existingStats is not null)
            {
                // Update existing record
                existingStats.TimeInVoiceSeconds = state.State.TimeInVoiceSeconds;
                existingStats.CallsMade = state.State.CallsMade;
                existingStats.MessagesSent = state.State.MessagesSent;
                existingStats.XpEarned = state.State.XpEarnedToday;

                ctx.UserDailyStats.Update(existingStats);
            }
            else
            {
                // Insert new record
                await ctx.UserDailyStats.AddAsync(new UserDailyStatsEntity
                {
                    UserId = userId,
                    Date = date,
                    TimeInVoiceSeconds = state.State.TimeInVoiceSeconds,
                    CallsMade = state.State.CallsMade,
                    MessagesSent = state.State.MessagesSent,
                    XpEarned = state.State.XpEarnedToday
                });
            }

            await ctx.SaveChangesAsync();

            state.State.LastFlush = DateTimeOffset.UtcNow;
            state.State.IsDirty = false;

            await state.WriteStateAsync();

            logger.LogDebug("Flushed stats for user {UserId} for {Date}", userId, date);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to flush stats to database for user {UserId}", this.GetPrimaryKey());
        }
    }

    private void EnsureCorrectDay()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (state.State.CurrentDate != today)
        {
            // Day changed - this is handled in OnActivateAsync normally
            // but if grain was long-running, reset here
            state.State = new UserStatsGrainState { CurrentDate = today };
        }
    }
}
