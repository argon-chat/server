namespace Argon.Grains;

using Microsoft.Extensions.Caching.Hybrid;
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

/// <summary>
/// The countdown in front of an account erasure, and the erasure itself.
/// </summary>
/// <remarks>
/// <para><b>The execution is a resumable sequence, not a straight line.</b> It writes
/// <see cref="AccountDeletionStatus.Executing"/> before it does any work and then anonymises the row
/// as its third step, so an activation lost anywhere after that used to leave an account that could
/// not sign in, could not be cancelled, could not be re-requested and still held every private row
/// the run had not reached — for ever, on an ordinary deploy restart (defect ACC-15). Each step is
/// therefore recorded in <see cref="AccountDeletionGrainState.StepsDone"/> as it completes and skipped
/// if it is already there, the poll is a durable Orleans reminder rather than a volatile grain timer
/// so something still arrives after the account can no longer be signed into, and both
/// <c>Executing</c> and <c>Failed</c> are picked up again by <see cref="CheckAndExecuteAsync"/>.</para>
///
/// <para><b>Step order is load-bearing.</b> Sessions end first, while the account is still whole:
/// <c>UserSessionGrain.FinalizeOfflineAsync</c> resolves the spaces to leave through <c>ctx.Users</c>
/// under the soft-delete filter and the membership rows, so a teardown after step 3 or step 6 would
/// find nothing to broadcast and nothing to vacate (defects ACC-02, ACC-04). The export archive goes
/// next, before the row is anonymised, because it is a complete copy of everything the following
/// steps are about to erase (ACC-10/X5).</para>
/// </remarks>
public class AccountDeletionGrain(
    [PersistentState("account-deletion-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<AccountDeletionGrainState> state,
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IPasswordHashingService passwordService,
    IUserPresenceService presenceService,
    IArgonCacheDatabase revocationStore,
    ISessionRevocationBroadcaster revocations,
    IExportS3Service exportStorage,
    IOptions<AccountDeletionOptions> options,
    IOptions<Orleans.Hosting.ReminderOptions> reminderOptions,
    IGrainFactory grainFactory,
    HybridCache cache,
    ILogger<AccountDeletionGrain> logger) : Grain, IAccountDeletionGrain, IRemindable
{
    private const int FileBatchSize = 50;

    /// <summary>The durable poll that sends reminders, executes an elapsed grace and resumes a lost run.</summary>
    /// <remarks>
    /// A reminder rather than the <c>RegisterGrainTimer</c> it replaces, because a timer only exists
    /// while an activation does. A scheduled deletion whose silo went away had nothing left to fire it
    /// until somebody happened to open the console, and an interrupted execution had nothing at all —
    /// the account could no longer sign in, so nobody was ever going to touch the grain again
    /// (ACC-15). <c>ExportPumpGrain</c> and <c>AutoDeleteSchedulerGrain</c> already use a reminder for
    /// exactly this reason.
    /// </remarks>
    private const string CheckReminderName = "account-deletion-check";

    /// <summary>
    /// The steps of <see cref="ExecuteDeletionAsync"/>, as the cursor remembers them.
    /// </summary>
    /// <remarks>
    /// Stable integers, never reordered or reused: a record written by a running deployment is read
    /// back by the next one, and renumbering a step would make a resumption skip work that never
    /// happened. New steps take the next free number rather than a place in the sequence.
    /// </remarks>
    private static class Step
    {
        public const int Sessions      = 1;
        public const int Exports       = 2;
        public const int Anonymize     = 3;
        public const int Username      = 4;
        public const int PrivateData   = 5;
        public const int Memberships   = 6;
        public const int Bots          = 7;
        public const int FileRefs      = 8;
        public const int Conversations = 9;
        public const int Notified      = 10;

        /// <summary>The private spaces this account owned, deleted with it.</summary>
        /// <remarks>
        /// Numbered after the steps that shipped before it and run before <see cref="Memberships"/>,
        /// which is the order that matters: a space deleted here has no memberships left for that step
        /// to walk. The number is only an identity in the cursor, so appending is safe — a deletion
        /// interrupted by the release that added this step resumes with this step not yet done, which
        /// is correct.
        /// </remarks>
        public const int Spaces        = 11;

        /// <summary>
        /// The steps that erase nothing about the account, and that a cancellation may discard.
        /// </summary>
        /// <remarks>
        /// Listed rather than derived from the ordering ("anything below <see cref="Anonymize"/>"),
        /// because the numbers above are stable identities and not a sequence — the rule on this class
        /// is that a new step takes the next free number rather than a place in the run, so an
        /// arithmetic test would misread the first preparation step somebody adds. Anything absent
        /// from this set counts as irreversible, which is the right default for a step whose author
        /// did not think about <see cref="ErasureHasBegunAsync"/> at all.
        /// </remarks>
        public static readonly IReadOnlySet<int> ReversiblePreparation = new HashSet<int> { Sessions, Exports };
    }

    private Guid UserId => this.GetPrimaryKey();
    private AccountDeletionOptions Options => options.Value;

    /// <summary>
    /// The poll that sends reminders and executes an elapsed grace.
    /// </summary>
    /// <remarks>
    /// Six hours by default, exactly as the constant it replaces; a host may lower it, and
    /// <see cref="AccountDeletionOptions.Validate"/> refuses a value wide enough to step over the
    /// closest reminder.
    /// </remarks>
    private TimeSpan CheckInterval => Options.CheckInterval;

    /// <summary>
    /// The same interval, raised to whatever Orleans will actually accept as a reminder period.
    /// </summary>
    /// <remarks>
    /// <c>RegisterOrUpdateReminder</c> throws below <c>ReminderOptions.MinimumReminderPeriod</c> — one
    /// minute out of the box, and lowered to seconds by <c>PresenceFeature</c> from
    /// <c>PresenceTimingOptions.ReminderFloor</c> so the integration host can run compressed clocks.
    /// Reading the floor rather than assuming it is what keeps a host whose <see cref="CheckInterval"/>
    /// is shorter than the floor from throwing out of the middle of a deletion request; the deletion
    /// is simply polled at the floor instead, which is the fastest the runtime will poll anything.
    /// </remarks>
    private TimeSpan CheckReminderPeriod
    {
        get
        {
            var floor = reminderOptions.Value.MinimumReminderPeriod;

            return CheckInterval < floor ? floor : CheckInterval;
        }
    }

    /// <summary>
    /// Re-arms the poll for any state that still has work in front of it.
    /// </summary>
    /// <remarks>
    /// <c>Executing</c> and <c>Failed</c> are here alongside <c>Scheduled</c>, and that is the whole
    /// of ACC-15's recovery: an activation that finds a run interrupted mid-flight, or one that ended
    /// in an exception with retries left, gets the tick that finishes it. A run that has exhausted
    /// <see cref="AccountDeletionOptions.MaxExecutionAttempts"/> is not re-armed — it is a state for a
    /// person to look at, not a loop to keep running.
    /// </remarks>
    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        if (NeedsPolling())
            await ArmCheckAsync();
    }

    /// <summary>
    /// An erasure that has stopped by itself: failed, out of attempts, and no longer polled.
    /// </summary>
    /// <remarks>
    /// Written as the negation of <see cref="NeedsPolling"/> rather than by repeating its bound, so
    /// the flag an operator acts on cannot drift away from the condition that actually stops the
    /// poll. Reported on <see cref="AccountDeletionStatusDto.Stranded"/>, because a half-erased
    /// account that nothing will ever touch again has to be visible to somebody: the person it
    /// belonged to can no longer sign in to ask.
    /// </remarks>
    private bool IsStranded
        => state.State.Status == AccountDeletionStatus.Failed && !NeedsPolling();

    private bool NeedsPolling()
        => state.State.Status switch
        {
            AccountDeletionStatus.Scheduled => true,
            AccountDeletionStatus.Executing => true,
            AccountDeletionStatus.Failed    => state.State.ExecutionAttempts < Options.MaxExecutionAttempts,
            _                               => false
        };

    /// <remarks>
    /// <para>Never allowed to fail the caller. A reminder that could not be registered costs the
    /// account its automatic poll until the next activation re-arms it; an exception here would
    /// instead make every call on this grain — including the console's <c>GetMe</c> — fail for as long
    /// as the reminder service is unhappy.</para>
    ///
    /// <para><paramref name="dueNow"/> is for <see cref="ResumeAsync"/>, and it is the difference
    /// between an operator's "resume" meaning something and appearing to do nothing: the period is six
    /// hours in production, so a resumption armed at the ordinary period would sit there until the
    /// next ordinary tick. <c>TimeSpan.Zero</c> is documented as a first tick as soon as the reminder
    /// service can deliver one.</para>
    /// </remarks>
    private async Task ArmCheckAsync(bool dueNow = false)
    {
        try
        {
            await this.RegisterOrUpdateReminder(
                CheckReminderName,
                dueNow ? TimeSpan.Zero : CheckReminderPeriod,
                CheckReminderPeriod);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not arm the deletion poll for user {UserId}", UserId);
        }
    }

    /// <remarks>Same contract as <see cref="ArmCheckAsync"/>: a poll that outlives its reason is noise, not a failure.</remarks>
    private async Task DisarmCheckAsync()
    {
        try
        {
            if (await this.GetReminder(CheckReminderName) is { } reminder)
                await this.UnregisterReminder(reminder);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not disarm the deletion poll for user {UserId}", UserId);
        }
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != CheckReminderName)
            return;

        await CheckAndExecuteAsync();
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
                Error = AccountDeletionRequestError.AlreadyScheduled,
                ScheduledDeletionAt = state.State.ExecutionAt
            };

        await using var ctx = await dbFactory.CreateDbContextAsync();

        // IgnoreQueryFilters, and the branch below it, is correction C7. A run that threw after step 3
        // leaves the row IsDeleted, which the global soft-delete filter hides — so the lookup answered
        // null and the person was told InternalError, for ever, on an account that had in fact already
        // been erased. There is no password left on that row to verify and nothing left to schedule:
        // the honest answer is the one the Completed branch above gives.
        var user = await ctx.Users.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == UserId);

        if (user is null)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.InternalError
            };

        if (user.IsDeleted)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.AlreadyScheduled,
                ScheduledDeletionAt = state.State.ExecutionAt
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

        if (await BarredAsync(ctx, user, trigger: "user") is { } barred)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = barred
            };

        var executionAt = BeginCountdown(user, AccountDeletionTrigger.User);
        await state.WriteStateAsync();

        AccountDeletionInstrument.DeletionsScheduled.Add(1);

        await ArmCheckAsync();
        await TrackAsync();

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

    public ValueTask<AccountDeletionRequestResult> RequestAutoDeleteAsync()
        => ScheduleInactivityAsync(honourDeclineHold: true, trigger: "auto_inactivity");

    /// <inheritdoc cref="IAccountDeletionGrain.StartByOperatorAsync"/>
    public ValueTask<AccountDeletionRequestResult> StartByOperatorAsync()
        => ScheduleInactivityAsync(honourDeclineHold: false, trigger: "operator");

    /// <summary>
    /// Arms the countdown the inactivity notice describes: the ordinary grace, the notice mail, and a
    /// deletion the account can still call off by signing in.
    /// </summary>
    /// <param name="honourDeclineHold">
    /// Whether a refusal the account holder already made stands in the way. It does for the sweep,
    /// which is the whole of defect CON-4, and does not for an operator, who is the person the hold
    /// defers to.
    /// </param>
    /// <param name="trigger">What the metrics and the bar's own counters record as the cause.</param>
    private async ValueTask<AccountDeletionRequestResult> ScheduleInactivityAsync(bool honourDeclineHold, string trigger)
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("RequestAutoDelete");
        activity?.SetTag("user.id", UserId);
        activity?.SetTag("deletion.trigger", trigger);
        AccountDeletionInstrument.DeletionsRequested.Add(1,
            new KeyValuePair<string, object?>("trigger", trigger));

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

        // The account holder has already answered this question (defect CON-4). Cancelling from the
        // console writes nothing the inactivity scan can read — it decides from
        // max(DeviceHistories.LastLoginTime) ?? Users.CreatedAt, and the console authenticates against
        // Aegis, which never records a login — so without this the sweeper re-scheduled the same
        // account on its very next pass, with a fresh notice mail and a fresh grace, indefinitely.
        if (honourDeclineHold
         && state.State.DeclinedAt is { } declinedAt
         && DateTimeOffset.UtcNow - declinedAt < Options.DeclineHoldsFor)
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "recently_declined"),
                new KeyValuePair<string, object?>("trigger", trigger));

            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = AccountDeletionRequestError.RecentlyDeclined
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

        if (await BarredAsync(ctx, user, trigger) is { } barred)
            return new AccountDeletionRequestResult
            {
                Success = false,
                Error = barred
            };

        var executionAt = BeginCountdown(user, AccountDeletionTrigger.AutoInactivity);
        await state.WriteStateAsync();

        AccountDeletionInstrument.DeletionsScheduled.Add(1);

        await ArmCheckAsync();
        await TrackAsync();

        // Send inactivity notice email (existing template for inactive accounts)
        var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
        await emailManager.SendDeleteNoticeAsync(user.Email, user.DisplayName, executionAt);

        logger.LogInformation(
            "Deletion scheduled for user {UserId} by {Trigger}, execution at {ExecutionAt}",
            UserId, trigger, executionAt);

        return new AccountDeletionRequestResult
        {
            Success = true,
            ScheduledDeletionAt = executionAt
        };
    }

    /// <summary>
    /// The bars that stand in front of an erasure whoever asked for it.
    /// </summary>
    /// <remarks>
    /// <para>Defect CON-3, pinned by
    /// <c>AccountConsoleTests.RunScan_AppliesTheSameBarsAsAUserRequestedDeletion</c>. The interactive
    /// path refused four things and the automatic one refused two: a dormant account that owns a live
    /// space was erased by the sweeper, leaving a space whose <c>CreatorId</c> points at "Deleted
    /// Account" and which nobody can ever delete, install a bot into or approve entitlements for —
    /// those are creator-only by design and there is no ownership-transfer call anywhere in the
    /// product. And an account under investigation was erased by a timer, taking the device history a
    /// case rests on with it, precisely because a locked account cannot sign in and is therefore
    /// guaranteed to reach the inactivity threshold. Neither reason gets weaker for coming from a
    /// timer, so the two lists are now one list and cannot drift again.</para>
    ///
    /// <para>Order matters and matches the order the console renders: lockdown, subscription,
    /// ownership. The password check stays on the interactive path alone — it is the only bar the
    /// sweeper has no way to satisfy, and the only one that is about the caller rather than the
    /// account.</para>
    ///
    /// <para>A lapsed timed lockdown does not bar anything, which is a deliberate difference from the
    /// bare <c>LockdownReason != NONE</c> this replaces: nothing clears the column when a timed ban
    /// runs out, so reading the reason alone would exempt every account that was ever muted for an
    /// hour from retention for the rest of time. Same rule
    /// <c>ArgonTransactionInterceptor.ResolveLockdownSeverityAsync</c> applies, for the same reason.</para>
    /// </remarks>
    private async Task<AccountDeletionRequestError?> BarredAsync(ApplicationDbContext ctx, UserEntity user, string trigger)
    {
        var userId = UserId;

        // Not a person's account, so none of what follows means anything: there is nobody to warn, no
        // console to cancel from, and the eleven steps would take an application's identity, its
        // membership of every space it serves and its messages with them. The platform account is the
        // same case with nobody at all behind it. Both are barred here, at the one gate every caller
        // passes — a person's own request, an operator's approval, and the sweep's proposal, which is
        // where this was found: the inactivity scan proposed the echo bot on its first pass in
        // production, because a bot's last activity is the day it was created and never moves.
        if (user.Id == UserEntity.SystemUser
         || await ctx.BotEntities.AnyAsync(bot => bot.BotAsUserId == userId))
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "service_account"),
                new KeyValuePair<string, object?>("trigger", trigger));

            return AccountDeletionRequestError.ServiceAccount;
        }

        var lockdownStands = user.LockdownReason != LockdownReason.NONE
                          && (user.LockDownExpiration is not { } expiry || expiry > DateTimeOffset.UtcNow);

        if (lockdownStands)
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "account_locked"),
                new KeyValuePair<string, object?>("trigger", trigger));

            return AccountDeletionRequestError.AccountLocked;
        }

        if (user.HasActiveUltima)
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "active_subscription"),
                new KeyValuePair<string, object?>("trigger", trigger));

            return AccountDeletionRequestError.HasActiveSubscription;
        }

        // Only a community stands in the way. A private space belongs to one person by construction —
        // their own room, or a room whose other members have themselves gone — and it is deleted with
        // the account rather than keeping it alive; a community is other people's home and needs an
        // owner to hand it over before its owner can leave.
        if (await ctx.Spaces.AnyAsync(s => s.CreatorId == userId && !s.IsDeleted && s.IsCommunity))
        {
            AccountDeletionInstrument.DeletionsRejected.Add(1,
                new KeyValuePair<string, object?>("reason", "owns_communities"),
                new KeyValuePair<string, object?>("trigger", trigger));

            return AccountDeletionRequestError.OwnsSpaces;
        }

        return null;
    }

    /// <summary>
    /// Tells the queue's register what this deletion is doing now.
    /// </summary>
    /// <remarks>
    /// The register is what makes "which accounts are being deleted right now" answerable: the state
    /// lives here, one grain per account, and nothing else lists them. Fenced because it is
    /// bookkeeping for a console — a queue that will not answer must never fail a deletion, and the
    /// next transition writes the register again anyway.
    /// </remarks>
    private async Task TrackAsync()
    {
        try
        {
            await grainFactory.GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId)
               .TrackDeletionAsync(
                    UserId,
                    state.State.Status switch
                    {
                        AccountDeletionStatus.Scheduled => AccountDeletionStatusKind.Scheduled,
                        AccountDeletionStatus.Executing => AccountDeletionStatusKind.Executing,
                        AccountDeletionStatus.Completed => AccountDeletionStatusKind.Completed,
                        AccountDeletionStatus.Failed    => AccountDeletionStatusKind.Failed,
                        _                               => AccountDeletionStatusKind.None
                    },
                    state.State.ExecutionAt,
                    state.State.Trigger is AccountDeletionTrigger.User);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not record the deletion of {UserId} in the queue's register", UserId);
        }
    }

    /// <summary>Puts the state into a fresh countdown and answers when it runs out.</summary>
    /// <remarks>
    /// The cursor and the attempt count are cleared alongside the reminder bookkeeping: this is a new
    /// deletion, not a resumption of the one that was cancelled or that failed, so none of the
    /// previous run's steps may be treated as already done. The owed departure announcements go with
    /// them — they belong to the run that was abandoned, and the new run walks the memberships from
    /// the database anyway, so replaying them would announce a departure that never happened.
    /// </remarks>
    private DateTimeOffset BeginCountdown(UserEntity user, AccountDeletionTrigger trigger)
    {
        var now         = DateTimeOffset.UtcNow;
        var executionAt = now + Options.EffectiveGracePeriod;

        state.State.Status                        = AccountDeletionStatus.Scheduled;
        state.State.ScheduledAt                   = now;
        state.State.ExecutionAt                   = executionAt;
        state.State.RemindersSent                 = [];
        state.State.StepsDone                     = [];
        state.State.PendingDepartureAnnouncements = [];
        state.State.ExecutionAttempts             = 0;
        state.State.OriginalEmail                 = user.Email;
        state.State.OriginalUsername              = user.Username;
        state.State.OriginalDisplayName           = user.DisplayName;
        state.State.FailureReason                 = null;
        state.State.CompletedAt                   = null;
        state.State.Trigger                       = trigger;
        state.State.Silent                        = false;

        return executionAt;
    }

    /// <summary>
    /// Calls a deletion off, when there is still a deletion to call off.
    /// </summary>
    /// <remarks>
    /// <para><b>A <c>Failed</c> run that has already started erasing is not one of them.</b> The reset
    /// below is total — the cursor, the attempt count, the schedule and the three <c>Original*</c>
    /// identity fields all go, and the poll is disarmed — which is exactly right for a countdown
    /// nobody has acted on yet, and catastrophic for a run that threw at step 5. That account is
    /// already anonymised, its credentials are already revoked and its export archive is already
    /// destroyed, while its friend requests, passkeys, device history, pending contact changes,
    /// payments and memberships are still there. Cancelling it told the person "nothing is scheduled",
    /// threw away the cursor that said how far the erasure had got and the identity a retry needed,
    /// stopped the only thing that would ever have finished it, and left
    /// <see cref="RequestDeletionAsync"/> answering <c>AlreadyScheduled</c> for ever on the anonymised
    /// row. Nothing in the product could finish or undo that account afterwards.</para>
    ///
    /// <para>So a failure is cancellable only while it has erased nothing, and one that has passed the
    /// point of no return is refused as <see cref="AccountDeletionCancelError.AlreadyExecuting"/> —
    /// the existing error for "this is under way and no longer yours to call off", which the console
    /// already renders. The way out of that state is <see cref="ResumeAsync"/>: an erasure that has
    /// started erasing is finished, not reversed.</para>
    ///
    /// <para><b>Where that point is, is a question about the account and not about the run.</b>
    /// <see cref="ErasureHasBegunAsync"/> owns the answer and its remarks give it in full: the first
    /// two steps end sessions and destroy an export archive, neither of which alters a row, so a
    /// deletion that got no further than those is still entirely the person's to call off. What they
    /// have lost by then is their live sessions — the revocation floor is a watermark on issuance
    /// time and cannot be lifted without re-honouring every other revocation the account has earned —
    /// so a cancelled deletion that already reached step 1 asks them to sign in again, and that is
    /// all it asks.</para>
    /// </remarks>
    public async ValueTask<AccountDeletionCancelResult> CancelDeletionAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("CancelDeletion");
        activity?.SetTag("user.id", UserId);

        // Not a case in the switch below because the answer needs a database read, and a `when` clause
        // cannot await. Read only for Failed: every other status is decided from state alone, and a
        // cancellation on the healthy path must not pay for a query.
        if (state.State.Status is AccountDeletionStatus.Failed && await ErasureHasBegunAsync())
        {
            var erasing = state.State.StepsDone
               .Where(step => !Step.ReversiblePreparation.Contains(step))
               .OrderBy(step => step)
               .ToArray();

            logger.LogWarning(
                "Refused to cancel a half-executed deletion for user {UserId}: {Reason}", UserId,
                erasing.Length > 0
                    ? $"erasing step(s) {string.Join(", ", erasing)} have already run"
                    : "the cursor records nothing irreversible, but the row is already anonymised");

            return new AccountDeletionCancelResult
            {
                Success = false,
                Error = AccountDeletionCancelError.AlreadyExecuting
            };
        }

        switch (state.State.Status)
        {
            case AccountDeletionStatus.None:
                return new AccountDeletionCancelResult { Success = false, Error = AccountDeletionCancelError.NotScheduled };
            case AccountDeletionStatus.Executing:
                return new AccountDeletionCancelResult { Success = false, Error = AccountDeletionCancelError.AlreadyExecuting };
            case AccountDeletionStatus.Completed:
                return new AccountDeletionCancelResult { Success = false, Error = AccountDeletionCancelError.AlreadyCompleted };
        }

        var (email, displayName) = await CallOffAsync(cause: "console");

        // Send cancellation email
        if (!string.IsNullOrEmpty(email))
        {
            var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
            await emailManager.SendDeletionCancelledAsync(email, displayName ?? "User");
        }

        logger.LogInformation("Account deletion cancelled for user {UserId}", UserId);

        return new AccountDeletionCancelResult { Success = true };
    }

    /// <inheritdoc cref="IAccountDeletionGrain.NoticeSignInAsync"/>
    public async ValueTask<bool> NoticeSignInAsync(SignInEvidence evidence)
    {
        // The common case, and the one that has to stay cheap: every sign-in and every app start of
        // every account lands here, and almost none of them has a countdown running.
        if (state.State.Status is not (AccountDeletionStatus.Scheduled or AccountDeletionStatus.Failed))
            return false;

        if (state.State.Trigger is not AccountDeletionTrigger.AutoInactivity)
        {
            logger.LogInformation(
                "User {UserId} signed in with a self-requested deletion counting down; leaving it in place — " +
                "only the console's cancel withdraws a request the person made themselves", UserId);
            return false;
        }

        // The same line CancelDeletionAsync draws: a run that has already erased something is past
        // calling off, whoever asks. Status alone cannot say (see ErasureHasBegunAsync), so this is
        // the one database read a sign-in ever pays, and only for a Failed countdown.
        if (state.State.Status is AccountDeletionStatus.Failed && await ErasureHasBegunAsync())
        {
            logger.LogWarning(
                "User {UserId} signed in while their inactivity deletion is half-executed; it cannot be called off", UserId);
            return false;
        }

        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("CancelDeletionOnSignIn");
        activity?.SetTag("user.id", UserId);

        var (email, displayName) = await CallOffAsync(cause: "sign_in");

        // The confirmation names the device and the address on purpose: a person who did not sign
        // in is reading about somebody who has their password.
        if (!string.IsNullOrEmpty(email))
        {
            var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
            await emailManager.SendDeletionCancelledBySignInAsync(
                email, displayName ?? "User", evidence.Ip, evidence.Location, evidence.Client, evidence.At);
        }

        logger.LogInformation(
            "Inactivity deletion of user {UserId} called off: the account signed in from {Ip} ({Location}) with {Client}",
            UserId, evidence.Ip, evidence.Location, evidence.Client);

        return true;
    }

    /// <summary>
    /// The cancellation itself, once the caller has decided it is allowed: the countdown is cleared,
    /// the refusal recorded and the check disarmed. Says who to tell, so the caller can pick the mail.
    /// </summary>
    /// <param name="cause">
    /// Which door it came through — <c>console</c> or <c>sign_in</c> — for the metric only. The state
    /// records the trigger of the countdown, not of its cancellation.
    /// </param>
    private async Task<(string? Email, string? DisplayName)> CallOffAsync(string cause)
    {
        var email = state.State.OriginalEmail;
        var displayName = state.State.OriginalDisplayName;

        state.State.Status = AccountDeletionStatus.None;
        state.State.ScheduledAt = null;
        state.State.ExecutionAt = null;
        state.State.RemindersSent = [];
        state.State.StepsDone = [];

        // Only ever non-empty for a run that reached step 6, which this cancellation cannot have been
        // (that is past the point of no return and refused by every caller). Cleared for the same
        // reason the cursor is: whatever the abandoned run owed, it is not owed by an account that is
        // staying.
        state.State.PendingDepartureAnnouncements = [];
        state.State.ExecutionAttempts = 0;
        state.State.OriginalEmail = null;
        state.State.OriginalUsername = null;
        state.State.OriginalDisplayName = null;

        // The record of the refusal, and the one thing the cancellation used to leave nowhere (CON-4).
        // Written for every cancellation, not only for a cancelled auto-deletion: a person who calls
        // off a deletion they asked for themselves is an even stronger sign of life than one who
        // answers the inactivity notice.
        state.State.DeclinedAt = DateTimeOffset.UtcNow;

        await state.WriteStateAsync();

        await DisarmCheckAsync();
        await TrackAsync();

        AccountDeletionInstrument.DeletionsCancelled.Add(1, new KeyValuePair<string, object?>("cause", cause));

        return (email, displayName);
    }

    /// <summary>
    /// Whether this erasure has already done something that cannot be taken back.
    /// </summary>
    /// <remarks>
    /// <para><b>The line is irreversibility, not "a step ran".</b> This used to answer true for
    /// <c>StepsDone.Count > 0</c>, and steps 1 and 2 erase nothing about the account.
    /// <see cref="Step.Sessions"/> writes a revocation floor and per-session tombstones, all of which
    /// are credential state and none of which touch a row; <see cref="Step.Exports"/> destroys an
    /// export archive, which is a derived copy the person can ask for again. The first write nobody
    /// can undo is <see cref="Step.Anonymize"/> — the username, the address, the phone number and the
    /// date of birth are overwritten in place, and there is nowhere left to read the originals from
    /// once <see cref="ExecuteDeletionAsync"/> clears the <c>Original*</c> fields. So the cursor is
    /// tested for that step or a later one, and steps 1 and 2 are what they actually are: preparation
    /// a cancellation may simply discard.</para>
    ///
    /// <para>Counting them cost a person their account. An export bucket answering 503 leaves
    /// <c>StepsDone = {1}</c> and <c>Failed</c> on a row that is completely intact — they sign in
    /// normally — and the console then refused to call the deletion off as
    /// <see cref="AccountDeletionCancelError.AlreadyExecuting"/>. Once the attempt bound was spent
    /// nothing polled the grain again either, so <c>Failed</c> was permanent, and the only way out was
    /// the accidental one: re-request the deletion (which resets the cursor) and cancel that.</para>
    ///
    /// <para><b>What a cancellation after step 1 does not put back, and why that is not a reason to
    /// refuse it.</b> The sessions are gone: the floor is a watermark on issuance time, so every
    /// credential minted before it stays dead and the person has to sign in again. That is a
    /// sign-out, which is the weaker event <c>SecurityGrain.ChangePasswordAsync</c> performs on
    /// purpose, and the floor is not a bar on the account — a fresh login mints credentials above the
    /// watermark and works. Lifting it instead would be worse than the inconvenience: the same floor
    /// suppresses every other revocation the account has ever earned, so a cancelled deletion would
    /// silently re-honour tokens a password change or a stolen-device sign-out had ended.</para>
    ///
    /// <para>The cursor is the primary answer, and the row is the check that does not trust it: a
    /// record written by a build older than <see cref="AccountDeletionGrainState.StepsDone"/> carries
    /// no cursor at all, and a step whose database write committed before its state write did not
    /// record itself either. <c>IgnoreQueryFilters</c> because the row being looked for is precisely
    /// the soft-deleted one.</para>
    /// </remarks>
    private async Task<bool> ErasureHasBegunAsync()
    {
        if (state.State.StepsDone.Any(step => !Step.ReversiblePreparation.Contains(step)))
            return true;

        var userId = UserId;

        await using var ctx = await dbFactory.CreateDbContextAsync();

        return await ctx.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId && u.IsDeleted);
    }

    /// <inheritdoc cref="IAccountDeletionGrain.ExpireGraceAsync"/>
    public async ValueTask<AccountDeletionRequestResult> ExpireGraceAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("ExpireDeletionGrace");
        activity?.SetTag("user.id", UserId);

        if (state.State.Status is not AccountDeletionStatus.Scheduled)
            return new AccountDeletionRequestResult
            {
                Success             = false,
                Error               = AccountDeletionRequestError.NotScheduled,
                ScheduledDeletionAt = state.State.ExecutionAt
            };

        var now = DateTimeOffset.UtcNow;

        state.State.ExecutionAt = now;
        MarkRemindersSpent();

        await state.WriteStateAsync();
        await ArmCheckAsync(dueNow: true);
        await TrackAsync();

        logger.LogWarning(
            "An operator brought the deletion of user {UserId} forward from {Was} to now",
            UserId, state.State.ScheduledAt);

        return new AccountDeletionRequestResult { Success = true, ScheduledDeletionAt = now };
    }

    /// <inheritdoc cref="IAccountDeletionGrain.EraseNowAsync"/>
    public async ValueTask<AccountDeletionRequestResult> EraseNowAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("EraseAccountNow");
        activity?.SetTag("user.id", UserId);

        switch (state.State.Status)
        {
            case AccountDeletionStatus.Completed:
                return new AccountDeletionRequestResult
                {
                    Success = false,
                    Error   = AccountDeletionRequestError.AlreadyScheduled
                };

            // Already running, or stopped part-way. Either way the countdown is behind it and the only
            // useful thing left to do is the thing CheckAndExecuteAsync would do — which this does
            // below, after silencing what has not been sent yet.
            case AccountDeletionStatus.Executing:
            case AccountDeletionStatus.Failed:
                break;

            case AccountDeletionStatus.Scheduled:
                state.State.ExecutionAt = DateTimeOffset.UtcNow;
                break;

            default:
            {
                // Nothing scheduled, so this call has to arm the countdown itself — which is where the
                // bars are checked, and the only reason this path reads the account at all.
                await using var ctx = await dbFactory.CreateDbContextAsync();
                var user = await ctx.Users.FirstOrDefaultAsync(x => x.Id == UserId);

                if (user is null)
                    return new AccountDeletionRequestResult
                    {
                        Success = false,
                        Error   = AccountDeletionRequestError.InternalError
                    };

                if (await BarredAsync(ctx, user, trigger: "operator_immediate") is { } barred)
                    return new AccountDeletionRequestResult { Success = false, Error = barred };

                BeginCountdown(user, AccountDeletionTrigger.AutoInactivity);
                state.State.ExecutionAt = DateTimeOffset.UtcNow;
                AccountDeletionInstrument.DeletionsScheduled.Add(1);
                break;
            }
        }

        state.State.Silent = true;
        MarkRemindersSpent();
        await state.WriteStateAsync();
        await TrackAsync();

        logger.LogWarning(
            "An operator is erasing the account of user {UserId} immediately, with no notification to it",
            UserId);

        // Synchronously, because the operator pressed a button and wants to know whether it worked;
        // ExecuteDeletionAsync records its own failure and leaves the poll armed to retry.
        await ArmCheckAsync();
        await ExecuteDeletionAsync();

        return new AccountDeletionRequestResult
        {
            Success             = state.State.Status is not AccountDeletionStatus.Failed,
            Error               = state.State.Status is AccountDeletionStatus.Failed
                ? AccountDeletionRequestError.InternalError
                : null,
            ScheduledDeletionAt = state.State.ExecutionAt
        };
    }

    /// <summary>
    /// Marks every warning threshold as already sent.
    /// </summary>
    /// <remarks>
    /// Both operator buttons that stop the waiting need this, and for the same reason: the poll sends a
    /// warning for every threshold whose window has opened, and collapsing the countdown opens all of
    /// them at once. Without it, bringing a deletion forward posts "one day remains" and "seven days
    /// remain" together, moments before the account is gone.
    /// </remarks>
    private void MarkRemindersSpent()
    {
        foreach (var before in Options.EffectiveReminders)
            state.State.RemindersSent.Add(AccountDeletionOptions.ReminderKey(before));
    }

    /// <inheritdoc cref="IAccountDeletionGrain.ResumeAsync"/>
    public async ValueTask<AccountDeletionStatusDto> ResumeAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("ResumeDeletion");
        activity?.SetTag("user.id", UserId);

        // Nothing to resume, and nothing to arm: a poll on a finished or never-started deletion is a
        // reminder that fires for ever with nothing to do.
        if (state.State.Status is AccountDeletionStatus.None or AccountDeletionStatus.Completed)
            return await GetDeletionStatusAsync();

        logger.LogWarning(
            "Resuming account deletion for user {UserId} from {Steps} completed step(s) after " +
            "{Attempts} failed attempt(s); last reason: {Reason}",
            UserId, state.State.StepsDone.Count, state.State.ExecutionAttempts, state.State.FailureReason);

        state.State.ExecutionAttempts = 0;
        await state.WriteStateAsync();

        await ArmCheckAsync(dueNow: true);

        return await GetDeletionStatusAsync();
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
            FailureReason = state.State.FailureReason,
            DeclinedAt = state.State.DeclinedAt,
            ExecutionAttempts = state.State.ExecutionAttempts,
            Stranded = IsStranded,
            Trigger = state.State.Trigger
        };

        return ValueTask.FromResult(dto);
    }

    /// <summary>
    /// One poll: send whatever reminder has come due, execute an elapsed grace, and pick up a run that
    /// was interrupted or that threw.
    /// </summary>
    /// <remarks>
    /// The last two arms are ACC-15's recovery. <c>Executing</c> means an activation was lost between
    /// two steps — the cursor says which — and <c>Failed</c> means a step threw; both resume from the
    /// cursor rather than from the beginning, and <c>Failed</c> is bounded by
    /// <see cref="AccountDeletionOptions.MaxExecutionAttempts"/> so a deletion the grain cannot
    /// complete settles into a state a person can look at instead of retrying for ever.
    /// </remarks>
    public async ValueTask CheckAndExecuteAsync()
    {
        switch (state.State.Status)
        {
            case AccountDeletionStatus.Executing:
                await ExecuteDeletionAsync();
                return;

            case AccountDeletionStatus.Failed when state.State.ExecutionAttempts < Options.MaxExecutionAttempts:
                logger.LogWarning(
                    "Retrying a failed account deletion for user {UserId} (attempt {Attempt} of {Max}), last reason: {Reason}",
                    UserId, state.State.ExecutionAttempts + 1, Options.MaxExecutionAttempts, state.State.FailureReason);
                await ExecuteDeletionAsync();
                return;

            case AccountDeletionStatus.Scheduled:
                break;

            default:
                return;
        }

        var now = DateTimeOffset.UtcNow;
        var executionAt = state.State.ExecutionAt!.Value;
        var remaining = executionAt - now;

        // Check and send reminders. Each threshold is remembered by
        // AccountDeletionOptions.ReminderKey rather than by its day count: a whole number of days
        // maps to itself, so state written by a production host reads back unchanged, while a host
        // whose grace is measured in seconds still gets one distinct key per threshold instead of
        // two thresholds that both floor to zero and collapse into one reminder.
        foreach (var before in Options.EffectiveReminders)
        {
            var reminderKey = AccountDeletionOptions.ReminderKey(before);

            if (remaining <= before && !state.State.RemindersSent.Contains(reminderKey))
            {
                state.State.RemindersSent.Add(reminderKey);
                await state.WriteStateAsync();

                var daysBefore = (int)before.TotalDays;

                AccountDeletionInstrument.DeletionRemindersSent.Add(1,
                    new KeyValuePair<string, object?>("days_before", daysBefore));

                if (!string.IsNullOrEmpty(state.State.OriginalEmail))
                {
                    var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
                    await emailManager.SendDeletionReminderAsync(
                        state.State.OriginalEmail,
                        state.State.OriginalDisplayName ?? "User",
                        daysBefore);
                }

                logger.LogInformation(
                    "Deletion reminder sent for user {UserId}, {Remaining} remaining",
                    UserId, before);
            }
        }

        // Execute if time has arrived
        if (now >= executionAt)
            await ExecuteDeletionAsync();
    }

    /// <summary>
    /// The erasure itself: ten steps, resumable, bounded by failures rather than by entries.
    /// </summary>
    /// <remarks>
    /// <para><b>Only a failure costs an attempt, and progress refunds them all.</b> The counter used
    /// to be raised here, on entry, which made a resumption cost exactly what a crash did: a grace
    /// that elapsed during a rolling deploy could be interrupted twice with no exception anywhere —
    /// two drained silos, two lost activations — and arrive at the third poll with two of its three
    /// attempts already spent. One ordinary transient database error then exhausted the bound, the
    /// poll was unregistered, and an account that was anonymised at step 3 and still holding
    /// everything steps 5 to 9 erase was left with nothing that would ever come back to it. The bound
    /// exists to stop a run that cannot make progress, so it counts the thing that means no progress:
    /// the counter is raised in the <c>catch</c>, and <see cref="RunStepAsync"/> clears it whenever a
    /// step actually records itself. A lost activation resumes for free, a run that keeps advancing
    /// keeps its attempts, and one that fails the same step over and over stops after
    /// <see cref="AccountDeletionOptions.MaxExecutionAttempts"/> — into a state
    /// <see cref="ResumeAsync"/> can be pointed at.</para>
    ///
    /// <para><c>Executing</c> is still written before any work, and that is what makes the resumption
    /// safe: it is the flag that says the cursor may be trusted.</para>
    /// </remarks>
    private async Task ExecuteDeletionAsync()
    {
        using var activity = AccountDeletionInstrument.ActivitySource.StartActivity("ExecuteDeletion");
        activity?.SetTag("user.id", UserId);
        var sw = Stopwatch.StartNew();

        var resuming = state.State.StepsDone.Count > 0;

        logger.LogInformation(
            "{What} account deletion execution for user {UserId}, {Done} step(s) already done",
            resuming ? "Resuming" : "Starting", UserId, state.State.StepsDone.Count);

        state.State.Status = AccountDeletionStatus.Executing;
        await state.WriteStateAsync();
        await TrackAsync();

        try
        {
            // 1. End every live session: revoke the credentials, close the sockets, vacate the voice
            //    seats. First, and before anything touches the row, because the machinery that does it
            //    resolves the user's spaces through the very rows step 3 and step 6 remove.
            await RunStepAsync(Step.Sessions, InvalidateSessionsAsync);

            // 2. Destroy the export archive, which is a complete copy of everything below.
            await RunStepAsync(Step.Exports, PurgeExportArchivesAsync);

            // 3. Anonymize user and profile
            await RunStepAsync(Step.Anonymize, AnonymizeUserAsync);

            // 4. Reserve old username
            await RunStepAsync(Step.Username, ReserveUsernameAsync);

            // 5. Hard-delete private data
            await RunStepAsync(Step.PrivateData, DeletePrivateDataAsync);

            // 6. Leave every space, through the grain that owns the roster
            await RunStepAsync(Step.Spaces, DeleteOwnedSpacesAsync);

            await RunStepAsync(Step.Memberships, RemoveMembershipsAsync);

            // 7. Soft-delete owned bots
            await RunStepAsync(Step.Bots, SoftDeleteBotsAsync);

            // 8. Decrement file references
            await RunStepAsync(Step.FileRefs, DecrementFileRefsAsync);

            // 9. Clean up conversations
            await RunStepAsync(Step.Conversations, CleanupConversationsAsync);

            // 10. Send completion email
            await RunStepAsync(Step.Notified, async () =>
            {
                if (string.IsNullOrEmpty(state.State.OriginalEmail))
                    return;

                // The step is still run and still recorded, so a silent erasure has the same cursor as
                // any other and nothing downstream has to know which kind it was.
                if (state.State.Silent)
                    return;

                var emailManager = grainFactory.GetGrain<IEmailManager>(Guid.Empty);
                await emailManager.SendDeletionCompletedAsync(
                    state.State.OriginalEmail,
                    state.State.OriginalDisplayName ?? "User");
            });

            // 11. Mark completed, and stop being the last place the identity is written down.
            //
            // Defect ACC-09: the three Original* fields exist so steps 4 and 10 have a username to
            // reserve and an address to write to, and the terminal write used to leave them in place —
            // so the grain-storage row under @grains/.../account-deletion-store kept the real e-mail
            // and display name of every deleted account in plaintext, keyed by user id, with no TTL and
            // no data-subject process that knows to look there. Since AnonymizeUserAsync frees the
            // address on the Users row, that key was the last surviving link from the id to the person,
            // which is precisely the association the erasure exists to sever. Nothing reads them past
            // this point; the Failed branch deliberately keeps them, because a retry still needs them.
            state.State.Status              = AccountDeletionStatus.Completed;
            state.State.CompletedAt         = DateTimeOffset.UtcNow;
            state.State.OriginalEmail       = null;
            state.State.OriginalUsername    = null;
            state.State.OriginalDisplayName = null;
            await state.WriteStateAsync();

            await DisarmCheckAsync();

            sw.Stop();
            await TrackAsync();

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

            // Here and nowhere else: an attempt is spent by failing, not by being started. See the
            // remarks above for what counting entries cost.
            state.State.ExecutionAttempts++;
            await state.WriteStateAsync();

            await TrackAsync();

            AccountDeletionInstrument.DeletionsFailed.Add(1);

            var exhausted = state.State.ExecutionAttempts >= Options.MaxExecutionAttempts;

            if (exhausted)
            {
                await DisarmCheckAsync();
                await ReportStrandedAsync();
            }

            logger.LogError(ex,
                "Account deletion failed for user {UserId} on attempt {Attempt} of {Max}{Ending}",
                UserId, state.State.ExecutionAttempts, Options.MaxExecutionAttempts,
                exhausted ? "; no further attempts will be made" : ", it will be retried");
        }
    }

    /// <summary>
    /// Tells the operator queue that this erasure has given up, so that somebody can find it.
    /// </summary>
    /// <remarks>
    /// <para>Finding R5's residual half. Disarming the poll is right — an erasure the grain cannot
    /// finish must stop hammering the tables — but it leaves an account that is anonymised, still
    /// holding everything the remaining steps erase, and reachable by nobody: its owner has no
    /// password digest and no address to sign in with, and the inactivity scan will never propose a
    /// soft-deleted row. <see cref="AccountDeletionStatusDto.Stranded"/> says so to anyone holding the
    /// user id, and until this call nothing produced that id.
    /// <c>IAccountDeletionQueueGrain.ListStrandedAsync</c> could only list erasures the queue had
    /// itself approved, which on a deployment where the sweep is off is none of them.</para>
    ///
    /// <para><b>Best-effort, and it has to be.</b> This runs inside the <c>catch</c> that has already
    /// written <c>Failed</c> and disarmed the poll: an exception escaping here would leave the
    /// reminder callback faulted for a state that is already correct and already durable. The queue is
    /// a notification surface, not the record — <see cref="GetDeletionStatusAsync"/> remains the
    /// record — so a queue that cannot be reached costs discoverability, which a later
    /// <c>ReconcileAsync</c> pass recovers for an approved deletion and a warning is all that is left
    /// for a self-requested one.</para>
    ///
    /// <para>Awaited rather than detached, so the report is made before the turn ends and cannot be
    /// lost to a deactivation. The one thing that can make it slow is the cycle
    /// <c>AccountDeletionQueueGrain.ApproveAsync</c> closes — it holds the queue's turn while it calls
    /// <see cref="RequestAutoDeleteAsync"/> on a deletion grain, so an approval landing on <em>this</em>
    /// account at the instant its execution exhausts itself has each grain waiting on the other until
    /// the request timeout. That needs a lost approval write (defect R19) to have left a Pending entry
    /// in front of an already-running deletion, it resolves itself, and the catch below is what keeps
    /// it out of the erasure. Every other queue call into this grain goes through
    /// <see cref="GetDeletionStatusAsync"/>, which is <c>[AlwaysInterleave]</c> and cannot deadlock.</para>
    /// </remarks>
    private async Task ReportStrandedAsync()
    {
        try
        {
            await grainFactory
               .GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId)
               .MarkStrandedAsync(UserId, state.State.FailureReason, state.State.ExecutionAttempts);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "The erasure of user {UserId} has given up and the operator queue could not be told; "
              + "nothing but this line names the account", UserId);
        }
    }

    /// <summary>
    /// Runs one step of the execution unless the cursor says it already happened, and records it.
    /// </summary>
    /// <remarks>
    /// The whole of ACC-15's safety. Most steps are set-based and would survive a second unconditional
    /// pass, but <see cref="DecrementFileRefsAsync"/> would release every owned file again, and the
    /// completion mail would arrive twice — so a resumption is only correct if it can tell which steps
    /// have already run. The record is written after the step, never before: a step interrupted
    /// half-way is one that has not happened, and re-running it is what the idempotence of the
    /// individual steps is for.
    /// </remarks>
    private async Task RunStepAsync(int step, Func<Task> body)
    {
        if (state.State.StepsDone.Contains(step))
        {
            logger.LogInformation("Skipping already-completed deletion step {Step} for user {UserId}", step, UserId);
            return;
        }

        await body();

        // Progress refunds the attempts: the bound is on attempts that achieve nothing, so a long
        // erasure that keeps advancing through interruptions is never starved of them, and one that
        // cannot get past the same step still stops after MaxExecutionAttempts.
        state.State.StepsDone.Add(step);
        state.State.ExecutionAttempts = 0;
        await state.WriteStateAsync();
    }

    /// <summary>
    /// Ends every session the account has, the way signing a device out ends one.
    /// </summary>
    /// <remarks>
    /// <para>Defects ACC-01, ACC-02 and ACC-04, pinned by
    /// <c>AccountDeletionTests.The_deleted_accounts_access_token_is_refused_on_every_authenticated_call</c>,
    /// <c>The_deleted_accounts_socket_is_closed_and_goes_quiet</c> and <c>The_voice_seat_is_vacated</c>.
    /// This step used to delete two Redis keys per live session and nothing else — no floor, no
    /// tombstone, no revocation signal, and no call to the session grain. The consequences were the
    /// three separate findings above and all of them are the same omission: an erased account kept a
    /// fully privileged access token for the rest of its seven-day life, its client stayed in the
    /// SignalR group of every space it had joined and went on receiving other people's presence and
    /// messages, and its voice seat stayed occupied because
    /// <c>UserSessionGrain.FinalizeOfflineAsync</c> — the only thing that calls
    /// <c>IUserGrain.LeaveAllVoiceAsync</c> — never ran.</para>
    ///
    /// <para>So it now does what <c>SecurityGrain.EndSessionAsync</c> does, per session, plus the
    /// sign-out-everywhere floor <c>ChangePasswordAsync</c> writes for the strictly weaker event of a
    /// password change. Order: the session grain first, because the presence fan-out it performs — the
    /// Offline broadcast and the voice seats — has to happen while the account is still whole; then
    /// the floor, then the per-session tombstones and the bus signal that closes the sockets.</para>
    ///
    /// <para><b>The floor is the one write here that is not best-effort.</b> Every other step of this
    /// method can fail and leave the erasure meaningful, but a floor that silently did not land is a
    /// deletion that silently revoked nothing — the account is erased and its credentials still work.
    /// It is allowed to throw, which fails the execution into <c>Failed</c>, which
    /// <see cref="CheckAndExecuteAsync"/> now retries.</para>
    /// </remarks>
    private async Task InvalidateSessionsAsync()
    {
        var sessionIds = new List<string>();

        try
        {
            sessionIds = await presenceService.GetActiveSessionIdsAsync(UserId);
        }
        catch (Exception ex)
        {
            // The floor below reaches every credential by date, including the ones this list would
            // have named, so an unreadable session index costs promptness rather than correctness.
            logger.LogWarning(ex, "Could not list the live sessions of user {UserId}", UserId);
        }

        foreach (var sessionId in sessionIds)
        {
            try
            {
                await grainFactory.GetGrain<IUserSessionGrain>($"{UserId}:{sessionId}").GoOfflineAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not take session {SessionId} of user {UserId} offline", sessionId, UserId);
            }
        }

        // Deliberately outside every try: see the remarks.
        await revocationStore.StringSetAsync(
            SessionRevocation.FloorKey(UserId),
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
            SessionRevocation.Window);

        var revokedKey = SessionRevocation.RevokedKey(UserId);

        foreach (var sessionId in sessionIds)
        {
            // Guid.AllBitsSet is the development placeholder a host hands a caller that presents no
            // session id of its own, and Guid.Empty is "nothing was carried". Neither names a device,
            // so tombstoning either would pool unrelated sessions under one entry — the guard
            // SecurityGrain.EndSessionAsync makes for the same reason.
            if (!Guid.TryParse(sessionId, out var presenceSessionId)
             || presenceSessionId == Guid.Empty
             || presenceSessionId == Guid.AllBitsSet)
                continue;

            var credentialSessionIds = new List<Guid>();

            try
            {
                await revocationStore.SetAddAsync(revokedKey, sessionId);

                // The devices screen and the refresh path do not mean the same thing by "session id"
                // (SessionRevocation.CredentialsKey), so the credential the device is holding is
                // tombstoned alongside the row it appears as.
                foreach (var credentialSessionId in await SessionRevocation.CredentialSessionsAsync(revocationStore, UserId, presenceSessionId))
                {
                    await revocationStore.SetAddAsync(revokedKey, credentialSessionId);

                    if (Guid.TryParse(credentialSessionId, out var parsed))
                        credentialSessionIds.Add(parsed);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not tombstone session {SessionId} of user {UserId}", sessionId, UserId);
            }

            // The sockets are not ours to close — a hub connection can only be aborted on the node
            // holding it — so the ids go out on the bus and every node that maps the hub closes what it
            // has. Never throws by contract.
            await revocations.PublishAsync(UserId, presenceSessionId, credentialSessionIds);
        }

        try
        {
            // EXPIRE, not GETEX: KeyExpireAsync is StringGetSetExpiry underneath and answers WRONGTYPE
            // for a set. Retention only — the tombstones above are already committed.
            await revocationStore.UpdateStringExpirationAsync(revokedKey, SessionRevocation.Window);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not set the retention on the revocation tombstones of user {UserId}", UserId);
        }

        try
        {
            // The fallback, kept from the original implementation: the session grains above clear these
            // themselves, and a session whose grain could not be reached still leaves the screen when
            // its keys go.
            foreach (var sessionId in sessionIds)
            {
                await presenceService.RemoveActivityPresence(UserId, sessionId);
                await presenceService.RemoveSessionAsync(UserId, sessionId);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to clear the presence of some sessions for user {UserId}", UserId);
        }

        logger.LogInformation("Invalidated {Count} sessions for user {UserId}", sessionIds.Count, UserId);
    }

    /// <summary>
    /// Stops any export that is running and destroys every archive the account has produced.
    /// </summary>
    /// <remarks>
    /// <para>Defect ACC-10/X5, pinned by
    /// <c>AccountDeletionTests.A_completed_export_does_not_survive_the_account</c>. A finished archive
    /// is <c>profile.json</c> with the person's e-mail, phone and date of birth, <c>devices.json</c>
    /// with up to a hundred IP addresses, and every message they wrote — the whole of what the eight
    /// steps below are about to erase, in one object, behind a presigned URL that needs no
    /// authentication. Nothing in the product ever deleted one: the expiry transition nulls
    /// <c>ArchiveS3Key</c> and walks away, so the bytes were left to a bucket lifecycle rule that this
    /// repository documents and never provisions. An erasure that leaves a downloadable copy of
    /// everything behind is not an erasure, so it goes here, at the account's own request.</para>
    ///
    /// <para>By prefix rather than by the stored key, and it is the same reason
    /// <c>UserDataExportGrain</c> cleans its intermediates that way: an archive whose key the export
    /// grain has already forgotten is exactly the one nothing else will ever remove.</para>
    ///
    /// <para><b>The cancellation is best-effort and the delete is not</b>, and the asymmetry is the
    /// point. Cancelling is a courtesy to a collection in flight — it stops one from writing a fresh
    /// object behind the delete — and an export grain that cannot be reached must not be able to hold
    /// an erasure open. The delete <em>is</em> the step: when it throws, the step is not recorded, the
    /// attempt fails, and the poll comes back to it. It used to be wrapped in a catch of its own,
    /// while these remarks claimed the retry that the catch was preventing —
    /// <see cref="RunStepAsync"/> records a step whenever the body returns, so an export bucket
    /// answering 503 marked the archives purged, and no resumption ever looked at step 2 again.
    /// <c>profile.json</c> — e-mail, phone number, date of birth — and <c>devices.json</c> with up to
    /// a hundred IP addresses stayed in the bucket for good, behind a presigned URL, with nothing left
    /// anywhere naming them.</para>
    ///
    /// <para><b>The call into the export grain is awaited, and that is no longer a deadlock.</b> It
    /// was one: <c>UserDataExportGrain.RequestExportAsync</c> holds its own turn while it asks this
    /// grain whether a deletion stands, and this step calls into that grain from inside an execution
    /// holding this one's turn across all ten steps — two non-reentrant activations, each waiting on
    /// the other until the request timeout broke one of them, which failed the person's export request
    /// and skipped this purge for that attempt. Orleans offers three ways out and only one of them
    /// keeps the ordering this step depends on. <c>[OneWay]</c> would have to be declared on
    /// <c>IUserDataExportGrain</c>, and a detached <c>Task.Run</c> is the same thing written by hand:
    /// both return before the export has actually stopped, so a collection in flight can write a fresh
    /// object behind the delete below — which is the single reason the cancellation comes first. The
    /// third is to remove the cycle instead of living with it, and that is what
    /// <see cref="IAccountDeletionGrain.GetDeletionStatusAsync"/> being <c>[AlwaysInterleave]</c>
    /// does: the export grain's question is answered while this execution holds the turn, its turn
    /// ends, and this call never waits on a grain that is waiting on us. Awaiting stays correct and
    /// stays ordered.</para>
    ///
    /// <para>What remains is a stall rather than a cycle through this grain, and it is not this
    /// grain's to break: <c>CancelExportAsync</c> awaits <c>IExportPumpGrain.UnregisterExportAsync</c>
    /// while the pump's own tick awaits <c>IsExportInProgressAsync</c> on the grains it registered, so
    /// those two can wait on each other for a request timeout. The cancellation being best-effort is
    /// what keeps that out of the erasure: the timeout is logged and the delete below still runs.</para>
    ///
    /// <para><b>An instance with no export bucket has nothing to purge, and that is not a failure.</b>
    /// <c>StorageOptions.ExportBucketName</c> ships as <c>""</c> and <c>ObjectStorageHealthCheck</c>
    /// filters an empty export bucket out of its probe on purpose, so "no data export on this
    /// deployment" is a supported, healthy shape. Without the
    /// <see cref="IExportS3Service.IsConfigured"/> test below, this step listed a bucket named
    /// <c>""</c>, <c>ListObjectsAsync</c> threw on the non-success page exactly as it is meant to for
    /// a bucket that is failing, and the erasure of every account on such an instance failed here —
    /// three times, and then the poll was unregistered for good. The person could not cancel it
    /// either, and nothing anywhere said the feature was inoperable. Skipping is only correct because
    /// the flag distinguishes "there is no store" from "the store is unwell": past it, an error is
    /// still a fault and still fails the attempt.</para>
    ///
    /// <para>The export cancellation runs either way. It costs one grain call, it is best-effort
    /// already, and it is what stops a grain that thinks it is collecting from writing behind the
    /// erasure — which stays true on an instance whose bucket was configured yesterday and is not
    /// today.</para>
    /// </remarks>
    private async Task PurgeExportArchivesAsync()
    {
        try
        {
            await grainFactory.GetGrain<IUserDataExportGrain>(UserId).CancelExportAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not cancel the running export of user {UserId}", UserId);
        }

        if (!exportStorage.IsConfigured)
        {
            logger.LogInformation(
                "No export bucket is configured, so there are no archives to purge for user {UserId}; "
              + "the erasure continues", UserId);

            return;
        }

        await exportStorage.DeletePrefixAsync($"exports/{UserId}/");

        logger.LogInformation("Purged the export archives of user {UserId}", UserId);
    }

    private async Task AnonymizeUserAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        var user = await ctx.Users
            .IgnoreQueryFilters()
            .FirstAsync(x => x.Id == userId);

        // Decrement avatar file ref if present — but only for an avatar DecrementFileRefsAsync will not
        // reach. Defect ACC-08: that step walks every non-deleted file this user owns, which normally
        // includes the avatar, so releasing it here as well took the reference count of a file with one
        // reference down to -1. Each file is released exactly once.
        //
        // What the targeted release is still for, now that R1 has shut both doors it used to be
        // justified by: a file this account owns that has already been soft-deleted. The step-8 walk
        // filters on `!f.IsDeleted` and never reaches one, while FileStorageGrain.OwnedByCallerAsync
        // reads with IgnoreQueryFilters and accepts it — so the reference survives the erasure and
        // FileGcService, which collects on RefCount <= 0, never collects the object. It can no longer
        // reach a foreign avatar at all: UserGrain.OwnsFileAsync refuses an avatarId the caller does
        // not own, and the release itself is scoped to the owner, so a stranger's file is a no-op
        // here rather than a 1 → 0 decrement.
        if (!string.IsNullOrEmpty(user.AvatarFileId))
        {
            try
            {
                var avatarKey = user.AvatarFileId.Contains('/')
                    ? user.AvatarFileId.Split('/')[^1]
                    : user.AvatarFileId;
                if (Guid.TryParse(avatarKey, out var avatarFileId))
                {
                    var releasedByTheWalk = await ctx.Files
                       .IgnoreQueryFilters()
                       .AnyAsync(f => f.Id == avatarFileId && f.OwnerId == userId && !f.IsDeleted);

                    if (!releasedByTheWalk)
                        await grainFactory.GetGrain<IFileStorageGrain>(userId).DecrementRefAsync(avatarFileId);
                }
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

        // The date of birth goes with the phone number, on this row and on the profile below. It is
        // personal data by the product's own reckoning — UserDataExportGrain.CollectProfileAsync
        // exports it as part of the Article 15 response — and the profile row is neither anonymised
        // anywhere else nor soft-deleted, so leaving it there meant UserGrain.GetMyProfile went on
        // answering a DM peer's LookupProfile with the real birth date of an account the product had
        // told its owner was gone, indefinitely.
        user.DateOfBirth = null;
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
            profile.DateOfBirth = null;
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
                Id = ArgonId.New(),
                UserName = state.State.OriginalUsername,
                NormalizedUserName = state.State.OriginalUsername.ToLowerInvariant(),
                IsBanned = false,
                IsReserved = true
            };

            ctx.Add(reserved);
            await ctx.SaveChangesAsync();

            // Registration caches the answer to "is this reserved", misses included, so the cached
            // "no" for this exact name has to go now rather than in a minute — that minute is when
            // someone would be racing to take the name that was just freed.
            await cache.RemoveAsync(ArgonAuthorizationService.ReservationKey);

            logger.LogInformation("Reserved username '{Username}' for deleted user {UserId}",
                state.State.OriginalUsername, UserId);
        }
        catch (Exception ex)
        {
            // May fail if already reserved — non-critical
            logger.LogWarning(ex, "Failed to reserve username for user {UserId}", UserId);
        }
    }

    /// <summary>
    /// Erases every table that holds personal data about this account.
    /// </summary>
    /// <remarks>
    /// <para>Defects ACC-07 and ACC-05, pinned by
    /// <c>AccountDeletionTests.Every_table_holding_personal_data_is_emptied</c> and
    /// <c>The_social_graph_forgets_the_deleted_account</c>. This list is hand-maintained and had
    /// drifted: four of the survivors did not exist when it was written, and the sharpest pair —
    /// <c>PendingEmailChanges</c> and <c>PendingPhoneChanges</c> — held a plaintext address and phone
    /// number the person was half-way through switching to, with no TTL, no sweeper and a declared FK
    /// cascade that can never fire because the user row is anonymised rather than removed, while the
    /// same execution is careful to null the <c>PhoneNumber</c> column eighty lines earlier.</para>
    ///
    /// <para><c>FriendRequest</c> is the one survivor that was not merely residue: the read paths do
    /// not filter deleted users, so the other party kept an actionable invitation from an account the
    /// product had told its owner was gone, and accepting it wrote a fresh friendship against the
    /// erased id. Deleted in both directions, exactly as <c>Friends</c> and <c>UserBlocklist</c>
    /// already were.</para>
    ///
    /// <para><b>What is deliberately kept, and why</b> — so the next reader can tell a decision from an
    /// oversight. <c>Reports</c>, <c>ReportCases</c> and <c>ContentViolations</c>: a moderation record
    /// is about the people who were harmed as much as the account that harmed them, and it is the one
    /// category with a legal basis to outlive the subject. <c>DeviceBans</c> and <c>DeviceKeys</c>: both
    /// are keyed by machine and neither carries a <c>UserId</c>, so a banned device stays banned after
    /// the account on it is erased and nothing there names the person. <c>CouponRedemption</c>: the row
    /// is what stops a single-use coupon being spent twice, and it survives every other account
    /// lifecycle too. <c>Messages</c>, <c>DirectMessages</c> and <c>Conversations</c>: somebody else's
    /// history, authored by the row that has just been anonymised. <c>UsernameReserved</c>: written by
    /// this very execution, one step earlier, on purpose.</para>
    ///
    /// <para><c>IgnoreQueryFilters</c> wherever the entity derives from <c>ArgonEntity</c>: those carry
    /// the global soft-delete filter, and a filtered delete would leave a hidden row behind and call it
    /// erased. Four of them — <c>AutoDeleteSettings</c>, <c>UltimaSubscriptions</c>, <c>SpaceBoosts</c>
    /// and <c>PaymentTransactions</c> — were missing it while the six around them had it. Nothing in
    /// the tree soft-deletes those tables today, so it was latent rather than live; it stops being
    /// latent the day a subscription cancel or a payment void is written the way that base class
    /// invites, and then a row carrying an amount, a currency and a person's id survives an erasure
    /// that reported success. The inconsistency also reads as deliberate to the next maintainer, which
    /// is how this list drifted in the first place.</para>
    /// </remarks>
    private async Task DeletePrivateDataAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        // Friends (both directions)
        await ctx.Friends
            .Where(f => f.UserId == userId || f.FriendId == userId)
            .ExecuteDeleteAsync();

        // Pending friend requests (both directions)
        await ctx.FriendRequest
            .Where(r => r.RequesterId == userId || r.TargetId == userId)
            .ExecuteDeleteAsync();

        // Blocks (both directions)
        await ctx.UserBlocklist
            .Where(b => b.UserId == userId || b.BlockedId == userId)
            .ExecuteDeleteAsync();

        // Ignores (both directions)
        await ctx.UserIgnorelist
            .Where(i => i.UserId == userId || i.IgnoredId == userId)
            .ExecuteDeleteAsync();

        // Privacy rules
        await ctx.PrivacyRules
            .Where(r => r.UserId == userId)
            .ExecuteDeleteAsync();

        // Mute settings
        await ctx.MuteSettings
            .Where(m => m.UserId == userId)
            .ExecuteDeleteAsync();

        // Auto-delete settings
        await ctx.AutoDeleteSettings
            .IgnoreQueryFilters()
            .Where(a => a.UserId == userId)
            .ExecuteDeleteAsync();

        // Saved gifs
        await ctx.SavedGifs
            .IgnoreQueryFilters()
            .Where(g => g.UserId == userId)
            .ExecuteDeleteAsync();

        // Pending contact changes — a plaintext address and phone number, unverified and unreferenced
        await ctx.PendingEmailChanges
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();

        await ctx.PendingPhoneChanges
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();

        // Device histories, and the observations beside them. The anti-abuse linkage a ban rests on
        // lives in DeviceKeys and DeviceBans, which are keyed by machine and name no user, so erasing
        // the per-user observation cannot shed a device ban.
        await ctx.DeviceHistories
            .Where(d => d.UserId == userId)
            .ExecuteDeleteAsync();

        await ctx.DeviceObservations
            .IgnoreQueryFilters()
            .Where(o => o.UserId == userId)
            .ExecuteDeleteAsync();

        // Passkeys
        await ctx.Passkeys
            .IgnoreQueryFilters()
            .Where(p => p.UserId == userId)
            .ExecuteDeleteAsync();

        // Read positions and notification state
        await ctx.ChannelReadStates
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        await ctx.NotificationCounters
            .Where(c => c.UserId == userId)
            .ExecuteDeleteAsync();

        await ctx.SystemNotifications
            .Where(n => n.UserId == userId)
            .ExecuteDeleteAsync();

        // Inventory, and the unread badges hanging off it
        await ctx.UnreadInventoryItems
            .Where(n => n.OwnerUserId == userId)
            .ExecuteDeleteAsync();

        await ctx.Items
            .IgnoreQueryFilters()
            .Where(i => i.OwnerId == userId)
            .ExecuteDeleteAsync();

        // Developer-team memberships and invitations. Teams the account OWNS are soft-deleted with
        // their bots by the next step; these are the rows that name it inside somebody else's team.
        await ctx.MemberTeamEntities
            .Where(m => m.UserId == userId)
            .ExecuteDeleteAsync();

        await ctx.TeamInvites
            .Where(i => i.FromUserId == userId || i.ToUserId == userId)
            .ExecuteDeleteAsync();

        // Subscriptions
        await ctx.UltimaSubscriptions
            .IgnoreQueryFilters()
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        // Space boosts
        await ctx.SpaceBoosts
            .IgnoreQueryFilters()
            .Where(b => b.UserId == userId)
            .ExecuteDeleteAsync();

        // Payment transactions
        await ctx.PaymentTransactions
            .IgnoreQueryFilters()
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

        // Trust score — derived from reports rather than a record of one, and recomputed from scratch
        // for whoever holds the id next, so nothing is lost by removing the person's copy.
        await ctx.UserTrustScores
            .Where(s => s.UserId == userId)
            .ExecuteDeleteAsync();

        logger.LogInformation("Deleted private data for user {UserId}", userId);
    }

    /// <summary>
    /// Leaves every space the account was in, through the grain that owns each roster.
    /// </summary>
    /// <remarks>
    /// <para>Defect ACC-03, pinned by
    /// <c>AccountDeletionTests.The_spaces_are_told_the_member_left_and_stop_listing_them</c>. This used
    /// to be one <c>ExecuteUpdateAsync</c> on a <c>DbContext</c> of its own: correct when it was
    /// written, and silently wrong from the moment <c>SpaceReadGrain</c> started answering rosters out
    /// of a cache with a two-minute distributed expiry. Every roster mutation inside <c>SpaceGrain</c>
    /// invalidates that cache; this one, the only roster writer living outside <c>SpaceGrain</c>, did
    /// not — so <c>GetSpaceSnapshot</c>, <c>GetMembers</c> and <c>GetMemberPresence</c> went on serving
    /// an account that no longer existed, and a client that bootstrapped a space in that window wrote
    /// the erased member into its own store with no event ever coming to remove them.</para>
    ///
    /// <para><see cref="ISpaceGrain.RemoveMemberAsync"/> does the soft-delete, the invalidation and the
    /// <c>LeavedFromServerUser</c> announcement as one thing, which also finally gives bots the
    /// <c>MemberLeave</c> their own API defines.</para>
    ///
    /// <para><b>There is no bare-update fallback behind the loop any more, and the reason is the
    /// announcement.</b> The step used to soft-delete whatever the loop had not managed to remove and
    /// then return normally, so <see cref="RunStepAsync"/> recorded step 6 as done even when a space
    /// had thrown: the row went, no <c>LeavedFromServerUser</c> followed it, no bot got its
    /// <c>MemberLeave</c>, every client that had bootstrapped that space kept the erased member until
    /// it happened to bootstrap again — and the cursor guaranteed nothing would ever come back to it.
    /// That is ACC-03 again, on the error path, made permanent. The failures are thrown instead, so
    /// the step is not recorded and the poll retries it, and leaving the row alone is what makes the
    /// retry worth anything: <see cref="ISpaceGrain.RemoveMemberAsync"/> is deliberately silent once
    /// the membership is already soft-deleted, so a fallback that erased the row first would turn
    /// every later attempt into a no-op that announces nothing. The rows that are left are the ones a
    /// filtered query still reads, and they are what the next attempt walks.</para>
    ///
    /// <para><b>The half-failure inside the space grain is now recovered rather than logged.</b>
    /// <c>SpaceGrain.RemoveMemberAsync</c> commits the soft-delete, then invalidates, then fires, so a
    /// throw from the cache or the bus leaves a space whose row is gone and whose members were never
    /// told — and the rule that makes the ordinary retry safe is exactly what makes that case
    /// unrecoverable: the next attempt selects <c>!IsDeleted</c> memberships and does not see it, and
    /// the method is silent for a row already removed. It recorded step 6 and moved on, and ACC-03
    /// stood for that space for ever (finding R22).</para>
    ///
    /// <para>So the loop asks the database which of the two failures it just had. A membership that
    /// is now soft-deleted is an announcement owed, and the id goes into
    /// <see cref="AccountDeletionGrainState.PendingDepartureAnnouncements"/> — written down before the
    /// throw, because the throw is what makes this attempt fail and grain state is the only thing that
    /// crosses to the next one. A membership still standing is the ordinary case and needs no note:
    /// the row is what the next attempt walks. Either way the step is not recorded, so the poll comes
    /// back.</para>
    ///
    /// <para>The debt is paid first, before the walk, and that ordering is the point rather than a
    /// preference: the spaces in the set are precisely the ones the walk can no longer see, so a walk
    /// that found nothing to do would otherwise return cleanly and let <see cref="RunStepAsync"/>
    /// record the step over an unpaid announcement.</para>
    /// </remarks>
    /// <summary>
    /// Deletes the spaces this account owned that are not communities.
    /// </summary>
    /// <remarks>
    /// <para>The other half of the rule <c>BarredAsync</c> applies: a community bars the deletion and
    /// so is never reached here, and everything else goes. Leaving a private space behind would leave
    /// a room with an owner who no longer exists — nobody can invite, rename, or delete it, and it sits
    /// in every remaining member's list for ever.</para>
    ///
    /// <para>Through <c>ISpaceDeletionGrain</c> rather than <c>ISpaceGrain.DeleteSpace</c> directly, so
    /// a space that already had a deletion scheduled ends in a coherent state rather than being
    /// deleted behind that grain's back. Failures are collected and thrown as one, so the step retries
    /// as a whole and the spaces that did go are not attempted again — <c>DeleteNowAsync</c> is
    /// idempotent on a space that is already gone.</para>
    /// </remarks>
    private async Task DeleteOwnedSpacesAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        var spaceIds = await ctx.Spaces
           .Where(s => s.CreatorId == userId && !s.IsDeleted && !s.IsCommunity)
           .Select(s => s.Id)
           .ToListAsync();

        if (spaceIds.Count == 0)
            return;

        var failed = new List<Guid>();

        foreach (var spaceId in spaceIds)
        {
            try
            {
                await grainFactory.GetGrain<ISpaceDeletionGrain>(spaceId).DeleteNowAsync(userId);
                logger.LogInformation(
                    "Deleted space {SpaceId} with the account of its owner {UserId}", spaceId, userId);
            }
            catch (Exception ex)
            {
                failed.Add(spaceId);
                logger.LogError(ex,
                    "Could not delete space {SpaceId} owned by {UserId}", spaceId, userId);
            }
        }

        if (failed.Count > 0)
            throw new InvalidOperationException(
                $"Could not delete {failed.Count} space(s) owned by this account: {string.Join(", ", failed)}");
    }

    private async Task RemoveMembershipsAsync()
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();
        var userId = UserId;

        var failed = new List<Guid>();

        // First, the announcements a previous attempt committed the row for and could not deliver.
        // Snapshotted because the set is mutated as each one lands.
        foreach (var spaceId in state.State.PendingDepartureAnnouncements.ToArray())
        {
            try
            {
                await grainFactory.GetGrain<ISpaceGrain>(spaceId).AnnounceMemberLeftAsync(userId);

                state.State.PendingDepartureAnnouncements.Remove(spaceId);
            }
            catch (Exception ex)
            {
                failed.Add(spaceId);
                logger.LogWarning(ex,
                    "Could not re-announce that user {UserId} left space {SpaceId}", userId, spaceId);
            }
        }

        var spaceIds = await ctx.UsersToServerRelations
            .Where(m => m.UserId == userId && !m.IsDeleted)
            .Select(m => m.SpaceId)
            .Distinct()
            .ToListAsync();

        foreach (var spaceId in spaceIds)
        {
            try
            {
                await grainFactory.GetGrain<ISpaceGrain>(spaceId).RemoveMemberAsync(userId);
            }
            catch (Exception ex)
            {
                failed.Add(spaceId);

                // Which of the two failures was it? A row that is now soft-deleted means the space
                // grain got past its ExecuteUpdateAsync and threw on the invalidation or the event, so
                // this attempt owes an announcement no later walk can rediscover. Read fresh — the
                // update was made by the space grain's own context, not this one — and read with
                // IgnoreQueryFilters, because a soft-deleted membership is exactly what is being
                // looked for. A read that itself throws leaves the space unrecorded, which degrades to
                // the behaviour before this existed rather than to a lost erasure.
                try
                {
                    var committed = await ctx.UsersToServerRelations
                       .IgnoreQueryFilters()
                       .AnyAsync(m => m.SpaceId == spaceId && m.UserId == userId && m.IsDeleted);

                    if (committed)
                        state.State.PendingDepartureAnnouncements.Add(spaceId);

                    logger.LogWarning(ex,
                        "Could not remove user {UserId} from space {SpaceId}; the membership row {RowState}",
                        userId, spaceId,
                        committed
                            ? "was already removed, so the departure announcement is owed and recorded"
                            : "still stands, so the next attempt walks it again");
                }
                catch (Exception readEx)
                {
                    logger.LogWarning(readEx,
                        "Could not remove user {UserId} from space {SpaceId}, and could not tell whether "
                      + "the membership row survived it", userId, spaceId);
                }
            }
        }

        logger.LogInformation(
            "Removed user {UserId} from {Removed} of {Total} space(s)",
            userId, spaceIds.Count - failed.Count, spaceIds.Count);

        if (failed.Count == 0)
            return;

        // The note has to outlive this attempt, and the throw below is what ends it.
        await state.WriteStateAsync();

        throw new InvalidOperationException(
            $"{failed.Count} space(s) were not told that user {userId} left " +
            $"({string.Join(", ", failed)}); memberships that still stand are left in place so the next " +
            $"attempt walks them, and {state.State.PendingDepartureAnnouncements.Count} announcement(s) " +
            "over an already-removed row are recorded for it to replay");
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
