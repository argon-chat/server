namespace ArgonComplexTest.Tests;

using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Auth;
using Argon.Features.Logic;
using Argon.Features.Storage;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using Argon.Grains.Persistence.States;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

/// <summary>
/// The account erasure off its happy path: callers it has to turn away, runs it has to pick up again,
/// and steps whose collaborators fail underneath it.
/// </summary>
/// <remarks>
/// <para>The neighbours (<c>AccountDeletionTests</c>, <c>AccountConsoleTests</c>) drive the erasure the
/// way a person does — request, wait out the grace, execute. Almost everything here goes through
/// <c>IAccountDeletionGrain.EraseNowAsync</c> instead, the operator's "erase now" button, which runs
/// the same ten steps synchronously with no grace at all. That keeps this fixture off the deletion
/// clocks: nothing below waits for a grace period, and the only waits are for a reminder the test is
/// about.</para>
///
/// <para>Failures are injected one grain at a time (<see cref="LifecycleDataHarness"/>) or one Redis
/// key at a time, always keyed by an account or a space this test created, so no neighbouring fixture
/// can see them. Each one is undone before the test ends, and every erasure a test breaks is finished
/// before it returns — the operator queue is one cluster-wide activation, and an erasure left stranded
/// would sit on its stranded page for every other fixture.</para>
/// </remarks>
[TestFixture]
public class AccountDeletionEdgeTests : TestBase
{
    private const string DeletionStateName = "account-deletion-store";
    private const string CheckReminder     = "account-deletion-check";

    private static IAccountDeletionGrain Deletion(Guid userId)
        => LifecycleDataHarness.Grains.GetGrain<IAccountDeletionGrain>(userId);

    private static GrainId DeletionId(Guid userId) => Deletion(userId).GetGrainId();

    private IUserPresenceService Presence => FactoryAsp.Services.GetRequiredService<IUserPresenceService>();

    // ── callers the erasure turns away ──────────────────────────────────────────────────────────

    /// <summary>
    /// An id with no account behind it is refused by every door, and no poll is armed for it.
    /// </summary>
    /// <remarks>
    /// Four entry points arm a countdown — the person's request, the sweep's proposal, an operator's
    /// approval and an operator's "erase now" — and each reads the account row before it does anything
    /// else. None of them may arm a reminder for a row that is not there: it would fire for ever with
    /// nothing to do.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task An_id_with_no_account_is_refused_by_every_entry_point_and_nothing_is_armed(CancellationToken ct = default)
    {
        var nobody = Guid.NewGuid();
        var grain  = Deletion(nobody);

        var byPerson   = await grain.RequestDeletionAsync("not-a-password");
        var bySweep    = await grain.RequestAutoDeleteAsync();
        var byOperator = await grain.StartByOperatorAsync();
        var erased     = await grain.EraseNowAsync();
        var resumed    = await grain.ResumeAsync();
        var cancelled  = await grain.CancelDeletionAsync();
        var armed      = await LifecycleDataHarness.ReminderAsync(DeletionId(nobody), CheckReminder);

        Assert.Multiple(() =>
        {
            Assert.That(byPerson.Error, Is.EqualTo(AccountDeletionRequestError.InternalError));
            Assert.That(bySweep.Error, Is.EqualTo(AccountDeletionRequestError.InternalError));
            Assert.That(byOperator.Error, Is.EqualTo(AccountDeletionRequestError.InternalError));
            Assert.That(erased.Error, Is.EqualTo(AccountDeletionRequestError.InternalError),
                "an immediate erasure of an account that does not exist reported something other than a refusal");
            Assert.That(resumed.Status, Is.EqualTo(AccountDeletionStatusKind.None),
                "resuming a deletion that never started started one");
            Assert.That(cancelled.Error, Is.EqualTo(AccountDeletionCancelError.NotScheduled));
            Assert.That(armed, Is.Null, "a poll was armed for an account that does not exist");
        });
    }

    /// <summary>
    /// The operator's immediate erasure applies the same bars as every other door.
    /// </summary>
    [Test, CancelAfter(60_000)]
    public async Task An_immediate_erasure_of_a_locked_account_is_refused_and_arms_nothing(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        await AccountSeed.LockAsync(session.UserId, ct: ct);

        try
        {
            var erased = await Deletion(session.UserId).EraseNowAsync();
            var status = await Deletion(session.UserId).GetDeletionStatusAsync();
            var armed  = await LifecycleDataHarness.ReminderAsync(DeletionId(session.UserId), CheckReminder);

            Assert.Multiple(() =>
            {
                Assert.That(erased.Success, Is.False, "an account under investigation was erased on an operator's click");
                Assert.That(erased.Error, Is.EqualTo(AccountDeletionRequestError.AccountLocked));
                Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.None),
                    "the refusal left a countdown behind it");
                Assert.That(armed, Is.Null, "the refusal left a poll armed");
            });

            Assert.That(await AccountSeed.IsVisibleAsync(session.UserId, ct), Is.True, "the refused erasure erased the account");
        }
        finally
        {
            await AccountSeed.UnlockAsync(session.UserId, ct);
        }
    }

    /// <summary>
    /// A failed run that already anonymised the row answers a new request and a sign-in truthfully.
    /// </summary>
    /// <remarks>
    /// <para>Correction C7: a run that threw after step 3 leaves a soft-deleted row, and the request
    /// path used to look it up through the soft-delete filter, find nothing and answer
    /// <c>InternalError</c> for ever. There is no password left on that row to check and nothing left
    /// to schedule, so the answer is <c>AlreadyScheduled</c>, carrying the date the run was due.</para>
    ///
    /// <para>And a sign-in may not call that run off even when the sweep started it: an inactivity
    /// deletion is the one a sign-in normally cancels, but past the point of no return there is
    /// nothing a cancellation could give back. The attempt count is seeded at the bound so the grain
    /// does not re-arm its poll and race the assertions.</para>
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_run_that_already_anonymised_the_row_answers_a_new_request_and_a_sign_in_truthfully(
        CancellationToken ct = default)
    {
        var session     = await CreateSessionAsync(ct);
        var executionAt = DateTimeOffset.UtcNow - AccountTimings.Slack;

        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == session.UserId)
               .ExecuteUpdateAsync(set => set
                   .SetProperty(u => u.IsDeleted, true)
                   .SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow), ct);

        await LifecycleDataHarness.WriteStateAsync(DeletionId(session.UserId), DeletionStateName, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Failed,
            ScheduledAt         = executionAt - AccountTimings.Grace,
            ExecutionAt         = executionAt,
            StepsDone           = [1, 2, 3],
            ExecutionAttempts   = AccountTimings.Deletion.MaxExecutionAttempts,
            FailureReason       = "seeded: step 4 threw",
            Trigger             = AccountDeletionTrigger.AutoInactivity,
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        var grain = Deletion(session.UserId);

        var again = await grain.RequestDeletionAsync(session.Credentials.password);

        var calledOff = await grain.NoticeSignInAsync(new SignInEvidence
        {
            Ip     = "198.51.100.23",
            Client = "integration-tests",
            At     = DateTimeOffset.UtcNow
        });

        var status = await grain.GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(again.Success, Is.False);
            Assert.That(again.Error, Is.EqualTo(AccountDeletionRequestError.AlreadyScheduled),
                "a request over a row the erasure already anonymised is told something other than the truth");
            Assert.That(again.ScheduledDeletionAt, Is.EqualTo(executionAt).Within(TimeSpan.FromSeconds(1)),
                "the refusal does not carry the date the erasure was due");

            Assert.That(calledOff, Is.False, "a sign-in called off an erasure that had already anonymised the account");
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.Failed));
            Assert.That(status.Stranded, Is.True, "the run is out of attempts and must say so");
            Assert.That(AccountTimings.Emails.Sent(session.Credentials.email, EmailKinds.DeletionCancelledBySignIn), Is.Empty,
                "the account was told its erasure had been called off");
        });
    }

    // ── runs that are picked up again ───────────────────────────────────────────────────────────

    /// <summary>
    /// A countdown whose activation was lost is re-armed by the next one and runs by itself.
    /// </summary>
    /// <remarks>
    /// <para>Defect ACC-15's recovery for a countdown rather than for an interrupted run: the poll is a
    /// durable reminder, and any activation of a <c>Scheduled</c> grain arms it. The grain below has
    /// never been activated — its record is what a silo that went away leaves behind — and after the
    /// first call wakes it, the test only watches: nothing calls <c>CheckAndExecuteAsync</c>, so a
    /// completion can only have come from the reminder.</para>
    ///
    /// <para>The waking call is itself a reminder tick, of a reminder the deletion grain does not own.
    /// A grain that answered it would execute an elapsed grace on somebody else's timer, so the status
    /// read immediately after it must still be <c>Scheduled</c>.</para>
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task A_countdown_whose_activation_was_lost_is_rearmed_by_the_next_one_and_runs_by_itself(
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var now     = DateTimeOffset.UtcNow;
        var id      = DeletionId(session.UserId);

        await LifecycleDataHarness.WriteStateAsync(id, DeletionStateName, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Scheduled,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            RemindersSent       = [.. AccountTimings.Reminders.Select(AccountDeletionOptions.ReminderKey)],
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        await LifecycleDataHarness.FireReminderAsync(id, "export-archive-ttl");

        var untouched = await Deletion(session.UserId).GetDeletionStatusAsync();
        var armed     = await LifecycleDataHarness.ReminderAsync(id, CheckReminder);

        Assert.Multiple(() =>
        {
            Assert.That(untouched.Status, Is.EqualTo(AccountDeletionStatusKind.Scheduled),
                "a reminder the deletion grain does not own executed the erasure");
            Assert.That(armed, Is.Not.Null,
                "the activation of a scheduled deletion did not arm its poll, so nothing will ever run it");
        });

        var finished = await Poll.ForValueAsync(
            async () => await Deletion(session.UserId).GetDeletionStatusAsync(),
            status => status.Status is AccountDeletionStatusKind.Completed,
            AccountTimings.DeletionPoll * 5 + AccountTimings.ExecutionBudget,
            AccountTimings.Slack / 4,
            ct);

        var disarmed = await LifecycleDataHarness.ReminderAsync(id, CheckReminder);

        Assert.Multiple(() =>
        {
            Assert.That(finished.Status, Is.EqualTo(AccountDeletionStatusKind.Completed),
                $"the re-armed poll never executed the elapsed grace; it is {finished.Status} ('{finished.FailureReason}')");
            Assert.That(AccountTimings.Emails.Sent(session.Credentials.email, EmailKinds.DeletionCompleted), Has.Count.EqualTo(1),
                "the account was not told, once, that it is gone");
            Assert.That(disarmed, Is.Null, "a finished erasure left its poll firing");
        });
    }

    /// <summary>
    /// A run caught mid-flight cannot be cancelled, and a record that names no identity still finishes.
    /// </summary>
    /// <remarks>
    /// <c>Executing</c> is only ever persisted by a run that lost its activation between two steps, and
    /// the console's cancel has to refuse it as <c>AlreadyExecuting</c>. The record is also the shape
    /// written before the grain kept the <c>Original*</c> fields: with no username to reserve and no
    /// address to write to, the steps that would have used them are skipped rather than failing the
    /// erasure.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task A_run_caught_mid_flight_cannot_be_cancelled_and_a_record_without_the_identity_still_finishes(
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var now     = DateTimeOffset.UtcNow;

        await LifecycleDataHarness.WriteStateAsync(DeletionId(session.UserId), DeletionStateName, new AccountDeletionGrainState
        {
            Status      = AccountDeletionStatus.Executing,
            ScheduledAt = now - AccountTimings.Grace,
            ExecutionAt = now - AccountTimings.Slack,
            StepsDone   = [1, 2]
        });

        var cancelled = await Deletion(session.UserId).CancelDeletionAsync();

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        var row = await AccountSeed.ReadUserAsync(session.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Success, Is.False);
            Assert.That(cancelled.Error, Is.EqualTo(AccountDeletionCancelError.AlreadyExecuting),
                "a run that is already erasing was offered to the console as cancellable");
            Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
                $"a record without the identity fields stalled the erasure in {reached}");
            Assert.That(row!.IsDeleted, Is.True, "the resumed run reported success without anonymising the row");
            Assert.That(AccountTimings.Emails.Sent(session.Credentials.email, EmailKinds.DeletionCompleted), Is.Empty,
                "a completion mail was sent to an address the record never named");
        });
    }

    // ── the operator's immediate erasure ────────────────────────────────────────────────────────

    /// <summary>
    /// "Erase now" over a running countdown happens at once, says nothing more, and cannot be repeated.
    /// </summary>
    /// <remarks>
    /// The countdown's own mail has gone out already; what an immediate erasure must not add is the
    /// reminders it just made moot or a completion notice nobody asked for. A second press on a
    /// finished account is refused rather than run again.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task Erasing_a_running_countdown_now_happens_at_once_silently_and_only_once(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var grain   = Deletion(session.UserId);
        var email   = session.Credentials.email;

        var requested = await grain.RequestDeletionAsync(session.Credentials.password);
        Assert.That(requested.Success, Is.True, requested.Error?.ToString());

        var erased = await grain.EraseNowAsync();
        var again  = await grain.EraseNowAsync();
        var status = await grain.GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.True, $"the immediate erasure failed: {erased.Error} ({status.FailureReason})");
            Assert.That(erased.ScheduledDeletionAt, Is.LessThan(requested.ScheduledDeletionAt!.Value),
                "the erasure was not brought forward from the end of the grace");
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.Completed));

            Assert.That(again.Success, Is.False, "a finished erasure ran a second time");
            Assert.That(again.Error, Is.EqualTo(AccountDeletionRequestError.AlreadyScheduled));

            Assert.That(AccountTimings.Emails.Sent(email, EmailKinds.DeletionScheduled), Has.Count.EqualTo(1));
            Assert.That(AccountTimings.Emails.Sent(email, EmailKinds.DeletionReminder), Is.Empty,
                "an immediate erasure posted the reminders it had just made moot");
            Assert.That(AccountTimings.Emails.Sent(email, EmailKinds.DeletionCompleted), Is.Empty,
                "a silent erasure announced itself");
        });
    }

    /// <summary>
    /// "Erase now" over a run that stopped part-way finishes it from its cursor.
    /// </summary>
    [Test, CancelAfter(90_000)]
    public async Task Erasing_a_stranded_run_now_finishes_it_from_where_it_stopped(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var now     = DateTimeOffset.UtcNow;

        await LifecycleDataHarness.WriteStateAsync(DeletionId(session.UserId), DeletionStateName, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Failed,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            StepsDone           = [1, 2],
            ExecutionAttempts   = AccountTimings.Deletion.MaxExecutionAttempts,
            FailureReason       = "seeded: the anonymising step threw",
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        var before = await Deletion(session.UserId).GetDeletionStatusAsync();
        var erased = await Deletion(session.UserId).EraseNowAsync();
        var after  = await Deletion(session.UserId).GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(before.Stranded, Is.True, "premise: the seeded run is out of attempts");
            Assert.That(erased.Success, Is.True, $"the stranded run was not finished: {after.FailureReason}");
            Assert.That(after.Status, Is.EqualTo(AccountDeletionStatusKind.Completed));
            Assert.That(after.Stranded, Is.False);
            Assert.That(AccountTimings.Emails.Sent(session.Credentials.email, EmailKinds.DeletionCompleted), Is.Empty,
                "a silent erasure announced itself");
        });

        Assert.That(await AccountSeed.IsVisibleAsync(session.UserId, ct), Is.False, "the finished run left the account visible");
    }

    // ── steps whose collaborators fail ──────────────────────────────────────────────────────────

    /// <summary>
    /// A session index the erasure cannot read still ends in a sign-out-everywhere floor.
    /// </summary>
    /// <remarks>
    /// The live-session list only makes the revocation prompt — the floor revokes every credential by
    /// date, including the ones the list would have named. So an unreadable index costs promptness and
    /// nothing else: the step goes on and writes the floor. The index is made unreadable by giving its
    /// key the wrong Redis type.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task A_session_index_that_cannot_be_read_still_ends_in_a_revocation_floor(CancellationToken ct = default)
    {
        var session  = await CreateSessionAsync(ct);
        var indexKey = $"presence:user:{session.UserId}:sessions";

        await LifecycleDataHarness.Cache.StringSetAsync(indexKey, "not a set", ct);

        try
        {
            Assert.That(async () => await Presence.GetActiveSessionIdsAsync(session.UserId, ct), Throws.Exception,
                "premise: the session index was made unreadable");

            var erased = await Deletion(session.UserId).EraseNowAsync();
            var floor  = SessionRevocation.ParseFloor(
                await LifecycleDataHarness.Cache.StringGetAsync(SessionRevocation.FloorKey(session.UserId), ct));

            Assert.Multiple(() =>
            {
                Assert.That(erased.Success, Is.True, $"an unreadable session index failed the erasure: {erased.Error}");
                Assert.That(floor, Is.Not.Null,
                    "the erasure wrote no floor, so every credential the account holds is still honoured");
            });
        }
        finally
        {
            await LifecycleDataHarness.Cache.KeyDeleteAsync(indexKey, ct);
        }
    }

    /// <summary>
    /// Only a session id that names a device is tombstoned.
    /// </summary>
    /// <remarks>
    /// <c>Guid.Empty</c> is "nothing was carried", <c>Guid.AllBitsSet</c> is the placeholder a
    /// development host hands a caller with no session header, and a non-GUID id is a legacy sid. None
    /// of them names one device, so tombstoning one would pool unrelated sessions under a single entry
    /// — the guard <c>SecurityGrain.EndSessionAsync</c> makes for the same reason. All four sessions
    /// leave the live index either way.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task Only_a_session_id_that_names_a_device_is_tombstoned(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var device  = Guid.NewGuid().ToString();
        string[] anonymous = ["legacy-sid", Guid.Empty.ToString(), Guid.AllBitsSet.ToString()];

        foreach (var sid in anonymous.Prepend(device))
            await Presence.SetSessionOnlineAsync(session.UserId, sid, ct);

        var erased  = await Deletion(session.UserId).EraseNowAsync();
        var revoked = await LifecycleDataHarness.Cache.SetMembersAsync(SessionRevocation.RevokedKey(session.UserId), ct);
        var live    = await Presence.GetActiveSessionIdsAsync(session.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.True, $"the erasure failed: {erased.Error}");
            Assert.That(revoked, Does.Contain(device), "the device's session was not tombstoned");

            foreach (var sid in anonymous)
                Assert.That(revoked, Does.Not.Contain(sid), $"'{sid}' names no device and was tombstoned anyway");

            Assert.That(live, Is.Empty, "the erased account still has live sessions in the index");
        });
    }

    /// <summary>
    /// A revocation store that refuses the per-session tombstone, and a presence store that refuses the
    /// clean-up, do not stop the erasure.
    /// </summary>
    /// <remarks>
    /// Both are best-effort by design — the floor, written before either, is what revokes — so each is
    /// made to throw by giving its key the wrong Redis type and the erasure is expected to finish with
    /// the floor in place.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task Tombstone_and_presence_clean_up_failures_do_not_stop_the_erasure(CancellationToken ct = default)
    {
        var session     = await CreateSessionAsync(ct);
        var device      = Guid.NewGuid().ToString();
        var revokedKey  = SessionRevocation.RevokedKey(session.UserId);
        var activityKey = $"activity:user:{session.UserId}:session:{device}";

        await Presence.SetSessionOnlineAsync(session.UserId, device, ct);
        await LifecycleDataHarness.Cache.StringSetAsync(revokedKey, "not a set", ct);
        await LifecycleDataHarness.Cache.SetAddAsync(activityKey, "not a string", ct);

        try
        {
            Assert.Multiple(() =>
            {
                Assert.That(async () => await LifecycleDataHarness.Cache.SetAddAsync(revokedKey, "probe", ct), Throws.Exception,
                    "premise: the tombstone set refuses writes");
                Assert.That(async () => await Presence.RemoveActivityPresence(session.UserId, device), Throws.Exception,
                    "premise: the session's activity cannot be cleared");
            });

            var erased = await Deletion(session.UserId).EraseNowAsync();
            var floor  = SessionRevocation.ParseFloor(
                await LifecycleDataHarness.Cache.StringGetAsync(SessionRevocation.FloorKey(session.UserId), ct));

            Assert.Multiple(() =>
            {
                Assert.That(erased.Success, Is.True, $"a best-effort revocation step failed the erasure: {erased.Error}");
                Assert.That(floor, Is.Not.Null, "the floor, which is what actually revokes, was not written");
            });
        }
        finally
        {
            await LifecycleDataHarness.Cache.KeyDeleteAsync(revokedKey, ct);
            await LifecycleDataHarness.Cache.KeyDeleteAsync(activityKey, ct);
        }
    }

    /// <summary>
    /// An export grain that cannot be reached does not hold the erasure open.
    /// </summary>
    /// <remarks>
    /// Step 2 cancels any running export before it deletes the archive prefix, and the cancellation is
    /// a courtesy: an export grain that will not answer must not be able to keep an account from being
    /// erased. The export grain is made unable to activate.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task An_export_grain_that_cannot_be_reached_does_not_hold_the_erasure_open(CancellationToken ct = default)
    {
        var session  = await CreateSessionAsync(ct);
        var exportId = LifecycleDataHarness.Grains.GetGrain<IUserDataExportGrain>(session.UserId).GetGrainId();

        var original = await LifecycleDataHarness.BreakActivationAsync(exportId, "user-data-export-store", ct);

        try
        {
            var erased = await Deletion(session.UserId).EraseNowAsync();
            var state  = await LifecycleDataHarness.ReadStateAsync<AccountDeletionGrainState>(DeletionId(session.UserId), DeletionStateName);

            Assert.Multiple(() =>
            {
                Assert.That(erased.Success, Is.True,
                    $"an unreachable export grain held the erasure open: {erased.Error} ({state.FailureReason})");
                Assert.That(state.StepsDone, Does.Contain(2), "the archive purge was not recorded as done");
            });
        }
        finally
        {
            await LifecycleDataHarness.RestoreRecordAsync(exportId, "user-data-export-store", original);
        }
    }

    /// <summary>
    /// An avatar the account had already soft-deleted is released exactly once.
    /// </summary>
    /// <remarks>
    /// The step-8 walk releases every file the account owns that is <em>not</em> soft-deleted, so the
    /// anonymising step releases the avatar only when the walk will not reach it (defect ACC-08). A
    /// soft-deleted avatar is that case: released here, once, and never again — a count of one going
    /// to zero, which is what lets <c>FileGcService</c> collect the bytes.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task An_avatar_already_soft_deleted_is_released_exactly_once(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var fileId  = Guid.CreateVersion7();
        var now     = DateTimeOffset.UtcNow;

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            db.Files.Add(new FileEntity
            {
                Id = fileId, OwnerId = session.UserId, Purpose = FilePurpose.Avatar,
                S3Key = $"seeded/{fileId:N}", BucketName = "seeded", FileSize = 3,
                ContentType = "image/png", FileName = "avatar.png", Finalized = true,
                IsDeleted = true, DeletedAt = now, CreatedAt = now, UpdatedAt = now
            });
            db.FileCounters.Add(new FileCounterEntity { Id = fileId, RefCount = 1, CreatedAt = now, UpdatedAt = now });

            await db.SaveChangesAsync(ct);

            await db.Users.Where(u => u.Id == session.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.AvatarFileId, fileId.ToString()), ct);
        }

        var erased = await Deletion(session.UserId).EraseNowAsync();

        await using var read = await AccountSeed.NewDbAsync(ct);

        var refs = await read.FileCounters.IgnoreQueryFilters()
           .Where(c => c.Id == fileId).Select(c => c.RefCount).FirstOrDefaultAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.True, $"the erasure failed: {erased.Error}");
            Assert.That(refs, Is.Zero,
                "the soft-deleted avatar was not released exactly once: one means it was kept for an " +
                "account that no longer exists, below zero means it was released twice");
        });
    }

    /// <summary>
    /// A file the reference counter has no record of does not stop the others being released.
    /// </summary>
    /// <remarks>
    /// Releasing a reference is per file and best-effort: <c>ReferenceCountService</c> throws for a
    /// file with no counter row, and that one file must cost a log line — not the release of every
    /// file after it in the walk, and not the erasure. Seeded here are an avatar and an upload with no
    /// counter row, and an upload with one; the counted upload has to come out at zero.
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task A_file_the_reference_counter_never_recorded_does_not_stop_the_others_being_released(
        CancellationToken ct = default)
    {
        var session   = await CreateSessionAsync(ct);
        var avatar    = Guid.CreateVersion7();
        var uncounted = Guid.CreateVersion7();
        var counted   = Guid.CreateVersion7();
        var now       = DateTimeOffset.UtcNow;

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            foreach (var (fileId, purpose, deleted) in new[]
                     {
                         (avatar, FilePurpose.Avatar, true),
                         (uncounted, FilePurpose.ChannelAttachment, false),
                         (counted, FilePurpose.ChannelAttachment, false)
                     })
            {
                db.Files.Add(new FileEntity
                {
                    Id = fileId, OwnerId = session.UserId, Purpose = purpose,
                    S3Key = $"seeded/{fileId:N}", BucketName = "seeded", FileSize = 3,
                    ContentType = "image/png", FileName = "seeded.png", Finalized = true,
                    IsDeleted = deleted, DeletedAt = deleted ? now : null, CreatedAt = now, UpdatedAt = now
                });
            }

            db.FileCounters.Add(new FileCounterEntity { Id = counted, RefCount = 1, CreatedAt = now, UpdatedAt = now });

            await db.SaveChangesAsync(ct);

            await db.Users.Where(u => u.Id == session.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.AvatarFileId, avatar.ToString()), ct);
        }

        var erased = await Deletion(session.UserId).EraseNowAsync();

        await using var read = await AccountSeed.NewDbAsync(ct);

        var refs = await read.FileCounters.IgnoreQueryFilters()
           .Where(c => c.Id == counted).Select(c => c.RefCount).FirstOrDefaultAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.True, $"a file with no counter failed the erasure: {erased.Error}");
            Assert.That(refs, Is.Zero, "the counted upload was not released because an uncounted one failed");
        });
    }

    /// <summary>
    /// A username that is already reserved does not stop the erasure.
    /// </summary>
    [Test, CancelAfter(90_000)]
    public async Task A_username_that_is_already_reserved_does_not_stop_the_erasure(CancellationToken ct = default)
    {
        var session    = await CreateSessionAsync(ct);
        var normalized = session.Credentials.username.ToLowerInvariant();

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            db.Add(new UsernameReservedEntity
            {
                Id = Guid.NewGuid(), UserName = session.Credentials.username, NormalizedUserName = normalized,
                IsReserved = true
            });

            await db.SaveChangesAsync(ct);
        }

        var erased = await Deletion(session.UserId).EraseNowAsync();

        await using var read = await AccountSeed.NewDbAsync(ct);

        var reservations = await read.Set<UsernameReservedEntity>().CountAsync(r => r.NormalizedUserName == normalized, ct);

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.True, $"a reservation that already existed failed the erasure: {erased.Error}");
            Assert.That(reservations, Is.EqualTo(1), "the name is not reserved exactly once");
        });
    }

    /// <summary>
    /// The teams an account owns go with it, and their bots with them.
    /// </summary>
    [Test, CancelAfter(90_000)]
    public async Task The_bots_of_the_teams_an_account_owns_are_retired_with_it(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var teams = LifecycleDataHarness.Grains.GetGrain<IDevTeamsGrain>(Guid.Empty);
        var tag   = Guid.NewGuid().ToString("N")[..12];

        var team = await teams.CreateTeamAsync(owner.UserId, $"edge-{tag}", ct);
        var bot  = await teams.CreateBotAppAsync(team.teamId, "Edge Bot", $"edge{tag}bot", ct);

        var erased = await Deletion(owner.UserId).EraseNowAsync();

        await using var db = await AccountSeed.NewDbAsync(ct);

        var teamGone = await db.TeamEntities.IgnoreQueryFilters()
           .Where(t => t.TeamId == team.teamId).Select(t => t.IsDeleted).FirstOrDefaultAsync(ct);
        var botGone = await db.BotEntities.IgnoreQueryFilters()
           .Where(b => b.AppId == bot.appId).Select(b => b.IsDeleted).FirstOrDefaultAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.True, $"the erasure failed: {erased.Error}");
            Assert.That(teamGone, Is.True, "a team whose owner no longer exists is still live");
            Assert.That(botGone, Is.True, "a bot whose team's owner no longer exists is still live");
        });
    }

    /// <summary>
    /// A private space that cannot be deleted fails the step, and the retry deletes it.
    /// </summary>
    /// <remarks>
    /// A private space is deleted with its owner rather than left with nobody able to run it, so a
    /// space whose deletion throws must not be recorded as dealt with: the step collects the failure,
    /// throws, and is not recorded, and the next attempt — here an operator's resume, once the space's
    /// deletion grain can activate again — deletes it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_private_space_that_cannot_be_deleted_fails_the_step_until_it_can(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var space    = await CreateSpaceAsync(owner, "Undeletable for now", ct);
        var breaking = LifecycleDataHarness.Grains.GetGrain<ISpaceDeletionGrain>(space).GetGrainId();
        var grain    = Deletion(owner.UserId);

        var original = await LifecycleDataHarness.BreakActivationAsync(breaking, "space-deletion-store", ct);

        AccountDeletionRequestResult erased;
        AccountDeletionGrainState    stopped;
        bool                         spaceStood;

        try
        {
            erased  = await grain.EraseNowAsync();
            stopped = await LifecycleDataHarness.ReadStateAsync<AccountDeletionGrainState>(DeletionId(owner.UserId), DeletionStateName);

            await using var db = await AccountSeed.NewDbAsync(ct);
            spaceStood = await db.Spaces.AnyAsync(s => s.Id == space && !s.IsDeleted, ct);
        }
        finally
        {
            await LifecycleDataHarness.RestoreRecordAsync(breaking, "space-deletion-store", original);
        }

        await grain.ResumeAsync();

        var finished = await AccountConsoleHarness.DriveDeletionUntilAsync(
            owner.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        await using var after = await AccountSeed.NewDbAsync(ct);

        var spaceGone = await after.Spaces.IgnoreQueryFilters()
           .Where(s => s.Id == space).Select(s => s.IsDeleted).FirstOrDefaultAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.False, "the erasure reported success over a space it could not delete");
            Assert.That(erased.Error, Is.EqualTo(AccountDeletionRequestError.InternalError));
            Assert.That(stopped.FailureReason, Does.Contain(space.ToString()), "the failure does not name the space");
            Assert.That(stopped.StepsDone, Does.Not.Contain(11), "the space step was recorded as done");
            Assert.That(spaceStood, Is.True, "premise: the space survived the failed attempt");

            Assert.That(finished, Is.EqualTo(AccountDeletionStatusKind.Completed), $"the retry did not finish; it is {finished}");
            Assert.That(spaceGone, Is.True, "the retry left the private space of an account that no longer exists");
        });
    }

    /// <summary>
    /// Spaces that cannot be told the member left keep the step open, and nothing owed is forgotten.
    /// </summary>
    /// <remarks>
    /// <para>Two spaces, both unable to activate. In the first the departure has already committed
    /// and the announcement is owed from an earlier attempt (the state defect R22 leaves); in the
    /// second the membership is still live. The attempt must fail without recording step 6, keep the
    /// owed announcement for the next attempt, and leave the live membership alone so the next attempt
    /// walks it — a membership removed without an announcement is exactly what no later walk can
    /// rediscover.</para>
    ///
    /// <para>Then both spaces come back, an operator resumes, and the retry has to pay both: the owed
    /// announcement is replayed and cleared, and the live membership is removed.</para>
    /// </remarks>
    [Test, CancelAfter(150_000)]
    public async Task Spaces_that_cannot_be_told_keep_the_membership_step_open_and_nothing_owed_is_forgotten(
        CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var victim = await CreateSessionAsync(ct);
        var owed   = await CreateSpaceAsync(owner, "Departure owed", ct);
        var live   = await CreateSpaceAsync(owner, "Membership live", ct);

        await JoinSpaceAsync(owner, victim, owed, ct);
        await JoinSpaceAsync(owner, victim, live, ct);

        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.UsersToServerRelations
               .Where(m => m.SpaceId == owed && m.UserId == victim.UserId)
               .ExecuteUpdateAsync(set => set
                   .SetProperty(m => m.IsDeleted, true)
                   .SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow), ct);

        var owedGrain = LifecycleDataHarness.Grains.GetGrain<ISpaceGrain>(owed).GetGrainId();
        var liveGrain = LifecycleDataHarness.Grains.GetGrain<ISpaceGrain>(live).GetGrainId();

        var now = DateTimeOffset.UtcNow;

        await LifecycleDataHarness.WriteStateAsync(DeletionId(victim.UserId), DeletionStateName, new AccountDeletionGrainState
        {
            Status                        = AccountDeletionStatus.Executing,
            ScheduledAt                   = now - AccountTimings.Grace,
            ExecutionAt                   = now - AccountTimings.Slack,
            StepsDone                     = [1, 2],
            PendingDepartureAnnouncements = [owed],
            OriginalEmail                 = victim.Credentials.email,
            OriginalUsername              = victim.Credentials.username,
            OriginalDisplayName           = victim.Credentials.displayName
        });

        var owedOriginal = await LifecycleDataHarness.BreakActivationAsync(owedGrain, SpaceStateName, ct);
        var liveOriginal = await LifecycleDataHarness.BreakActivationAsync(liveGrain, SpaceStateName, ct);

        AccountDeletionRequestResult erased;
        AccountDeletionGrainState    stopped;
        bool                         liveStood;

        try
        {
            erased  = await Deletion(victim.UserId).EraseNowAsync();
            stopped = await LifecycleDataHarness.ReadStateAsync<AccountDeletionGrainState>(DeletionId(victim.UserId), DeletionStateName);

            await using var db = await AccountSeed.NewDbAsync(ct);
            liveStood = await db.UsersToServerRelations
               .AnyAsync(m => m.SpaceId == live && m.UserId == victim.UserId && !m.IsDeleted, ct);
        }
        finally
        {
            await LifecycleDataHarness.RestoreRecordAsync(owedGrain, SpaceStateName, owedOriginal);
            await LifecycleDataHarness.RestoreRecordAsync(liveGrain, SpaceStateName, liveOriginal);
        }

        await Deletion(victim.UserId).ResumeAsync();

        var finished = await AccountConsoleHarness.DriveDeletionUntilAsync(
            victim.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        var settled = await LifecycleDataHarness.ReadStateAsync<AccountDeletionGrainState>(DeletionId(victim.UserId), DeletionStateName);
        var roster  = (await owner.Servers.GetMembers(live, ct)).Values.Select(m => m.member.userId).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(erased.Success, Is.False, "the erasure reported success over two spaces that were never told");
            Assert.That(stopped.StepsDone, Does.Not.Contain(6), "the membership step was recorded as done");
            Assert.That(stopped.PendingDepartureAnnouncements, Does.Contain(owed),
                "the announcement that could not be replayed was forgotten, and no later walk can find it");
            Assert.That(stopped.FailureReason, Does.Contain("2 space(s)"), $"unexpected reason: '{stopped.FailureReason}'");
            Assert.That(liveStood, Is.True,
                "the live membership was removed without an announcement, so the retry would find nothing to announce");

            Assert.That(finished, Is.EqualTo(AccountDeletionStatusKind.Completed),
                $"the retry did not finish; it is {finished} ('{settled.FailureReason}')");
            Assert.That(settled.PendingDepartureAnnouncements, Is.Empty, "the owed announcement was never paid");
            Assert.That(roster, Does.Not.Contain(victim.UserId), "the space still lists the erased account");
        });
    }

    private const string SpaceStateName = "realtime-server";

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Account deletion edge fixture", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
            throw new InvalidOperationException($"could not create the space '{name}': {(result as FailedCreateSpace)?.error}");

        return success.space.spaceId;
    }

    private static async Task JoinSpaceAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        if (joined is not SuccessJoin)
            throw new InvalidOperationException($"the guest could not join {spaceId}: {(joined as FailedJoin)?.error}");
    }
}
