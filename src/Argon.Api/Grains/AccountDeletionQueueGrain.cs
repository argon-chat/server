namespace Argon.Grains;

using Argon.Features.Logic;
using Argon.Grains.Interfaces;
using Microsoft.Extensions.Options;
using Orleans.Providers;
using Persistence.States;

/// <summary>
/// The queue between the inactivity scan and an erasure — one activation for the whole cluster.
/// </summary>
/// <remarks>
/// <para><b>What this grain is for.</b> Automatic deletion is switched off in production because nobody
/// trusts a timer with an irreversible operation on somebody's account, and the campaign gave three
/// reasons why that mistrust was earned (CON-2, CON-3, CON-4). Rather than making the timer a little safer
/// again, the decision was to take the judgement away from it: the scan proposes, this grain holds the
/// proposal, and an operator approves it through the admin console. Nothing here deletes anything —
/// <see cref="ApproveAsync"/> calls the same guarded <see cref="IAccountDeletionGrain.RequestAutoDeleteAsync"/>
/// the sweeper used to call, so every bar that refuses a person asking to delete their own account refuses
/// an approval too.</para>
///
/// <para><b>Reconciliation rather than accumulation.</b> <see cref="ReconcileAsync"/> replaces the pending
/// half of the queue with the candidate set the scan just produced. That is the whole implementation of
/// "an entry disappears when the person signs in again": a fresh login moves the account out of the
/// candidate set, and the next pass simply does not carry it forward. It also keeps the state bounded and
/// self-healing — a pending entry can never describe an account the current rules would not propose,
/// because the current rules are what wrote it. Approved entries are held back from that rule until the
/// deletion they authorised is over, since approving is itself what takes an account out of the candidate
/// set and the record would otherwise disappear a day after the decision.</para>
///
/// <para><b>And a decision that has played out is kept for a while longer.</b> "The worklist is for work,
/// so retire a finished decision" is right about the worklist and wrong about the audit view sitting on
/// the same rows. An erasure that runs anonymises its account — that is step three — so the entry is the
/// last thing on the console that can say this account existed, who approved its deletion and when; and
/// the reconciliation that retired it fired somewhere between a second and a day after the erasure
/// finished, which is exactly when an operator comes back to see how their own approval went. So a
/// finished entry becomes <see cref="QueuedAccountDeletionState.Completed"/> and is kept for
/// <see cref="AccountDeletionOptions.DecisionRetention"/> measured from the completion, then retired.
/// <see cref="QueuedAccountDeletionState.Pending"/> is the only state that is a projection of the scan
/// and the only one that may go the moment its account stops being proposed.</para>
///
/// <para><b>The one thing it remembers on its own is a refusal.</b> If a rejection only removed the entry,
/// the next scan would re-propose the same account tomorrow, which is defect CON-4 wearing an operator's
/// clothes. <see cref="AccountDeletionQueueGrainState.RejectedUntil"/> outlives the entry for
/// <see cref="AccountDeletionOptions.DeclineHoldsFor"/> — a full inactivity period, the same value a
/// person's own refusal earns them — and <see cref="ReconcileAsync"/> refuses to re-enqueue while it
/// stands.</para>
/// </remarks>
public class AccountDeletionQueueGrain(
    [PersistentState("account-deletion-queue-store", ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME)]
    IPersistentState<AccountDeletionQueueGrainState> state,
    IGrainFactory grainFactory,
    IOptions<AccountDeletionOptions> options,
    ILogger<AccountDeletionQueueGrain> logger) : Grain, IAccountDeletionQueueGrain
{
    /// <summary>
    /// How many accounts the queue will hold at once — <see cref="IAccountDeletionQueueGrain.MaxEntries"/>,
    /// which the scan reads as well so it can stop collecting at the same number.
    /// </summary>
    private const int MaxEntries = IAccountDeletionQueueGrain.MaxEntries;

    /// <summary>The largest page <see cref="ListAsync"/> will answer with, whatever a caller asks for.</summary>
    private const int MaxPageSize = 200;

    private AccountDeletionOptions Options => options.Value;

    public async ValueTask<AccountDeletionQueueScan> ReconcileAsync(List<AccountDeletionCandidate> candidates)
    {
        var now = DateTimeOffset.UtcNow;

        // Holds are purged here rather than on a timer of their own: the scan is the only thing that reads
        // them, so an expired hold has no effect until the next pass anyway.
        foreach (var expired in state.State.RejectedUntil.Where(hold => hold.Value <= now).Select(hold => hold.Key).ToArray())
            state.State.RejectedUntil.Remove(expired);

        var held     = 0;
        var proposed = new Dictionary<Guid, AccountDeletionCandidate>();

        foreach (var candidate in candidates)
        {
            if (state.State.RejectedUntil.TryGetValue(candidate.UserId, out var until) && until > now)
            {
                held++;
                continue;
            }

            proposed[candidate.UserId] = candidate;
        }

        var kept    = new List<AccountDeletionQueueRecord>(state.State.Entries.Count);
        var retired = 0;

        foreach (var entry in state.State.Entries)
        {
            // A pending entry is pure projection: it lives exactly as long as the scan keeps proposing
            // its account, which is the whole of "the entry goes away when the person signs in again".
            if (entry.State is QueuedAccountDeletionState.Pending)
            {
                if (proposed.ContainsKey(entry.UserId))
                    kept.Add(entry);
                else
                    retired++;

                continue;
            }

            // A completed entry has stopped asking anything of anybody: the erasure it authorised ran, the
            // account behind it is a tombstone, and the only question left is how long the record of the
            // decision stays on the page. It is not re-read from the deletion grain — there is nothing
            // there that can change, and a call per retained row per pass would be paid for a week — so
            // the retention window is the whole of its lifetime.
            if (entry.State is QueuedAccountDeletionState.Completed)
            {
                if (RetentionElapsed(entry, now))
                    retired++;
                else
                    kept.Add(entry);

                continue;
            }

            // A decided entry is not a projection and the proposal set cannot speak for it — in either
            // direction. An approved account stops being a candidate the moment it is approved (the scan
            // skips anything already scheduled), so retiring on "not proposed" would make every approval
            // vanish from the worklist within a day, inside the grace period it authorised. And being
            // proposed again does not keep one alive either: defect R23, where an approval whose deletion
            // the account holder had long since cancelled was carried for ever because the account came
            // back into the candidate set a year later — visible as "approved", refused by Approve as
            // already decided and by Reject as already scheduled, unfixable without editing grain state.
            // What decides is the deletion itself, so that is what is asked.
            if (await ReclassifyAsync(entry, now))
                kept.Add(entry);
            else
                retired++;
        }

        var known    = kept.Where(entry => proposed.ContainsKey(entry.UserId)).ToDictionary(entry => entry.UserId);
        var enqueued = 0;

        foreach (var candidate in proposed.Values)
        {
            if (known.TryGetValue(candidate.UserId, out var existing))
            {
                // The scan's arithmetic is refreshed; the decision and the age of the entry are not.
                existing.LastActivityAt  = candidate.LastActivityAt;
                existing.ThresholdMonths = candidate.ThresholdMonths;
                existing.Reason          = candidate.Reason;
                continue;
            }

            kept.Add(new AccountDeletionQueueRecord
            {
                UserId          = candidate.UserId,
                LastActivityAt  = candidate.LastActivityAt,
                ThresholdMonths = candidate.ThresholdMonths,
                Reason          = candidate.Reason,
                EnqueuedAt      = now,
                State           = QueuedAccountDeletionState.Pending
            });

            enqueued++;
        }

        // Decided entries survive the cap unconditionally — there are only ever as many as an operator
        // approved within a grace period plus a retention window, and dropping one would lose the record
        // of who said yes while the deletion it authorised was still running, the record of one that has
        // just run (see QueuedAccountDeletionState.Completed), or the only trace of one that stopped half
        // way (see QueuedAccountDeletionState.Stranded).
        state.State.Entries =
        [
            .. kept.Where(entry => entry.State is not QueuedAccountDeletionState.Pending),
            .. kept.Where(entry => entry.State is QueuedAccountDeletionState.Pending)
                   .OrderBy(entry => entry.LastActivityAt)
                   .Take(MaxEntries)
        ];

        await state.WriteStateAsync();

        logger.LogInformation(
            "Account deletion queue reconciled: {Enqueued} enqueued, {Retired} retired, {Held} held, {Length} waiting",
            enqueued, retired, held, state.State.Entries.Count);

        return new AccountDeletionQueueScan
        {
            Enqueued = enqueued,
            Retired  = retired,
            Held     = held,
            Length   = state.State.Entries.Count
        };
    }

    public ValueTask<AccountDeletionQueueSnapshot> ListAsync(int offset, int limit)
    {
        var page = Math.Clamp(limit, 1, MaxPageSize);
        var skip = Math.Max(offset, 0);

        // Work owed first, and longest-idle first inside each band: the queue is a worklist, and the
        // account that has been silent longest is the one an operator should be looking at. Stranded
        // entries sort with the pending ones because they are work owed too — an erasure that stopped
        // half way needs a person — approved ones after them, being work in progress, and completed ones
        // last of all, being a record of work that is over and kept only for its retention window.
        var ordered = state.State.Entries
           .OrderBy(WorklistRank)
           .ThenBy(entry => entry.LastActivityAt)
           .ThenBy(entry => entry.UserId);

        var snapshot = new AccountDeletionQueueSnapshot
        {
            Entries    = [.. ordered.Skip(skip).Take(page).Select(Describe)],
            TotalCount = state.State.Entries.Count,
            Offset     = skip,
            Limit      = page
        };

        return ValueTask.FromResult(snapshot);
    }

    /// <summary>
    /// The stranded half of the queue: approvals whose erasure stopped with steps left undone.
    /// </summary>
    /// <remarks>
    /// <para>Its own page rather than a filter over <see cref="ListAsync"/> so the count an operator
    /// sees is the count of accounts needing a person, and so paging over it stays consistent while the
    /// worklist beside it fills and drains.</para>
    ///
    /// <para><b>Every stranded erasure, not only the approved ones.</b> This used to list what the
    /// reconciliation had marked, which could only ever be a deletion this queue itself authorised —
    /// so a person's own deletion request that stranded was invisible to everybody, the account being
    /// unable to sign in and ask (defect R5). <see cref="MarkStrandedAsync"/> is the other half:
    /// <c>AccountDeletionGrain</c> reports in when it disarms itself, an entry is created if there is
    /// none, and this page is complete whatever the deletion's origin.</para>
    /// </remarks>
    public ValueTask<AccountDeletionQueueSnapshot> ListStrandedAsync(int offset, int limit)
    {
        var page = Math.Clamp(limit, 1, MaxPageSize);
        var skip = Math.Max(offset, 0);

        var stranded = state.State.Entries
           .Where(entry => entry.State is QueuedAccountDeletionState.Stranded)
           .OrderBy(entry => entry.StrandedSince ?? entry.DecidedAt ?? entry.EnqueuedAt)
           .ThenBy(entry => entry.UserId)
           .ToList();

        var snapshot = new AccountDeletionQueueSnapshot
        {
            Entries    = [.. stranded.Skip(skip).Take(page).Select(Describe)],
            TotalCount = stranded.Count,
            Offset     = skip,
            Limit      = page
        };

        return ValueTask.FromResult(snapshot);
    }

    /// <inheritdoc cref="IAccountDeletionQueueGrain.MarkStrandedAsync"/>
    /// <remarks>
    /// <para>The created entry carries <see cref="AccountDeletionQueueReasons.StrandedErasure"/> and
    /// no inactivity arithmetic, because there is none: nothing measured this account, and inventing a
    /// threshold to fill the fields would put a number in front of an operator that no scan ever
    /// computed. <c>LastActivityAt</c> is the moment the queue learned of the erasure — it is what
    /// <see cref="ListAsync"/> sorts the worklist by, and "just heard about it" is the truthful
    /// position for a row whose account's real activity is unknown; <see cref="ListStrandedAsync"/>,
    /// the page this row exists for, sorts by <c>StrandedSince</c> instead.</para>
    ///
    /// <para><b>It cannot be Pending afterwards, and that matters more than it looks.</b> A pending
    /// entry is a projection of the scan and the next <see cref="ReconcileAsync"/> retires one whose
    /// account is not proposed — an anonymised row never is — so a stranded erasure recorded as
    /// pending would vanish on the next daily pass, which is the defect this exists to close.</para>
    /// </remarks>
    public async ValueTask MarkStrandedAsync(Guid userId, string? reason, int attempts)
    {
        var now = DateTimeOffset.UtcNow;

        if (state.State.Entries.FirstOrDefault(entry => entry.UserId == userId) is { } existing)
        {
            existing.FailureReason     = reason;
            existing.ExecutionAttempts = attempts;

            if (existing.State is not QueuedAccountDeletionState.Stranded)
            {
                existing.State         = QueuedAccountDeletionState.Stranded;
                existing.StrandedSince = now;
            }
        }
        else
        {
            state.State.Entries.Add(new AccountDeletionQueueRecord
            {
                UserId            = userId,
                LastActivityAt    = now,
                ThresholdMonths   = 0,
                Reason            = AccountDeletionQueueReasons.StrandedErasure,
                EnqueuedAt        = now,
                State             = QueuedAccountDeletionState.Stranded,
                StrandedSince     = now,
                FailureReason     = reason,
                ExecutionAttempts = attempts
            });
        }

        await state.WriteStateAsync();

        logger.LogError(
            "The erasure of account {UserId} has stopped with steps left undone after {Attempts} attempt(s) "
          + "({Reason}). It is on the stranded queue until an operator resumes it.",
            userId, attempts, reason);
    }

    /// <inheritdoc cref="IAccountDeletionQueueGrain.GetDeclineHoldsAsync"/>
    public ValueTask<List<Guid>> GetDeclineHoldsAsync()
    {
        var now = DateTimeOffset.UtcNow;

        return ValueTask.FromResult(state.State.RejectedUntil
           .Where(hold => hold.Value > now)
           .Select(hold => hold.Key)
           .ToList());
    }

    /// <inheritdoc cref="IAccountDeletionQueueGrain.RefreshAsync"/>
    /// <inheritdoc cref="IAccountDeletionQueueGrain.TrackDeletionAsync"/>
    public async ValueTask TrackDeletionAsync(
        Guid userId, AccountDeletionStatusKind status, DateTimeOffset? executionAt, bool selfRequested)
    {
        var now = DateTimeOffset.UtcNow;

        // Over when it is over: a finished or called-off deletion leaves the register rather than
        // sitting in it as a row the console has to filter out. Completed accounts are still visible —
        // through the queue's own entries, which keep a decision for DecisionRetention — and that is
        // the view that belongs to an operator's decision rather than to work in progress.
        if (status is AccountDeletionStatusKind.None or AccountDeletionStatusKind.Completed)
        {
            if (state.State.InFlight.Remove(userId))
                await state.WriteStateAsync();
            return;
        }

        if (state.State.InFlight.TryGetValue(userId, out var existing))
        {
            existing.Status      = status;
            existing.ExecutionAt = executionAt;
            existing.UpdatedAt   = now;
        }
        else
        {
            state.State.InFlight[userId] = new InFlightDeletionRecord
            {
                ArmedAt       = now,
                ExecutionAt   = executionAt,
                SelfRequested = selfRequested,
                Status        = status,
                UpdatedAt     = now
            };
        }

        await state.WriteStateAsync();
    }

    /// <inheritdoc cref="IAccountDeletionQueueGrain.ListInFlightAsync"/>
    public ValueTask<InFlightDeletionsSnapshot> ListInFlightAsync(int offset, int limit)
    {
        var page = Math.Clamp(limit, 1, MaxPageSize);
        var from = Math.Max(offset, 0);

        var all = state.State.InFlight
           .Select(pair => new InFlightDeletion
            {
                UserId        = pair.Key,
                Status        = pair.Value.Status,
                ArmedAt       = pair.Value.ArmedAt,
                ExecutionAt   = pair.Value.ExecutionAt,
                SelfRequested = pair.Value.SelfRequested
            })
            // Soonest first: the erasure about to run is the one an operator still has time to stop.
           .OrderBy(entry => entry.ExecutionAt ?? DateTimeOffset.MaxValue)
           .ThenBy(entry => entry.ArmedAt)
           .ToList();

        return ValueTask.FromResult(new InFlightDeletionsSnapshot
        {
            Entries     = all.Skip(from).Take(page).ToList(),
            TotalCount  = all.Count,
            FailedCount = all.Count(entry => entry.Status is AccountDeletionStatusKind.Failed)
        });
    }

    public async ValueTask RefreshAsync(Guid userId)
    {
        if (state.State.Entries.FirstOrDefault(entry => entry.UserId == userId) is not { } entry)
            return;

        if (entry.State is QueuedAccountDeletionState.Pending)
            return;

        if (!await ReclassifyAsync(entry, DateTimeOffset.UtcNow))
            state.State.Entries.Remove(entry);

        await state.WriteStateAsync();
    }

    public async ValueTask<AccountDeletionQueueDecision> ApproveAsync(Guid userId, Guid operatorId, string operatorEmail)
    {
        if (state.State.Entries.FirstOrDefault(entry => entry.UserId == userId) is not { } entry)
            return new AccountDeletionQueueDecision
            {
                Success = false,
                Error   = AccountDeletionQueueDecisionError.NotQueued
            };

        if (entry.State is not QueuedAccountDeletionState.Pending)
            return new AccountDeletionQueueDecision
            {
                Success             = false,
                Error               = AccountDeletionQueueDecisionError.AlreadyDecided,
                ScheduledDeletionAt = entry.ScheduledDeletionAt
            };

        // The intent is written down before the irreversible thing is done, which is the order defect
        // R19 was about. RequestAutoDeleteAsync writes Scheduled into the deletion grain's own state,
        // arms its reminder and mails the account its notice; if the queue's write of "who approved
        // this" then failed or was lost with the activation, the entry stayed Pending in front of an
        // armed deletion. Approving again answered AlreadyScheduled — permanently un-approvable — and
        // Reject, seeing a Pending entry, dropped the row and left the countdown running with nobody's
        // name on it. Written first, a lost write costs an approval that has to be repeated; written
        // last, it costs the record of an erasure that is going to happen anyway. For the same reason a
        // call that *throws* is not rolled back: a timeout cannot say whether the deletion grain wrote
        // Scheduled before or after it lost the caller, and the reconciliation settles the entry against
        // the deletion within one pass either way. Only an explicit refusal — which is the grain saying
        // it did nothing — rolls back.
        var decidedAt = DateTimeOffset.UtcNow;

        entry.State                  = QueuedAccountDeletionState.Approved;
        entry.DecidedByOperatorId    = operatorId;
        entry.DecidedByOperatorEmail = operatorEmail;
        entry.DecidedAt              = decidedAt;
        entry.ScheduledDeletionAt    = null;

        await state.WriteStateAsync();

        // The guarded path, and the reason approving is not "write Scheduled into the deletion grain":
        // lockdown, an active subscription, a space the account still owns and a standing refusal all
        // refuse here, exactly as they refuse the person themselves.
        var scheduled = await grainFactory.GetGrain<IAccountDeletionGrain>(userId).RequestAutoDeleteAsync();

        if (!scheduled.Success)
        {
            // AlreadyScheduled is the one refusal that is not a refusal: the deletion grain is telling us
            // a deletion of this account already stands, which is exactly what a repeat of an approval
            // whose write was lost looks like from here. Adopt it — reporting a failure over an armed
            // erasure is how the account came to be deleted with nobody's name against it. Adopted even
            // when the execution date cannot be read back: the entry's job is to record the decision, and
            // the next reconciliation will fill the date in.
            if (scheduled.Error is AccountDeletionRequestError.AlreadyScheduled)
            {
                var execution = await ScheduleOfLiveDeletionAsync(userId);

                entry.ScheduledDeletionAt = execution;

                await state.WriteStateAsync();

                logger.LogWarning(
                    "Operator {OperatorId} approved the deletion of {UserId}, which was already scheduled for "
                  + "{ExecutionAt}; adopting the existing schedule rather than refusing the approval",
                    operatorId, userId, execution);

                return new AccountDeletionQueueDecision { Success = true, ScheduledDeletionAt = execution };
            }

            // Nothing was armed, so the decision is rolled back to the state it was read in and the
            // entry stays workable. The scan's arithmetic on it is untouched.
            entry.State                  = QueuedAccountDeletionState.Pending;
            entry.DecidedByOperatorId    = null;
            entry.DecidedByOperatorEmail = null;
            entry.DecidedAt              = null;
            entry.ScheduledDeletionAt    = null;

            await state.WriteStateAsync();

            logger.LogWarning(
                "Operator {OperatorId} approved the deletion of {UserId} and the deletion grain refused it: {Error}",
                operatorId, userId, scheduled.Error);

            return new AccountDeletionQueueDecision
            {
                Success      = false,
                Error        = AccountDeletionQueueDecisionError.RefusedByDeletionGrain,
                RequestError = scheduled.Error
            };
        }

        entry.ScheduledDeletionAt = scheduled.ScheduledDeletionAt;

        await state.WriteStateAsync();

        logger.LogInformation(
            "Operator {OperatorId} approved the deletion of inactive account {UserId}, execution at {ExecutionAt}",
            operatorId, userId, scheduled.ScheduledDeletionAt);

        return new AccountDeletionQueueDecision
        {
            Success             = true,
            ScheduledDeletionAt = scheduled.ScheduledDeletionAt
        };
    }

    public async ValueTask<AccountDeletionQueueDecision> RejectAsync(Guid userId, Guid operatorId, string operatorEmail)
    {
        if (state.State.Entries.FirstOrDefault(entry => entry.UserId == userId) is not { } entry)
            return new AccountDeletionQueueDecision
            {
                Success = false,
                Error   = AccountDeletionQueueDecisionError.NotQueued
            };

        // A decided entry has a countdown running against it, or the wreckage of one. Calling that off is
        // a cancellation of a scheduled deletion, which belongs to the account holder's own console, not
        // to a queue entry.
        if (entry.State is not QueuedAccountDeletionState.Pending)
            return new AccountDeletionQueueDecision
            {
                Success             = false,
                Error               = AccountDeletionQueueDecisionError.AlreadyDecided,
                ScheduledDeletionAt = entry.ScheduledDeletionAt
            };

        // And the same question asked of the deletion grain, not only of the entry. The entry's state is
        // this grain's belief about a decision; the deletion is the fact. They disagree exactly when an
        // approval's write was lost (R19) — a Pending entry in front of an armed erasure — and rejecting
        // that used to remove the row and record a decline while the countdown kept running, so nothing
        // anywhere said the account was about to be erased or on whose say-so.
        if (await ScheduleOfLiveDeletionAsync(userId) is { } execution)
        {
            logger.LogWarning(
                "Operator {OperatorId} rejected the queued deletion of {UserId}, whose deletion is already "
              + "running (execution at {ExecutionAt}); refusing — only the account holder can call that off",
                operatorId, userId, execution);

            return new AccountDeletionQueueDecision
            {
                Success             = false,
                Error               = AccountDeletionQueueDecisionError.AlreadyDecided,
                ScheduledDeletionAt = execution
            };
        }

        state.State.Entries.Remove(entry);
        state.State.RejectedUntil[userId] = DateTimeOffset.UtcNow + Options.DeclineHoldsFor;

        await state.WriteStateAsync();

        logger.LogInformation(
            "Operator {OperatorId} ({OperatorEmail}) rejected the deletion of inactive account {UserId}; " +
            "the scan will not propose it again before {Until}",
            operatorId, operatorEmail, userId, state.State.RejectedUntil[userId]);

        return new AccountDeletionQueueDecision { Success = true };
    }

    /// <summary>
    /// Reads what actually became of a decided entry's deletion, and answers whether the entry still has
    /// a job to do.
    /// </summary>
    /// <remarks>
    /// <para>A grain call per decided entry per pass, bounded by how many approvals an operator makes
    /// between two scans rather than by how many accounts exist. An entry whose status cannot be read is
    /// kept unchanged: losing the record of a decision is worse than carrying it one pass too long, and
    /// the next pass will settle it.</para>
    ///
    /// <para>The four outcomes, and why each is what it is:</para>
    /// <list type="bullet">
    /// <item><description><c>Scheduled</c>/<c>Executing</c> — the deletion this entry authorised is on
    /// its way. Kept, and the entry is put back to <c>Approved</c> if a previous pass had marked it
    /// stranded, because a resumed erasure is running again.</description></item>
    /// <item><description><c>Failed</c> with attempts left — a retry is still coming, so it stays
    /// <c>Approved</c>; the reason is carried anyway, since an operator watching the queue would rather
    /// see one failure than learn of it only when the attempts run out.</description></item>
    /// <item><description><c>Failed</c> and stranded — the erasure has stopped by itself and nothing
    /// will poll it again (defect R5). This is the only surface that knows, so the entry is kept and
    /// marked, and <see cref="ListStrandedAsync"/> is where a person finds it.</description></item>
    /// <item><description><c>Completed</c> — the erasure ran. The decision has played out, and this is
    /// the one outcome that is <em>not</em> retired on the spot: the account is anonymised, so this entry
    /// is the last place the console can say whose deletion this was, and the pass that used to drop it
    /// ran within a day of the erasure — under a second of it, when a neighbouring scan happened to land.
    /// It becomes <see cref="QueuedAccountDeletionState.Completed"/>, stamped with the moment the queue
    /// saw it finish, and lives out <see cref="AccountDeletionOptions.DecisionRetention"/> from
    /// there.</description></item>
    /// <item><description><c>None</c>, because the account holder cancelled inside the grace period —
    /// retired at once, as it always was. There is no completed erasure to keep a record of: the account
    /// is alive and back under the scan's rule, so the row would be a badge saying "gone" over somebody
    /// who is not, and leaving it <c>Approved</c> instead is defect R23 exactly. The decision itself is
    /// in the audit log, which is where a question about a deletion that did not happen
    /// belongs.</description></item>
    /// </list>
    /// </remarks>
    private async Task<bool> ReclassifyAsync(AccountDeletionQueueRecord entry, DateTimeOffset now)
    {
        AccountDeletionStatusDto status;

        try
        {
            status = await grainFactory.GetGrain<IAccountDeletionGrain>(entry.UserId).GetDeletionStatusAsync();
        }
        catch (Exception e)
        {
            logger.LogWarning(e,
                "Could not read the deletion status of decided account {UserId}; keeping its entry", entry.UserId);

            return true;
        }

        entry.FailureReason     = status.FailureReason;
        entry.ExecutionAttempts = status.ExecutionAttempts;

        switch (status.Status)
        {
            case AccountDeletionStatusKind.Scheduled:
            case AccountDeletionStatusKind.Executing:
                entry.State               = QueuedAccountDeletionState.Approved;
                entry.StrandedSince       = null;
                entry.ScheduledDeletionAt = status.ExecutionAt ?? entry.ScheduledDeletionAt;
                return true;

            case AccountDeletionStatusKind.Failed when status.Stranded:
                if (entry.State is not QueuedAccountDeletionState.Stranded)
                {
                    entry.State         = QueuedAccountDeletionState.Stranded;
                    entry.StrandedSince = now;

                    logger.LogError(
                        "The erasure of account {UserId}, approved by {OperatorId}, has stopped with steps left "
                      + "undone after {Attempts} attempts ({Reason}). It is on the stranded queue until an "
                      + "operator resumes it.",
                        entry.UserId, entry.DecidedByOperatorId, status.ExecutionAttempts, status.FailureReason);
                }

                return true;

            case AccountDeletionStatusKind.Failed:
                entry.State         = QueuedAccountDeletionState.Approved;
                entry.StrandedSince = null;
                return true;

            case AccountDeletionStatusKind.Completed:
                if (entry.State is not QueuedAccountDeletionState.Completed)
                {
                    entry.State       = QueuedAccountDeletionState.Completed;
                    entry.CompletedAt = now;

                    // An erasure that stranded, was resumed by hand and then finished is not stranded any
                    // more, and the stranded page reads this field: leaving it set would keep offering an
                    // operator work that is over.
                    entry.StrandedSince = null;
                }

                return !RetentionElapsed(entry, now);

            default:
                return false;
        }
    }

    /// <summary>Whether a played-out decision has been kept for as long as it is meant to be.</summary>
    /// <remarks>
    /// <para>Measured from the completion rather than from the decision, because those are a grace period
    /// apart — thirty days at shipped values against a week of retention — so "a week since the operator
    /// said yes" would retire most entries before their erasure had run at all.</para>
    ///
    /// <para>The fallbacks are for a row that has somehow arrived here without a completion stamp: state
    /// written by a deployment older than the stamp cannot be <see cref="QueuedAccountDeletionState.Completed"/>
    /// at all, so this is defence rather than migration, and it fails towards keeping the row — the worst
    /// case is one entry carried a grace period too long, against an audit view silently losing rows.</para>
    /// </remarks>
    private bool RetentionElapsed(AccountDeletionQueueRecord entry, DateTimeOffset now)
        => now - (entry.CompletedAt ?? entry.DecidedAt ?? entry.EnqueuedAt) >= Options.DecisionRetention;

    /// <summary>
    /// When the account's deletion executes, if one is actually live right now — otherwise null.
    /// </summary>
    /// <remarks>
    /// Asked of the deletion grain rather than of the entry, because the entry is a belief and this is
    /// the fact. Unreadable is answered as "no live deletion": both callers use it to <em>refuse</em>
    /// something, and refusing an operator on a store blip is worse than the retry they get instead.
    /// </remarks>
    private async Task<DateTimeOffset?> ScheduleOfLiveDeletionAsync(Guid userId)
    {
        try
        {
            var status = await grainFactory.GetGrain<IAccountDeletionGrain>(userId).GetDeletionStatusAsync();

            return status.Status is AccountDeletionStatusKind.Scheduled or AccountDeletionStatusKind.Executing
                ? status.ExecutionAt ?? status.ScheduledAt
                : null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the deletion status of account {UserId}", userId);

            return null;
        }
    }

    /// <summary>
    /// Where an entry sorts on the worklist: work owed first, work in progress next, work that is over
    /// last.
    /// </summary>
    /// <remarks>
    /// A completed entry sorts below an approved one rather than beside it, which is the ordering half of
    /// why <see cref="QueuedAccountDeletionState.Completed"/> is a state and not a stamp: an erasure that
    /// has run needs nobody, and it must not be able to push a proposal or a live approval onto a second
    /// page for the whole of its retention window.
    /// </remarks>
    private static int WorklistRank(AccountDeletionQueueRecord record)
        => record.State switch
        {
            QueuedAccountDeletionState.Pending   => 0,
            QueuedAccountDeletionState.Stranded  => 1,
            QueuedAccountDeletionState.Completed => 3,
            _                                    => 2
        };

    private static QueuedAccountDeletion Describe(AccountDeletionQueueRecord record)
        => new()
        {
            UserId                 = record.UserId,
            LastActivityAt         = record.LastActivityAt,
            ThresholdMonths        = record.ThresholdMonths,
            Reason                 = record.Reason,
            EnqueuedAt             = record.EnqueuedAt,
            State                  = record.State,
            DecidedByOperatorId    = record.DecidedByOperatorId,
            DecidedByOperatorEmail = record.DecidedByOperatorEmail,
            DecidedAt              = record.DecidedAt,
            ScheduledDeletionAt    = record.ScheduledDeletionAt,
            StrandedSince          = record.StrandedSince,
            FailureReason          = record.FailureReason,
            ExecutionAttempts      = record.ExecutionAttempts,
            CompletedAt            = record.CompletedAt
        };
}
