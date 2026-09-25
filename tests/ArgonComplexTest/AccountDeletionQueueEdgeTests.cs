namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using Argon.Grains.Persistence.States;
using ArgonComplexTest.Infrastructure.Account;
using Orleans.Runtime;

/// <summary>
/// The operator's account-deletion queue, driven directly: holds that lapse, decisions that cannot be
/// made twice, and entries whose deletion has gone quiet or cannot be read.
/// </summary>
/// <remarks>
/// <para>Every test here gets <em>a queue of its own</em> — the same grain class under a fresh key
/// rather than <c>IAccountDeletionQueueGrain.SingletonId</c>. Nothing in the grain depends on its key,
/// and the production singleton is shared by every fixture in the process: a reconciliation replaces
/// the whole pending half of the queue with the candidates it is handed, so a test calling
/// <c>ReconcileAsync</c> on the singleton with its own two candidates would retire every other
/// fixture's entries. <c>AccountConsoleTests</c> and <c>AdminConsoleTests</c> cover the singleton as
/// the scan and the console drive it.</para>
///
/// <para>The deletions the queue consults are real deletion grains. Where a test needs one in a state
/// no call produces — out of attempts, or unable to activate — its record is written or poisoned the
/// way <c>AccountDeletionTests</c> seeds a lost activation.</para>
/// </remarks>
[TestFixture]
public class AccountDeletionQueueEdgeTests : TestBase
{
    private const string QueueStateName    = "account-deletion-queue-store";
    private const string DeletionStateName = "account-deletion-store";
    private const string OperatorEmail     = "operator@test.local";

    private static readonly Guid Operator = Guid.NewGuid();

    private static IAccountDeletionQueueGrain PrivateQueue()
        => LifecycleDataHarness.Grains.GetGrain<IAccountDeletionQueueGrain>(Guid.NewGuid());

    private static GrainId DeletionId(Guid userId)
        => LifecycleDataHarness.Grains.GetGrain<IAccountDeletionGrain>(userId).GetGrainId();

    private static AccountDeletionCandidate Candidate(Guid userId) => new()
    {
        UserId          = userId,
        LastActivityAt  = DateTimeOffset.UtcNow.AddDays(-400),
        ThresholdMonths = 12,
        Reason          = AccountDeletionQueueReasons.InactivityDefault
    };

    private static AccountDeletionQueueRecord Entry(Guid userId, QueuedAccountDeletionState state) => new()
    {
        UserId                 = userId,
        LastActivityAt         = DateTimeOffset.UtcNow.AddDays(-400),
        ThresholdMonths        = 12,
        Reason                 = AccountDeletionQueueReasons.InactivityDefault,
        EnqueuedAt             = DateTimeOffset.UtcNow.AddDays(-1),
        State                  = state,
        DecidedByOperatorId    = state is QueuedAccountDeletionState.Pending ? null : Operator,
        DecidedByOperatorEmail = state is QueuedAccountDeletionState.Pending ? null : OperatorEmail,
        DecidedAt              = state is QueuedAccountDeletionState.Pending ? null : DateTimeOffset.UtcNow.AddHours(-1)
    };

    /// <summary>
    /// An operator's refusal keeps the account off the queue while it stands, and not a moment longer.
    /// </summary>
    /// <remarks>
    /// A rejection that only removed the entry would see the same account proposed again by the next
    /// scan — defect CON-4 in an operator's hands — so it leaves a hold. The hold is purged by the first
    /// reconciliation after it lapses, and the account can be proposed again. The lapsed hold is
    /// written before the queue ever activates: it is the record of a refusal whose year ran out while
    /// nothing scanned.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_refusal_holds_an_account_off_the_queue_while_it_stands_and_not_after(CancellationToken ct = default)
    {
        var queue  = PrivateQueue();
        var id     = queue.GetGrainId();
        var held   = Guid.NewGuid();
        var lapsed = Guid.NewGuid();

        await LifecycleDataHarness.WriteStateAsync(id, QueueStateName, new AccountDeletionQueueGrainState
        {
            RejectedUntil = { [lapsed] = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1) }
        });

        var first    = await queue.ReconcileAsync([Candidate(held)]);
        var purged   = await LifecycleDataHarness.ReadStateAsync<AccountDeletionQueueGrainState>(id, QueueStateName);
        var rejected = await queue.RejectAsync(held, Operator, OperatorEmail);
        var second   = await queue.ReconcileAsync([Candidate(held), Candidate(lapsed)]);
        var holds    = await queue.GetDeclineHoldsAsync();
        var listed   = (await queue.ListAsync(0, 50)).Entries;

        Assert.Multiple(() =>
        {
            Assert.That(first.Enqueued, Is.EqualTo(1));
            Assert.That(purged.RejectedUntil.Keys, Does.Not.Contain(lapsed), "a hold that had run out was kept");
            Assert.That(rejected.Success, Is.True, $"the rejection was refused: {rejected.Error}");

            Assert.That(second.Held, Is.EqualTo(1), "the rejected account was not held back from the queue");
            Assert.That(second.Enqueued, Is.EqualTo(1), "the account whose hold had lapsed was not proposed again");
            Assert.That(holds, Is.EqualTo(new[] { held }));
            Assert.That(listed.Select(e => e.UserId), Is.EqualTo(new[] { lapsed }),
                "the queue should hold the account whose refusal lapsed and not the one just refused");
        });
    }

    /// <summary>
    /// A decision is made once, and only about an account the queue holds.
    /// </summary>
    /// <remarks>
    /// <para>Approving a second time and rejecting an approval are both refused as
    /// <c>AlreadyDecided</c>, with the schedule the first decision armed; the countdown belongs to the
    /// account holder's console from then on. A decision about an account the queue never held is
    /// <c>NotQueued</c>.</para>
    ///
    /// <para>Then the account holder calls the deletion off, and a refresh — what the admin console
    /// performs after acting on an account — retires the approval: a record of a deletion that is not
    /// going to happen is defect R23. A refresh of a pending entry leaves it alone; a pending entry is
    /// the scan's to retire, not the refresh's.</para>
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_decision_is_made_once_and_only_about_an_account_the_queue_holds(CancellationToken ct = default)
    {
        var queue   = PrivateQueue();
        var session = await CreateSessionAsync(ct);
        var waiting = Guid.NewGuid();

        var approveStranger = await queue.ApproveAsync(Guid.NewGuid(), Operator, OperatorEmail);
        var rejectStranger  = await queue.RejectAsync(Guid.NewGuid(), Operator, OperatorEmail);

        await queue.ReconcileAsync([Candidate(session.UserId), Candidate(waiting)]);

        var approved      = await queue.ApproveAsync(session.UserId, Operator, OperatorEmail);
        var approvedTwice = await queue.ApproveAsync(session.UserId, Operator, OperatorEmail);
        var rejectedAfter = await queue.RejectAsync(session.UserId, Operator, OperatorEmail);

        Assert.Multiple(() =>
        {
            Assert.That(approveStranger.Error, Is.EqualTo(AccountDeletionQueueDecisionError.NotQueued));
            Assert.That(rejectStranger.Error, Is.EqualTo(AccountDeletionQueueDecisionError.NotQueued));

            Assert.That(approved.Success, Is.True, $"the approval was refused: {approved.Error} / {approved.RequestError}");

            Assert.That(approvedTwice.Success, Is.False, "an approved deletion was approved again");
            Assert.That(approvedTwice.Error, Is.EqualTo(AccountDeletionQueueDecisionError.AlreadyDecided));
            Assert.That(approvedTwice.ScheduledDeletionAt, Is.EqualTo(approved.ScheduledDeletionAt),
                "the refusal does not carry the schedule the first approval armed");

            Assert.That(rejectedAfter.Success, Is.False, "an operator rejected a deletion that is already counting down");
            Assert.That(rejectedAfter.Error, Is.EqualTo(AccountDeletionQueueDecisionError.AlreadyDecided));
        });

        var cancelled = await LifecycleDataHarness.Grains.GetGrain<IAccountDeletionGrain>(session.UserId).CancelDeletionAsync();
        Assert.That(cancelled.Success, Is.True, $"the account holder could not call the deletion off: {cancelled.Error}");

        await queue.RefreshAsync(waiting);
        await queue.RefreshAsync(session.UserId);

        var listed = (await queue.ListAsync(0, 50)).Entries;

        Assert.Multiple(() =>
        {
            Assert.That(listed.Select(e => e.UserId), Does.Not.Contain(session.UserId),
                "an approval whose deletion the account holder called off is still on the queue");
            Assert.That(listed.SingleOrDefault(e => e.UserId == waiting)?.State, Is.EqualTo(QueuedAccountDeletionState.Pending),
                "a refresh touched an entry nobody has decided on");
        });
    }

    /// <summary>
    /// An approved entry whose erasure gave up moves to the stranded page and stays put.
    /// </summary>
    /// <remarks>
    /// A deletion out of attempts disarms its own poll, and nothing will touch it again (defect R5);
    /// the queue is the one surface that can say so. A reconciliation that finds one marks the entry
    /// stranded, carries the reason and the attempts an operator needs before resuming it, and does not
    /// restart the clock on a second pass.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task An_approval_whose_erasure_gave_up_moves_to_the_stranded_page_and_stays_put(CancellationToken ct = default)
    {
        var queue   = PrivateQueue();
        var account = Guid.NewGuid();
        var bound   = AccountTimings.Deletion.MaxExecutionAttempts;
        var now     = DateTimeOffset.UtcNow;

        await LifecycleDataHarness.WriteStateAsync(DeletionId(account), DeletionStateName, new AccountDeletionGrainState
        {
            Status            = AccountDeletionStatus.Failed,
            ScheduledAt       = now - AccountTimings.Grace,
            ExecutionAt       = now - AccountTimings.Slack,
            StepsDone         = [1, 2, 3],
            ExecutionAttempts = bound,
            FailureReason     = "seeded: the private-data step keeps timing out",
            Trigger           = AccountDeletionTrigger.AutoInactivity
        });

        await LifecycleDataHarness.WriteStateAsync(queue.GetGrainId(), QueueStateName, new AccountDeletionQueueGrainState
        {
            Entries = [Entry(account, QueuedAccountDeletionState.Approved)]
        });

        var first     = await queue.ReconcileAsync([]);
        var marked    = (await queue.ListStrandedAsync(0, 50)).Entries.SingleOrDefault(e => e.UserId == account);
        var second    = await queue.ReconcileAsync([]);
        var remarked  = (await queue.ListStrandedAsync(0, 50)).Entries.SingleOrDefault(e => e.UserId == account);

        Assert.That(marked, Is.Not.Null, "an approval whose erasure gave up is not on the stranded page");

        Assert.Multiple(() =>
        {
            Assert.That(first.Retired, Is.Zero, "the stranded approval was retired, and with it the only record of it");
            Assert.That(second.Retired, Is.Zero);
            Assert.That(marked!.State, Is.EqualTo(QueuedAccountDeletionState.Stranded));
            Assert.That(marked.StrandedSince, Is.Not.Null);
            Assert.That(marked.FailureReason, Does.Contain("seeded"), "the operator is not told what stopped it");
            Assert.That(marked.ExecutionAttempts, Is.EqualTo(bound));
            Assert.That(marked.DecidedByOperatorId, Is.EqualTo(Operator), "the record of who approved it was lost");
            Assert.That(remarked!.StrandedSince, Is.EqualTo(marked.StrandedSince),
                "a second pass restarted the clock on how long the erasure has been stranded");
        });
    }

    /// <summary>
    /// An entry whose deletion cannot be read is kept as it was, and does not stop a rejection.
    /// </summary>
    /// <remarks>
    /// The deletion grain is the fact and the entry only a belief, so the queue asks — and when the
    /// answer cannot be had, it errs towards keeping records and towards letting the operator act.
    /// A decided entry is carried unchanged rather than lost, and a rejection proceeds as if no deletion
    /// stood, because refusing an operator on a store blip is worse than the retry they get instead.
    /// Both deletion grains are made unable to activate.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task An_entry_whose_deletion_cannot_be_read_is_kept_and_does_not_block_a_rejection(CancellationToken ct = default)
    {
        var queue    = PrivateQueue();
        var decided  = Guid.NewGuid();
        var proposed = Guid.NewGuid();

        await LifecycleDataHarness.WriteStateAsync(queue.GetGrainId(), QueueStateName, new AccountDeletionQueueGrainState
        {
            Entries = [Entry(decided, QueuedAccountDeletionState.Approved), Entry(proposed, QueuedAccountDeletionState.Pending)]
        });

        var decidedOriginal  = await LifecycleDataHarness.BreakActivationAsync(DeletionId(decided), DeletionStateName, ct);
        var proposedOriginal = await LifecycleDataHarness.BreakActivationAsync(DeletionId(proposed), DeletionStateName, ct);

        try
        {
            var scan     = await queue.ReconcileAsync([Candidate(proposed)]);
            var kept     = (await queue.ListAsync(0, 50)).Entries.SingleOrDefault(e => e.UserId == decided);
            var rejected = await queue.RejectAsync(proposed, Operator, OperatorEmail);
            var holds    = await queue.GetDeclineHoldsAsync();

            Assert.Multiple(() =>
            {
                Assert.That(scan.Retired, Is.Zero, "an entry was retired on a deletion status nobody could read");
                Assert.That(kept?.State, Is.EqualTo(QueuedAccountDeletionState.Approved),
                    "the decided entry was changed on the strength of an answer that never came");
                Assert.That(rejected.Success, Is.True,
                    $"an unreadable deletion status blocked the operator's rejection: {rejected.Error}");
                Assert.That(holds, Does.Contain(proposed), "the rejection left no hold behind it");
            });
        }
        finally
        {
            await LifecycleDataHarness.RestoreRecordAsync(DeletionId(decided), DeletionStateName, decidedOriginal);
            await LifecycleDataHarness.RestoreRecordAsync(DeletionId(proposed), DeletionStateName, proposedOriginal);
        }
    }
}
