namespace Argon.Grains;

using System.Diagnostics;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using Argon.Features.Logic;
using Argon.Features.Storage;
using Argon.Services;
using Instruments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Orleans.Providers;
using Persistence.States;

public class AccountDeletionGrain(
    [PersistentState("account-deletion-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<AccountDeletionGrainState> state,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPasswordHashingService passwordService,
    IUserPresenceService presenceService,
    IOptions<AccountDeletionOptions> options,
    IGrainFactory grainFactory,
    ILogger<AccountDeletionGrain> logger) : Grain, IAccountDeletionGrain
{
    private IDisposable? _checkTimer;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private const int FileBatchSize = 50;

    private Guid UserId => this.GetPrimaryKey();
    private AccountDeletionOptions Options => options.Value;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (state.State.Status == AccountDeletionStatus.Scheduled)
            _checkTimer = this.RegisterGrainTimer(
                static async (grain, _) => await grain.CheckAndExecuteAsync(),
                this,
                CheckInterval, CheckInterval);

        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        _checkTimer?.Dispose();
        return Task.CompletedTask;
    }

    public async ValueTask<AccountDeletionRequestResult> RequestDeletionAsync(string password)
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("RequestDeletion");
        activity?.SetTag("user.id", UserId);
        AccountDeletionInstrument.DeletionsRequested.Add(1);

        if (state.State.Status == AccountDeletionStatus.Scheduled)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.AlreadyScheduled,
                ScheduledDeletionAt = state.State.ExecutionAt
            };

        if (state.State.Status is AccountDeletionStatus.Executing or AccountDeletionStatus.Completed)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.AlreadyScheduled
            };

        await using var ctx = await dbFactory.CreateDbContextAsync();
        var user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == UserId);

        if (user is null)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.InternalError
            };

        // Verify password
        if (!passwordService.VerifyPassword(password, user))
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1, new KeyValuePair<string, object?>("reason", "invalid_password"));
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.InvalidPassword
            };
        }

        // Check lockdown
        if (user.LockdownReason != LockdownReason.NONE)
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1, new KeyValuePair<string, object?>("reason", "account_locked"));
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.AccountLocked
            };
        }

        // Check active Ultima subscription
        if (user.HasActiveUltima)
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1, new KeyValuePair<string, object?>("reason", "active_subscription"));
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.HasActiveSubscription
            };
        }

        // Check owned spaces
        var ownsSpaces = await ctx.Spaces
            .AnyAsync(s => s.CreatorId == UserId && !s.IsDeleted);

        if (ownsSpaces)
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1, new KeyValuePair<string, object?>("reason", "owns_spaces"));
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.OwnsSpaces
            };
        }

        // Schedule deletion
        var now = DateTimeOffset.UtcNow;
        var executionAt = now.AddDays(Options.GracePeriodDays);

        state.State.Status = AccountDeletionStatus.Scheduled;
        state.State.ScheduledAt = now;
        state.State.ExecutionAt = executionAt;
        state.State.RemindersSent = [];
        state.State.OriginalEmail = user.Email;
        state.State.OriginalUsername = user.Username;
        state.State.OriginalDisplayName = user.DisplayName;
        state.State.FailureReason = null;
        state.State.CompletedAt = null;
        await state.WriteStateAsync();

        AccountDeletionInstrument.DeletionsScheduled.Add(1);

        // Start timer
        _checkTimer?.Dispose();
        _checkTimer = this.RegisterGrainTimer(
            static async (grain, _) => await grain.CheckAndExecuteAsync(),
            this,
            CheckInterval, CheckInterval);

        // Send email
        var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
        await emailManager.SendDeletionScheduledAsync(user.Email, user.DisplayName, executionAt);

        logger.LogInformation(
            "Account deletion scheduled for user {UserId}, execution at {ExecutionAt}",
            UserId, executionAt);

        return new AccountDeletionRequestResult
        {
            Success = true,
            ScheduledDeletionAt = executionAt
        };
    }

    public async ValueTask<AccountDeletionRequestResult> RequestAutoDeleteAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("RequestAutoDelete");
        activity?.SetTag("user.id", UserId);
        AccountDeletionInstrument.DeletionsRequested.Add(1,
            new KeyValuePair<string, object?>("trigger", "auto_inactivity"));

        if (state.State.Status is AccountDeletionStatus.Scheduled
            or AccountDeletionStatus.Executing
            or AccountDeletionStatus.Completed)
        {
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.AlreadyScheduled,
                ScheduledDeletionAt = state.State.ExecutionAt
            };
        }

        await using var ctx = await dbFactory.CreateDbContextAsync();
        var user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == UserId);

        if (user is null)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.InternalError
            };

        // Active subscription blocks auto-deletion
        if (user.HasActiveUltima)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.HasActiveSubscription
            };

        // Schedule deletion
        var now = DateTimeOffset.UtcNow;
        var executionAt = now.AddDays(Options.GracePeriodDays);

        state.State.Status = AccountDeletionStatus.Scheduled;
        state.State.ScheduledAt = now;
        state.State.ExecutionAt = executionAt;
        state.State.RemindersSent = [];
        state.State.OriginalEmail = user.Email;
        state.State.OriginalUsername = user.Username;
        state.State.OriginalDisplayName = user.DisplayName;
        state.State.FailureReason = null;
        state.State.CompletedAt = null;
        await state.WriteStateAsync();

        AccountDeletionInstrument.DeletionsScheduled.Add(1);

        // Start timer
        _checkTimer?.Dispose();
        _checkTimer = this.RegisterGrainTimer(
            static async (grain, _) => await grain.CheckAndExecuteAsync(),
            this,
            CheckInterval, CheckInterval);

        // Send inactivity notice email (existing template for inactive accounts)
        var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
        await emailManager.SendDeleteNoticeAsync(user.Email, user.DisplayName, executionAt);

        logger.LogInformation(
            "Auto-delete scheduled for inactive user {UserId}, execution at {ExecutionAt}",
            UserId, executionAt);

        return new AccountDeletionRequestResult
        {
            Success = true,
            ScheduledDeletionAt = executionAt
        };
    }

    public async ValueTask<AccountDeletionCancelResult> CancelDeletionAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("CancelDeletion");
        activity?.SetTag("user.id", UserId);

        switch (state.State.Status)
        {
            case AccountDeletionStatus.None:
                return new AccountDeletionCancelResult { Success = false, Error = AccountDeletionCancelError.NotScheduled };
            case AccountDeletionStatus.Executing:
                return new AccountDeletionCancelResult { Success = false, Error = AccountDeletionCancelError.AlreadyExecuting };
            case AccountDeletionStatus.Completed:
                return new AccountDeletionCancelResult { Success = false, Error = AccountDeletionCancelError.AlreadyCompleted };
        }

        var email = state.State.OriginalEmail;
        var displayName = state.State.OriginalDisplayName;

        state.State.Status = AccountDeletionStatus.None;
        state.State.ScheduledAt = null;
        state.State.ExecutionAt = null;
        state.State.RemindersSent = [];
        state.State.OriginalEmail = null;
        state.State.OriginalUsername = null;
        state.State.OriginalDisplayName = null;
        await state.WriteStateAsync();

        _checkTimer?.Dispose();
        _checkTimer = null;

        AccountDeletionInstrument.DeletionsCancelled.Add(1);

        // Send cancellation email
        if (!string.IsNullOrEmpty(email))
        {
            var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
            await emailManager.SendDeletionCancelledAsync(email, displayName ?? "User");
        }

        logger.LogInformation("Account deletion cancelled for user {UserId}", UserId);

        return new AccountDeletionCancelResult { Success = true };
    }

    public ValueTask<AccountDeletionStatusDto> GetDeletionStatusAsync()
    {
        var dto = new AccountDeletionStatusDto
        {
            Status = state.State.Status switch
            {
                AccountDeletionStatus.Scheduled => AccountDeletionStatusKind.Scheduled,
                AccountDeletionStatus.Executing => AccountDeletionStatusKind.Executing,
                AccountDeletionStatus.Completed => AccountDeletionStatusKind.Completed,
                AccountDeletionStatus.Failed    => AccountDeletionStatusKind.Failed,
                _                               => AccountDeletionStatusKind.None
            },
            ScheduledAt = state.State.ScheduledAt,
            ExecutionAt = state.State.ExecutionAt,
            CompletedAt = state.State.CompletedAt,
            FailureReason = state.State.FailureReason
        };

        return ValueTask.FromResult(dto);
    }

    public async ValueTask CheckAndExecuteAsync()
    {
        if (state.State.Status != AccountDeletionStatus.Scheduled)
            return;

        var now = DateTimeOffset.UtcNow;
        var executionAt = state.State.ExecutionAt!.Value;
        var remaining = executionAt - now;

        // Check and send reminders
        foreach (var reminderDay in Options.ReminderDays)
        {
            if (remaining.TotalDays <= reminderDay && !state.State.RemindersSent.Contains(reminderDay))
            {
                state.State.RemindersSent.Add(reminderDay);
                await state.WriteStateAsync();

                AccountDeletionInstrument.DeletionRemindersSent.Add(1,
                    new KeyValuePair<string, object?>("days_before", reminderDay));

                if (!string.IsNullOrEmpty(state.State.OriginalEmail))
                {
                    var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
                    await emailManager.SendDeletionReminderAsync(
                        state.State.OriginalEmail,
                        state.State.OriginalDisplayName ?? "User",
                        reminderDay);
                }

                logger.LogInformation(
                    "Deletion reminder sent for user {UserId}, {Days} days remaining",
                    UserId, reminderDay);
            }
        }

        // Execute if time has arrived
        if (now >= executionAt)
            await ExecuteDeletionAsync();
    }

    private async Task ExecuteDeletionAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("ExecuteDeletion");
        activity?.SetTag("user.id", UserId);
        var sw = Stopwatch.StartNew();

        logger.LogInformation("Starting account deletion execution for user {UserId}", UserId);

        state.State.Status = AccountDeletionStatus.Executing;
        await state.WriteStateAsync();

        try
        {
            // 1. Invalidate all sessions
            await InvalidateSessionsAsync();

            // 2. Anonymize user and profile
            await AnonymizeUserAsync();

            // 3. Reserve old username
            await ReserveUsernameAsync();

            // 4. Hard-delete private data
            await DeletePrivateDataAsync();

            // 5. Soft-delete space memberships
            await SoftDeleteMembershipsAsync();

            // 6. Soft-delete owned bots
            await SoftDeleteBotsAsync();

            // 7. Decrement file references
            await DecrementFileRefsAsync();

            // 8. Clean up conversations
            await CleanupConversationsAsync();

            // 9. Send completion email
            if (!string.IsNullOrEmpty(state.State.OriginalEmail))
            {
                var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
                await emailManager.SendDeletionCompletedAsync(
                    state.State.OriginalEmail,
                    state.State.OriginalDisplayName ?? "User");
            }

            // 10. Mark completed
            state.State.Status = AccountDeletionStatus.Completed;
            state.State.CompletedAt = DateTimeOffset.UtcNow;
            await state.WriteStateAsync();

            _checkTimer?.Dispose();
            _checkTimer = null;

            sw.Stop();
            AccountDeletionInstrument.DeletionsCompleted.Add(1);
            AccountDeletionInstrument.DeletionExecutionDuration.Record(sw.Elapsed.TotalSeconds);

            logger.LogInformation(
                "Account deletion completed for user {UserId} in {Duration:F1}s",
                UserId, sw.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            state.State.Status = AccountDeletionStatus.Failed;
            state.State.FailureReason = ex.Message;
            await state.WriteStateAsync();

            AccountDeletionInstrument.DeletionsFailed.Add(1);

            logger.LogError(ex, "Account deletion failed for user {UserId}", UserId);
        }
    }

    private async Task InvalidateSessionsAsync()
    {
        try
        {
            var sessionIds = await presenceService.GetActiveSessionIdsAsync(UserId);
            foreach (var sessionId in sessionIds)
            {
                await presenceService.RemoveActivityPresence(UserId, sessionId);
                await presenceService.RemoveSessionAsync(UserId, sessionId);
            }

            logger.LogInformation("Invalidated {Count} sessions for user {UserId}", sessionIds.Count, UserId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to invalidate some sessions for user {UserId}", UserId);
        }
    }

    private async Task AnonymizeUserAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        var user = await ctx.Users
            .IgnoreQueryFilters()
            .FirstAsync(x => x.Id == userId);

        // Decrement avatar file ref if present
        if (!string.IsNullOrEmpty(user.AvatarFileId))
        {
            try
            {
                var avatarKey = user.AvatarFileId.Contains('/')
                    ? user.AvatarFileId.Split('/')[^1]
                    : user.AvatarFileId;
                if (Guid.TryParse(avatarKey, out var avatarFileId))
                    await grainFactory.GetGrain<IFileStorageGrain>(userId).DecrementRefAsync(avatarFileId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to decrement avatar file ref for user {UserId}", userId);
            }
        }

        user.DisplayName = "Deleted Account";
        user.Username = $"deleted_{Guid.NewGuid():N}";
        user.Email = $"deleted_{userId}@void.local";
        user.PhoneNumber = null;
        user.PasswordDigest = null;
        user.AvatarFileId = null;
        user.IsDeleted = true;
        user.DeletedAt = DateTimeOffset.UtcNow;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.LockdownReason = LockdownReason.NONE;
        user.LockDownExpiration = null;
        user.HasActiveUltima = false;

        ctx.Users.Update(user);

        // Anonymize profile
        var profile = await ctx.UserProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.UserId == userId);

        if (profile is not null)
        {
            profile.CustomStatus = null;
            profile.CustomStatusIconId = null;
            profile.Bio = null;
            profile.Badges = [];
            profile.BackgroundId = 0;
            profile.VoiceCardEffectId = 0;
            profile.AvatarFrameId = 0;
            profile.NickEffectId = 0;
            profile.PrimaryColor = 0;
            profile.AccentColor = 0;
            ctx.UserProfiles.Update(profile);
        }

        await ctx.SaveChangesAsync();

        logger.LogInformation("Anonymized user entity and profile for user {UserId}", userId);
    }

    private async Task ReserveUsernameAsync()
    {
        if (string.IsNullOrEmpty(state.State.OriginalUsername))
            return;

        try
        {
            await using var ctx = await dbFactory.CreateDbContextAsync();
            var reserved = new UsernameReservedEntity
            {
                Id = Guid.NewGuid(),
                UserName = state.State.OriginalUsername,
                NormalizedUserName = state.State.OriginalUsername.ToLowerInvariant(),
                IsBanned = false,
                IsReserved = true
            };

            ctx.Add(reserved);
            await ctx.SaveChangesAsync();

            logger.LogInformation("Reserved username '{Username}' for deleted user {UserId}",
                state.State.OriginalUsername, UserId);
        }
        catch (Exception ex)
        {
            // May fail if already reserved — non-critical
            logger.LogWarning(ex, "Failed to reserve username for user {UserId}", UserId);
        }
    }

    private async Task DeletePrivateDataAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        // Friends (both directions)
        await ctx.Friends
            .Where(f => f.UserId == userId || f.FriendId == userId)
            .ExecuteDeleteAsync();

        // Blocks (both directions)
        await ctx.UserBlocklist
            .Where(b => b.UserId == userId || b.BlockedId == userId)
            .ExecuteDeleteAsync();

        // Mute settings
        await ctx.MuteSettings
            .Where(m => m.UserId == userId)
            .ExecuteDeleteAsync();

        // Auto-delete settings
        await ctx.AutoDeleteSettings
            .Where(a => a.UserId == userId)
            .ExecuteDeleteAsync();

        // Device histories
        await ctx.DeviceHistories
            .Where(d => d.UserId == userId)
            .ExecuteDeleteAsync();

        // Passkeys
        await ctx.Passkeys
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();

        // Subscriptions
        await ctx.UltimaSubscriptions
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        // Space boosts
        await ctx.SpaceBoosts
            .Where(b => b.UserId == userId)
            .ExecuteDeleteAsync();

        // Payment transactions
        await ctx.PaymentTransactions
            .Where(t => t.UserId == userId)
            .ExecuteDeleteAsync();

        // Daily stats
        await ctx.UserDailyStats
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        // User levels
        await ctx.UserLevels
            .Where(l => l.UserId == userId)
            .ExecuteDeleteAsync();

        logger.LogInformation("Deleted private data for user {UserId}", userId);
    }

    private async Task SoftDeleteMembershipsAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        await ctx.UsersToServerRelations
            .Where(m => m.UserId == userId && !m.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.IsDeleted, true)
                .SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow)
                .SetProperty(m => m.UpdatedAt, DateTimeOffset.UtcNow));

        logger.LogInformation("Soft-deleted space memberships for user {UserId}", userId);
    }

    private async Task SoftDeleteBotsAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        // Find teams owned by this user
        var teamIds = await ctx.TeamEntities
            .Where(t => t.OwnerId == userId)
            .Select(t => t.TeamId)
            .ToListAsync();

        if (teamIds.Count == 0)
            return;

        // Soft-delete all bots belonging to those teams
        await ctx.BotEntities
            .Where(b => teamIds.Contains(b.TeamId) && !b.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.IsDeleted, true)
                .SetProperty(b => b.DeletedAt, DateTimeOffset.UtcNow)
                .SetProperty(b => b.UpdatedAt, DateTimeOffset.UtcNow));

        // Soft-delete the teams themselves
        await ctx.TeamEntities
            .Where(t => t.OwnerId == userId && !t.IsDeleted)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.IsDeleted, true)
                .SetProperty(t => t.DeletedAt, DateTimeOffset.UtcNow)
                .SetProperty(t => t.UpdatedAt, DateTimeOffset.UtcNow));

        logger.LogInformation("Soft-deleted {Count} bot team(s) for user {UserId}", teamIds.Count, userId);
    }

    private async Task DecrementFileRefsAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        var fileIds = await ctx.Files
            .Where(f => f.OwnerId == userId && !f.IsDeleted)
            .Select(f => f.Id)
            .ToListAsync();

        if (fileIds.Count == 0)
            return;

        var fileGrain = grainFactory.GetGrain<IFileStorageGrain>(userId);
        var failed = 0;

        foreach (var batch in fileIds.Chunk(FileBatchSize))
        {
            foreach (var fileId in batch)
            {
                try
                {
                    await fileGrain.DecrementRefAsync(fileId);
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogWarning(ex, "Failed to decrement ref for file {FileId}, user {UserId}", fileId, userId);
                }
            }
        }

        logger.LogInformation(
            "Decremented file refs for user {UserId}: {Total} total, {Failed} failed",
            userId, fileIds.Count, failed);
    }

    private async Task CleanupConversationsAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        await ctx.UserConversations
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync();

        logger.LogInformation("Cleaned up conversations for user {UserId}", userId);
    }
}
