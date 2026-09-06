namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AccountContracts;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;

/// <summary>
/// The account console — the web surface a person uses to delete their account or ask for their data
/// — and the inactivity sweeper that deletes accounts without ever asking anyone.
/// </summary>
/// <remarks>
/// <para>Everything the console does, a grain already does; the console is a mapping layer, seven
/// enum values wide, between what the grain answers and what a browser can render. That makes it the
/// easiest place in the feature for a defect to hide: a mapping that collapses two refusals into one
/// generic error, or drops the date a banner is supposed to show, changes nothing a grain-level test
/// can see and everything the user can. So the fixture asserts the mapping itself — every refusal
/// the grain can produce, seen from the console, and <c>GetMe</c> compared against the grain it is
/// reporting on at every step of a deletion rather than only at the ends.</para>
///
/// <para>The other half is the console's identity model, which is unusual enough to be worth pinning
/// on purpose: no method takes a user id. Each one keys its grain off the ambient request context,
/// and display fields come out of the token's claims rather than the database. That is what makes
/// "user A cannot act on user B" true — there is no argument to forge — and it is also what makes it
/// fragile, because the whole guarantee now rests on the context being set by exactly one thing.
/// These tests pin both sides of that: the caller's identity is the only lever there is, and it is a
/// lever the console honours completely, including when it names a subject that owns no account.</para>
///
/// <para>Auto-deletion is here for the same reason. It shares the grain with the console's own
/// deletion but skips the password, so the only thing standing between an account and erasure is the
/// scan's arithmetic: a threshold, a last-login timestamp and a subscription flag. The cohort test
/// below seeds one account per branch of that arithmetic and runs the real scan over them, because
/// the interesting failures are not "nothing was deleted" but "the wrong one was".</para>
///
/// <para>Deliberately not duplicated here: <c>AccountLifecycleTests</c> already covers the deletion
/// grain's own guards and round trip, and <c>DataExportTests</c> the export over
/// <c>ISecurityInteraction</c>. What follows is the console's view of those, and the branches neither
/// fixture reaches.</para>
/// </remarks>
[TestFixture]
public class AccountConsoleTests : TestBase
{
    // ── Console: what GetMe reports ─────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>GetMe</c> agrees with the deletion grain at every step of a deletion, not just at the ends.
    /// </summary>
    /// <remarks>
    /// <para>The console renders a banner from these three fields — status and the two timestamps —
    /// and a person reads a date off it and plans around it. So the assertion is equality with the
    /// grain rather than a shape check: a <c>GetMe</c> that reports <c>Scheduled</c> with the wrong
    /// execution date is worse than one that reports nothing, and both pass a test that only asks
    /// whether the status changed.</para>
    ///
    /// <para>Cancellation is included in the walk because it is the step that has to clear both
    /// timestamps: a stale <c>deletionExecutionAt</c> left behind after a cancel is exactly the sort
    /// of thing the console would keep showing. And the walk ends after execution rather than at the
    /// grace, because <c>Completed</c> is a state the console can be opened in and has to render.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task GetMe_MirrorsTheDeletionGrain_AtEveryStepOfTheLifecycle(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var grain   = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var fresh = await console.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(fresh.userId, Is.EqualTo(session.UserId));
            Assert.That(fresh.deletionStatus, Is.EqualTo(DeletionStatusKind.None));
            Assert.That(fresh.deletionScheduledAt, Is.Null);
            Assert.That(fresh.deletionExecutionAt, Is.Null);
            Assert.That(fresh.gdrpExportInProgress, Is.False);
        });

        var requested = await console.RequestDeleteAccount(session.Credentials.password, ct);
        var scheduled = await grain.GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(requested.success, Is.True, requested.error.ToString());
            Assert.That(requested.scheduledAt, Is.EqualTo(scheduled.ScheduledAt),
                "the result of the request and the grain's own status have to name the same moment");
            Assert.That(requested.executionAt, Is.EqualTo(scheduled.ExecutionAt));
        });

        var pending = await console.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(pending.deletionStatus, Is.EqualTo(DeletionStatusKind.Scheduled));
            Assert.That(pending.deletionScheduledAt, Is.EqualTo(scheduled.ScheduledAt),
                "the console renders this date; it must be the grain's, not one derived from a grace period");
            Assert.That(pending.deletionExecutionAt, Is.EqualTo(scheduled.ExecutionAt));
        });

        var cancelled = await console.CancelDeleteAccount(ct);
        var afterCancel = await console.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.success, Is.True, cancelled.error.ToString());
            Assert.That(afterCancel.deletionStatus, Is.EqualTo(DeletionStatusKind.None));
            Assert.That(afterCancel.deletionScheduledAt, Is.Null,
                "a cancelled deletion leaves no date behind for the console to keep showing");
            Assert.That(afterCancel.deletionExecutionAt, Is.Null);
        });

        var again = await console.RequestDeleteAccount(session.Credentials.password, ct);
        Assert.That(again.success, Is.True, again.error.ToString());

        // A deadline passing, not a state change: there is nothing to poll for until the grace has
        // elapsed, which is the one case the campaign's rules allow a fixed wait for.
        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, ct: ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the deletion did not finish within {AccountTimings.ExecutionBudget}; status was {reached}, " +
            $"reason: {(await grain.GetDeletionStatusAsync()).FailureReason}");

        var executed = await grain.GetDeletionStatusAsync();
        var done     = await console.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(done.deletionStatus, Is.EqualTo(DeletionStatusKind.Completed));
            Assert.That(done.deletionScheduledAt, Is.EqualTo(executed.ScheduledAt));
            Assert.That(done.deletionExecutionAt, Is.EqualTo(executed.ExecutionAt));
        });
    }

    /// <summary>
    /// The identity in the header is the token's, not the database's — and that is the whole contract.
    /// </summary>
    /// <remarks>
    /// Deliberate, documented, and worth a regression guard precisely because it looks like a bug:
    /// the console never loads a user row to render its own header, so a renamed account shows its
    /// old name until the OIDC token is re-issued. Pinning it here means a later change that starts
    /// reading the database is a decision somebody made rather than one that happened, and it names
    /// the consequence in one place: what the console displays is whatever the token said, including
    /// for an account whose row no longer carries that name at all.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task GetMe_RendersTheIdentityTheTokenCarried_NotTheStoredOne(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var stored  = await AccountSeed.ReadUserAsync(session.UserId, ct);

        const string claimedName   = "Name From The Console Token";
        const string claimedAvatar = "avatar-from-the-console-token";

        var (consoleScope, console) = AccountConsoleHarness.Console(
            session.UserId, claimedName, session.SessionId, claimedAvatar);

        await using var scope = consoleScope;

        var me = await console.GetMe(ct);

        Assert.That(stored, Is.Not.Null);
        Assert.That(stored!.DisplayName, Is.Not.EqualTo(claimedName),
            "the seed is only meaningful if the stored name differs from the claimed one");

        Assert.Multiple(() =>
        {
            Assert.That(me.displayName, Is.EqualTo(claimedName));
            Assert.That(me.avatarFileId, Is.EqualTo(claimedAvatar));
            Assert.That(me.userId, Is.EqualTo(session.UserId));
        });
    }

    // ── Console: error mapping ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every refusal the deletion grain can produce arrives at the console as its own error.
    /// </summary>
    /// <remarks>
    /// <para>Five distinct refusals, five distinct things the console has to say — "that password is
    /// wrong", "your account is locked", "cancel your subscription first", "hand over your spaces
    /// first", "you already asked". Any of them collapsing into <c>InternalError</c> leaves the user
    /// with "try again later" and no way to make progress, and the mapping is a bare <c>switch</c>
    /// with a catch-all, so a renamed or added grain error does exactly that, silently.</para>
    ///
    /// <para>One account per branch, because the guards are order-dependent — the grain checks
    /// scheduling, then the password, then lockdown, then the subscription, then ownership — and a
    /// single account carrying two of them would only ever prove the first.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task RequestDeleteAccount_MapsEveryGrainRefusalToItsOwnConsoleError(CancellationToken ct = default)
    {
        // Console() writes an AsyncLocal, so the set and the call have to share one async frame —
        // see AccountConsoleHarness. Hence a local function rather than a field.
        async Task<DeleteAccountResult> AskAsync(TestUserSession user, string password)
        {
            var (consoleScope, console) = AccountConsoleHarness.Console(user);
            await using var scope = consoleScope;

            return await console.RequestDeleteAccount(password, ct);
        }

        var wrongPassword = await CreateSessionAsync(ct);
        var locked        = await CreateSessionAsync(ct);
        var subscriber    = await CreateSessionAsync(ct);
        var owner         = await CreateSessionAsync(ct);
        var repeater      = await CreateSessionAsync(ct);

        await AccountSeed.LockAsync(locked.UserId, ct: ct);
        await AccountSeed.SetUltimaAsync(subscriber.UserId, true, ct);

        var space = await owner.Users.CreateSpace(
            new CreateServerRequest("Console guard space", "owned by the caller", string.Empty), ct);

        Assert.That(space, Is.InstanceOf<SuccessCreateSpace>(),
            $"the ownership branch needs an owned space: {(space as FailedCreateSpace)?.error}");

        var first = await AskAsync(repeater, repeater.Credentials.password);
        Assert.That(first.success, Is.True, first.error.ToString());

        var refusals = new[]
        {
            (Branch: "wrong password", Result: await AskAsync(wrongPassword, "definitely-not-the-password"),
                Expected: DeleteAccountError.InvalidPassword),
            (Branch: "account under lockdown", Result: await AskAsync(locked, locked.Credentials.password),
                Expected: DeleteAccountError.AccountLocked),
            (Branch: "active subscription", Result: await AskAsync(subscriber, subscriber.Credentials.password),
                Expected: DeleteAccountError.HasActiveSubscription),
            (Branch: "owns a space", Result: await AskAsync(owner, owner.Credentials.password),
                Expected: DeleteAccountError.OwnsSpaces),
            (Branch: "already scheduled", Result: await AskAsync(repeater, repeater.Credentials.password),
                Expected: DeleteAccountError.AlreadyScheduled)
        };

        Assert.Multiple(() =>
        {
            foreach (var (branch, result, expected) in refusals)
            {
                Assert.That(result.success, Is.False, $"{branch}: the request was accepted");
                Assert.That(result.error, Is.EqualTo(expected),
                    $"{branch}: the console cannot tell the user what to do about a {result.error}");
            }
        });
    }

    /// <summary>
    /// Asking a second time says when the first deletion will run.
    /// </summary>
    /// <remarks>
    /// <para>The console's whole reason for asking is to render a banner, and the banner needs a
    /// date. A person who opens the console on a second device, or after clearing their session, hits
    /// exactly this path: the request is refused as already scheduled, and the answer has to carry
    /// the deadline it is refusing on behalf of — otherwise the only way to learn it is
    /// <c>GetMe</c>, which the client does not call again on a failed request.</para>
    ///
    /// <para>The grain populates it: <c>RequestDeletionAsync</c> returns
    /// <c>ScheduledDeletionAt</c> on the <c>AlreadyScheduled</c> branch specifically so this can be
    /// answered. What happens to it between there and here is what this test is about.</para>
    ///
    /// <para><b>Observed.</b> <c>AccountConsoleService.RequestDeleteAccount</c>
    /// (<c>src/Argon.Api/Features/AccountConsole/AccountConsoleService.cs</c>) maps every failure
    /// through one expression that returns <c>new DeleteAccountResult(false, error, null, null)</c>:
    /// both timestamps are hard-coded null on the refusal path, so the
    /// <c>ScheduledDeletionAt</c> the grain deliberately fills in for <c>AlreadyScheduled</c>
    /// (<c>AccountDeletionGrain.RequestDeletionAsync</c>, the first guard) is discarded before the
    /// console ever sees it. Observed: the grain reported an execution date, the console answered
    /// <c>executionAt = null</c>. The console cannot render "already scheduled, for this date" at
    /// all; the fix is to carry <c>result.ScheduledDeletionAt</c> through the failure branch.</para>
    ///
    /// <para><b>Adjudicated a design question, not an agreed defect (campaign verdict
    /// <c>CON-1</c>).</b> The review reproduced the mechanism above and then declined to call it a
    /// bug: the mapping does discard the date, but the reviewer found the refusal branch effectively
    /// unreachable — the console gates the delete action on the <c>MeDetails</c> status it fetches at
    /// page load — so populating it is a three-sided change (grain, mapping, a client that refetches)
    /// nobody benefits from today.
    /// The test stays red and keeps <c>[Category("KnownPresenceBug")]</c> so the default run
    /// excludes it: it pins a decision the product still owes, and it goes green the day that
    /// decision is made and implemented.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    [Category("KnownPresenceBug")]
    public async Task RequestDeleteAccount_WhenAlreadyScheduled_StillSaysWhenTheDeletionWillRun(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var first = await console.RequestDeleteAccount(session.Credentials.password, ct);
        Assert.That(first.success, Is.True, first.error.ToString());

        var status = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId).GetDeletionStatusAsync();
        var second = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.Multiple(() =>
        {
            Assert.That(second.success, Is.False);
            Assert.That(second.error, Is.EqualTo(DeleteAccountError.AlreadyScheduled));
            Assert.That(second.executionAt, Is.EqualTo(status.ExecutionAt),
                "the grain hands back the pending execution date on this branch; the console has to pass it on");
        });
    }

    /// <summary>
    /// Every refusal <c>CancelDeleteAccount</c> can meet arrives as its own error too.
    /// </summary>
    /// <remarks>
    /// The two ends of the same account's life: cancelling before anything was asked for, and
    /// cancelling after the erasure has already run. They are different sentences — "there is nothing
    /// to cancel" and "it is too late" — and only the second one is a state the user cannot get out
    /// of, so a mapping that returns the same value for both is a mapping that cannot say so. The
    /// executed deletion in the middle is what makes the second assertion honest: <c>Completed</c>
    /// reached by driving the real poll past the real grace, not by writing a status.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task CancelDeleteAccount_MapsNothingScheduledAndTooLateToDifferentErrors(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var nothingScheduled = await console.CancelDeleteAccount(ct);

        Assert.Multiple(() =>
        {
            Assert.That(nothingScheduled.success, Is.False);
            Assert.That(nothingScheduled.error, Is.EqualTo(CancelDeleteError.NotScheduled));
        });

        var requested = await console.RequestDeleteAccount(session.Credentials.password, ct);
        Assert.That(requested.success, Is.True, requested.error.ToString());

        // The grace elapsing is a deadline, not a state change; nothing to poll for until it passes.
        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, ct: ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the deletion did not finish within {AccountTimings.ExecutionBudget}; status was {reached}");

        var tooLate = await console.CancelDeleteAccount(ct);

        Assert.Multiple(() =>
        {
            Assert.That(tooLate.success, Is.False, "an account that has already been erased cannot be un-erased");
            Assert.That(tooLate.error, Is.EqualTo(CancelDeleteError.AlreadyCompleted),
                "and the console has to be able to say which of the two refusals this is");
        });
    }

    /// <summary>
    /// The console's export button reports the three answers it has to distinguish, and
    /// <c>gdrpExportInProgress</c> tracks the job it started.
    /// </summary>
    /// <remarks>
    /// <para><c>Ok</c>, <c>Already</c> and <c>RateLimit</c> are three different things to tell a
    /// person — "we are building it", "we already are", "you asked recently, wait" — and the third
    /// one is the only one with a fixed cost attached, because the window is thirty days in
    /// production. A mapping that turned any of them into <c>Unknown</c> would leave the console
    /// showing a generic failure for a request that succeeded twenty seconds ago.</para>
    ///
    /// <para>Driven through a real export rather than a stubbed status: the rate limit is measured
    /// from a completion that actually happened, and the flag is asserted on both sides of it. The
    /// flag is the only thing on <c>MeDetails</c> that says an export exists at all — there is no
    /// export id, status or link anywhere on the console's surface — so if it does not flip back the
    /// button stays dead for the life of the page.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task RequestExportGDRP_ReportsOkThenAlreadyThenRateLimit(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var grain   = GetGrainFactory().GetGrain<IUserDataExportGrain>(session.UserId);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var accepted = await console.RequestExportGDRP(ct);
        var duplicate = await console.RequestExportGDRP(ct);
        var during   = await console.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.EqualTo(RequestExportGDRPStatus.Ok));
            Assert.That(duplicate, Is.EqualTo(RequestExportGDRPStatus.Already),
                "a second press while the first archive is being built is not a failure, and not a second export");
            Assert.That(during.gdrpExportInProgress, Is.True,
                "the console has nothing else to render the running job with");
        });

        var finished = await Poll.ForValueAsync(
            async () => (await grain.GetExportStatusAsync()).Status,
            reported => reported is ExportStatusKind.Completed or ExportStatusKind.Failed,
            AccountTimings.ExportBudget,
            AccountTimings.ExportTick / 4,
            ct);

        Assert.That(finished, Is.EqualTo(ExportStatusKind.Completed),
            $"the export ended in {finished}: {(await grain.GetExportStatusAsync()).FailureReason}");

        var after       = await console.GetMe(ct);
        var rateLimited = await console.RequestExportGDRP(ct);

        Assert.Multiple(() =>
        {
            Assert.That(after.gdrpExportInProgress, Is.False,
                "the flag has to clear when the job it describes has finished, or the button never comes back");
            Assert.That(rateLimited, Is.EqualTo(RequestExportGDRPStatus.RateLimit),
                $"a second export inside the {AccountTimings.ExportRateLimit} window is refused as rate-limited, " +
                "which is the one refusal the console has to explain rather than retry");
        });
    }

    // ── Console: whose account is it ────────────────────────────────────────────────────────────

    /// <summary>
    /// A console call reaches exactly the account its request context names, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>No method on <c>IAccountConsole</c> takes a user id — every one keys its grain off
    /// <c>this.GetUserId()</c> — so "a console for A acting on B" is not an input a caller can
    /// supply. What this test pins is the property that makes that guarantee real rather than
    /// incidental: the identity lives entirely in the ambient request context, so a console instance
    /// resolved while A was the caller answers about B the moment the context says B, and a
    /// scheduled deletion on A leaves B's grain untouched.</para>
    ///
    /// <para>That is the shape worth guarding. A console that had cached its caller at resolution
    /// time would be the dangerous one: it would keep acting as a stale identity across a reused
    /// scope, and every assertion about "the right user" would still pass on a single-user test.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_console_call_reaches_only_the_account_its_context_names(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var (aliceScope, aliceConsole) = AccountConsoleHarness.Console(alice);
        await using var scopeA = aliceScope;

        var asAlice = await aliceConsole.GetMe(ct);
        var scheduled = await aliceConsole.RequestDeleteAccount(alice.Credentials.password, ct);

        Assert.That(scheduled.success, Is.True, scheduled.error.ToString());

        var (bobScope, bobConsole) = AccountConsoleHarness.Console(bob);
        await using var scopeB = bobScope;

        var asBob = await bobConsole.GetMe(ct);

        // Alice's own console object, called while the context says Bob. It has to answer about Bob:
        // if it answered about Alice, the identity would be something the instance carries, and a
        // reused scope would be a cross-account leak.
        var aliceInstanceUnderBob = await aliceConsole.GetMe(ct);

        var bobDeletion = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(bob.UserId).GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(asAlice.userId, Is.EqualTo(alice.UserId));
            Assert.That(asBob.userId, Is.EqualTo(bob.UserId));
            Assert.That(asBob.deletionStatus, Is.EqualTo(DeletionStatusKind.None),
                "Alice scheduling her own deletion must not appear on Bob's console");
            Assert.That(bobDeletion.Status, Is.EqualTo(AccountDeletionStatusKind.None),
                "and must not have reached Bob's deletion grain either");
            Assert.That(aliceInstanceUnderBob.userId, Is.EqualTo(bob.UserId),
                "the console holds no identity of its own — the request context is the only lever there is");
            Assert.That(aliceInstanceUnderBob.deletionStatus, Is.EqualTo(DeletionStatusKind.None));
        });

        // And back the other way, to show the switch is not one-directional: Bob's instance, Alice's
        // context, Alice's pending deletion.
        var (backScope, backConsole) = AccountConsoleHarness.Console(alice);
        await using var scopeBack = backScope;

        var backToAlice = await bobConsole.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(backToAlice.userId, Is.EqualTo(alice.UserId));
            Assert.That(backToAlice.deletionStatus, Is.EqualTo(DeletionStatusKind.Scheduled));
        });

        var cleanup = await backConsole.CancelDeleteAccount(ct);
        Assert.That(cleanup.success, Is.True, cleanup.error.ToString());
    }

    /// <summary>
    /// A console context naming a subject that owns no account cannot start a data export for it.
    /// </summary>
    /// <remarks>
    /// <para><c>AccountConsoleAuthInterceptor</c> validates the token's issuer, signature, lifetime
    /// and audience and then trusts the <c>sub</c> claim verbatim — it never asks whether that id is
    /// an account. Everything downstream inherits that: the console grabs a grain keyed on the id and
    /// starts working. An export is the sharp end of it, because it is the one console action with an
    /// external side effect — a job, a timer, objects written into the export bucket — for an account
    /// that does not exist and can never collect them.</para>
    ///
    /// <para>Whatever the console answers here, "we started building your archive" is not it. The job
    /// is cancelled before the assertions so a red test does not leave a stray export running against
    /// the shared host.</para>
    ///
    /// <para><b>Known defect.</b> Nothing on this path checks that the subject exists.
    /// <c>AccountConsoleAuthInterceptor.InvokeAsync</c>
    /// (<c>src/Argon.Core/Services/Ion/AccountConsoleAuthInterceptor.cs</c>) parses <c>sub</c> into a
    /// <c>Guid</c> and publishes it as the request context without a database read;
    /// <c>AccountConsoleService.RequestExportGDRP</c> keys
    /// <c>IUserDataExportGrain</c> on it; and <c>UserDataExportGrain.RequestExportAsync</c>
    /// (<c>src/Argon.Api/Grains/UserDataExportGrain.cs</c>) has no user lookup either — it writes
    /// state, arms the process timer and registers with the export pump. Observed: <c>Ok</c>, and
    /// <c>IsExportInProgressAsync</c> true, for a <c>Guid.NewGuid()</c> that owns nothing. The
    /// collection then runs to <c>Completed</c> against a null user
    /// (<c>CollectProfileAsync</c> returns silently) and uploads an archive of empty arrays with no
    /// <c>profile.json</c> and no e-mail, since both <c>SendExport*EmailAsync</c> bail on the missing
    /// row. A subject that owns no account should be refused before any of that.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    [Category("KnownPresenceBug")]
    public async Task The_console_does_not_start_an_export_for_a_subject_that_is_not_an_account(CancellationToken ct = default)
    {
        var ghost = Guid.NewGuid();
        var grain = GetGrainFactory().GetGrain<IUserDataExportGrain>(ghost);

        var (consoleScope, console) = AccountConsoleHarness.Console(ghost, "Nobody At All");
        await using var scope = consoleScope;

        var answer     = await console.RequestExportGDRP(ct);
        var inProgress = await grain.IsExportInProgressAsync();

        // Before the assertions, so a failure does not leave a job running on the shared host.
        await grain.CancelExportAsync();

        Assert.Multiple(() =>
        {
            Assert.That(answer, Is.Not.EqualTo(RequestExportGDRPStatus.Ok),
                "the console accepted a GDPR export request for an id that owns no account");
            Assert.That(inProgress, Is.False,
                "and started the job: a timer, an export id and archive objects for a user that does not exist");
        });
    }

    /// <summary>
    /// The console's services do not answer on the first-party client port — with or without an
    /// ordinary Argon session token.
    /// </summary>
    /// <remarks>
    /// <para>The one thing <see cref="AccountConsoleHarness"/> structurally cannot test is
    /// authentication: it sets the request context by hand, which is precisely the interceptor's
    /// output, so every fixture built on it assumes a token was checked. This reaches for the
    /// transport instead — a raw Ion unary POST at the route <c>MapRpcEndpoints</c> publishes — and
    /// asks the question that survives without an OIDC provider to authenticate against.</para>
    ///
    /// <para>That question is port isolation, and it is worth more than it looks.
    /// <c>AccountConsoleFeature</c> registers <c>IAccountConsole</c> and its
    /// <c>AccountConsoleAuthInterceptor</c> against <c>AccountConsoleOptions.Port</c> (8930) while
    /// <c>IonProtocolFeature</c> registers <c>ArgonTransactionInterceptor</c> with no port at all —
    /// so the console's guard is port-scoped and the first-party one is global. If the console's
    /// services ever became reachable on the client port, an ordinary session token would satisfy
    /// the only interceptor on that path and any signed-in user would be talking to the console with
    /// their own Argon credentials, never having gone near Aegis. The 8930 binding is the whole
    /// separation, which makes "not served here" a security assertion rather than a routing detail —
    /// and the reason the feature's own comment says the interceptor must never see a call meant for
    /// the first-party surface.</para>
    ///
    /// <para>Both callers are tried, anonymous and authenticated, because only the second one closes
    /// the hole described above. A control probe at an interface name that does not exist shows what
    /// the transport says about something it has never heard of, so the console's answer can be read
    /// against it; both are written to the test output. What the observed refusal is does not matter
    /// — 404, <c>INTERFACE_NOT_FOUND</c>, <c>NO_AUTH</c> are all refusals — only that the call was
    /// not answered.</para>
    ///
    /// <para>Note for whoever reads a green here: this does <em>not</em> prove the interceptor
    /// refuses an anonymous caller on its own port. The integration host binds no extra Ion ports at
    /// all (the in-memory server has no Kestrel), so that path has no coverage in this suite.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_console_does_not_answer_on_the_first_party_client_port(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var anonymous     = await PostIonAsync("IAccountConsole", "GetMe", null, ct);
        var authenticated = await PostIonAsync("IAccountConsole", "GetMe", session.Token, ct);
        var control       = await PostIonAsync("INoSuchConsoleAtAll", "GetMe", null, ct);

        await TestContext.Out.WriteLineAsync($"anonymous     -> {anonymous}");
        await TestContext.Out.WriteLineAsync($"authenticated -> {authenticated}");
        await TestContext.Out.WriteLineAsync($"control       -> {control}");

        Assert.Multiple(() =>
        {
            Assert.That(anonymous.WasAnswered, Is.False,
                $"an unauthenticated console call was answered on the client port: {anonymous}");
            Assert.That(authenticated.WasAnswered, Is.False,
                "an ordinary Argon session token reached the account console on the first-party port, where the "
              + $"only interceptor is the first-party one: {authenticated}");
        });
    }

    // ── Auto-deletion ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The inactivity scan schedules exactly the accounts whose arithmetic says they are inactive.
    /// </summary>
    /// <remarks>
    /// <para>Four branches of one calculation, one account each: the twelve-month default reached; a
    /// recent login rescuing an account created two years ago; a shorter threshold the account chose
    /// for itself; and an Ultima subscription, which takes the account out of the candidate query
    /// entirely. The failure worth catching here is never "nothing happened" — it is one account too
    /// many, and the only way to see that is to seed the near misses alongside the hits.</para>
    ///
    /// <para>The notice mail is asserted with the same shape, positives and negatives together. It is
    /// the only warning an inactive person gets before their account is erased without them ever
    /// asking, so "it was sent" and "it was sent to nobody else" are equally load-bearing.</para>
    ///
    /// <para>The recent-login account deliberately has an ancient <c>CreatedAt</c>: the scan falls
    /// back to the creation date only when there is no login history at all, and an account that has
    /// been signing in for two years must never be read as two years idle.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task RunScan_SchedulesExactlyTheAccountsThatAreActuallyInactive(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var idle       = await CreateSessionAsync(ct);   // last login 13 months ago, default threshold
        var active     = await CreateSessionAsync(ct);   // created 2 years ago, signed in yesterday
        var impatient  = await CreateSessionAsync(ct);   // asked for 3 months, idle for 4
        var subscriber = await CreateSessionAsync(ct);   // idle for 14 months, paying

        await AccountSeed.BackdateLastLoginAsync(idle.UserId, now - TimeSpan.FromDays(400), ct: ct);

        await AccountSeed.BackdateCreatedAtAsync(active.UserId, now - TimeSpan.FromDays(730), ct);
        await AccountSeed.BackdateLastLoginAsync(active.UserId, now - TimeSpan.FromDays(1), ct: ct);

        await AccountSeed.SetAutoDeleteAsync(impatient.UserId, 3, enabled: true, ct);
        await AccountSeed.BackdateLastLoginAsync(impatient.UserId, now - TimeSpan.FromDays(130), ct: ct);

        await AccountSeed.SetUltimaAsync(subscriber.UserId, true, ct);
        await AccountSeed.BackdateLastLoginAsync(subscriber.UserId, now - TimeSpan.FromDays(430), ct: ct);

        await GetGrainFactory()
           .GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId)
           .RunScanAsync();

        var statuses = new Dictionary<string, AccountDeletionStatusKind>
        {
            ["idle (13 months, 12-month default)"] = await StatusOfAsync(idle.UserId),
            ["active (signed in yesterday)"]       = await StatusOfAsync(active.UserId),
            ["impatient (4 months, asked for 3)"]  = await StatusOfAsync(impatient.UserId),
            ["subscriber (14 months, Ultima)"]     = await StatusOfAsync(subscriber.UserId)
        };

        // The notice is sent one-way from inside the scan, so give the two expected ones a moment to
        // land before asserting that the other two never arrive.
        await AccountTimings.Emails.WaitForAsync(idle.Credentials.email, EmailKinds.DeleteNotice, AccountTimings.Slack, ct);
        await AccountTimings.Emails.WaitForAsync(impatient.Credentials.email, EmailKinds.DeleteNotice, AccountTimings.Slack, ct);

        Assert.Multiple(() =>
        {
            Assert.That(statuses["idle (13 months, 12-month default)"], Is.EqualTo(AccountDeletionStatusKind.Scheduled),
                "an account idle past the default threshold is what the sweeper exists for");
            Assert.That(statuses["active (signed in yesterday)"], Is.EqualTo(AccountDeletionStatusKind.None),
                "the scan measures from the last login, not from the sign-up date");
            Assert.That(statuses["impatient (4 months, asked for 3)"], Is.EqualTo(AccountDeletionStatusKind.Scheduled),
                "an account that asked to be erased after three months of silence gets that");
            Assert.That(statuses["subscriber (14 months, Ultima)"], Is.EqualTo(AccountDeletionStatusKind.None),
                "a paid account is never swept, however quiet it has been");

            Assert.That(AccountTimings.Emails.Sent(idle.Credentials.email, EmailKinds.DeleteNotice),
                Has.Count.EqualTo(1), "the only warning before an unrequested erasure");
            Assert.That(AccountTimings.Emails.Sent(impatient.Credentials.email, EmailKinds.DeleteNotice),
                Has.Count.EqualTo(1));
            Assert.That(AccountTimings.Emails.Sent(active.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty, "an account that signed in yesterday must not be told it is about to be deleted");
            Assert.That(AccountTimings.Emails.Sent(subscriber.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty);
        });
    }

    /// <summary>
    /// An account that turned auto-deletion off is never swept.
    /// </summary>
    /// <remarks>
    /// <para>The setting is a switch with an <c>Enabled</c> column, and the only defensible reading of
    /// "off" is that the sweeper leaves the account alone — silence stops being evidence the moment
    /// the account holder says it is not. Every other reading makes the column meaningless: falling
    /// back to the shipped default turns "off" into "twelve months", which is what an account with no
    /// setting at all already gets, so the switch would have exactly one position.</para>
    ///
    /// <para>The row is seeded rather than written through <c>SetAutoDeletePeriod</c> because no
    /// product surface can produce it — the grain hard-codes <c>Enabled = true</c> on both the insert
    /// and the update, and refuses <c>null</c>. So this is reachable today only from a legacy row or
    /// an operator, which is the reason to pin the semantics now rather than after someone adds the
    /// off switch the column already promises. The stored period of thirty-six months is longer than
    /// the account has been idle, so the test is red under either intended reading — "never" or "use
    /// the stored number" — and green only under the one the code actually implements.</para>
    ///
    /// <para><b>Observed.</b> <c>AutoDeleteSchedulerGrain.ScanAndTriggerAsync</c>
    /// (<c>src/Argon.Api/Grains/AutoDeleteSchedulerGrain.cs</c>) projects the threshold as
    /// <c>AutoDeleteSettings.Where(s =&gt; s.UserId == u.Id &amp;&amp; s.Enabled).Select(s =&gt;
    /// s.Months).FirstOrDefault()</c>, which collapses "the switch is off" and "there is no setting"
    /// into the same absent value; the decision below it then reads <c>is &gt; 0 ? value :
    /// DefaultAutoDeleteMonths</c> and applies the twelve-month default. Observed: an account with
    /// <c>Enabled = false, Months = 36</c> and a last login 430 days old was scheduled by the scan
    /// and sent the <c>delete-notice</c> mail. Turning auto-deletion off makes it happen sooner than
    /// leaving it at thirty-six months would have. The projection has to carry <c>Enabled</c>
    /// alongside <c>Months</c> so the two states can be told apart at the point of decision.</para>
    ///
    /// <para><b>Adjudicated a design question, not an agreed defect (campaign verdict
    /// <c>CON-2</c>).</b> The review reproduced the mechanism above and then declined to call it a
    /// bug: the arithmetic is as described, but the reviewer found no surface — user, operator or admin —
    /// that can write <c>Enabled = false</c>, so the policy has to be decided before the projection:
    /// either drop the off switch the column and the clients picker promise, or grant it on both sides
    /// at once.
    /// The test stays red and keeps <c>[Category("KnownPresenceBug")]</c> so the default run
    /// excludes it: it pins a decision the product still owes, and it goes green the day that
    /// decision is made and implemented.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    [Category("KnownPresenceBug")]
    public async Task RunScan_NeverTouchesAnAccountThatTurnedAutoDeleteOff(CancellationToken ct = default)
    {
        var optedOut = await CreateSessionAsync(ct);

        await AccountSeed.SetAutoDeleteAsync(optedOut.UserId, 36, enabled: false, ct);
        await AccountSeed.BackdateLastLoginAsync(optedOut.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(430), ct: ct);

        await GetGrainFactory()
           .GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId)
           .RunScanAsync();

        var status = await StatusOfAsync(optedOut.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(AccountDeletionStatusKind.None),
                "an account that switched automatic deletion off was scheduled for automatic deletion");
            Assert.That(AccountTimings.Emails.Sent(optedOut.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty, "and was told so by e-mail");
        });
    }

    /// <summary>
    /// The sweeper applies the same bars the person themselves would have been held to.
    /// </summary>
    /// <remarks>
    /// <para>Two things the interactive path refuses outright: deleting an account that owns a space,
    /// because it would orphan the space and everyone in it, and deleting an account under lockdown,
    /// because an investigation is exactly when its data must not evaporate. Neither reason gets
    /// weaker when the request comes from a timer instead of a person — if anything the ownership
    /// one gets stronger, since nobody is present to be told "hand over your spaces first" and act
    /// on it.</para>
    ///
    /// <para>So the assertion is that the automatic entry point is no more permissive than the
    /// interactive one. It is the same grain and the same execution afterwards, so a guard that only
    /// the interactive path carries is not a policy, it is an accident of which caller you came
    /// from.</para>
    ///
    /// <para><b>Known defect.</b> <c>AccountDeletionGrain.RequestAutoDeleteAsync</c>
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs</c>) checks two things — already scheduled,
    /// and <c>HasActiveUltima</c> — where <c>RequestDeletionAsync</c> checks four, dropping both the
    /// <c>LockdownReason != NONE</c> bar and the <c>Spaces.Any(s =&gt; s.CreatorId == UserId
    /// &amp;&amp; !s.IsDeleted)</c> bar. <c>AutoDeleteSchedulerGrain.ScanAndTriggerAsync</c>
    /// (<c>src/Argon.Api/Grains/AutoDeleteSchedulerGrain.cs</c>) calls it unconditionally for every
    /// inactive candidate. Observed: both accounts came back <c>Scheduled</c>. The execution that
    /// follows soft-deletes memberships and anonymises the row while <c>SpaceEntity.CreatorId</c>
    /// still points at it, so a live space is left owned by "Deleted Account"; and an account under
    /// <c>UNDER_INVESTIGATION</c> is erased by a timer with nobody's approval.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    [Category("KnownPresenceBug")]
    public async Task RunScan_AppliesTheSameBarsAsAUserRequestedDeletion(CancellationToken ct = default)
    {
        var idleOwner  = await CreateSessionAsync(ct);
        var idleLocked = await CreateSessionAsync(ct);
        var longAgo    = DateTimeOffset.UtcNow - TimeSpan.FromDays(400);

        var space = await idleOwner.Users.CreateSpace(
            new CreateServerRequest("Sweeper guard space", "owned by an idle account", string.Empty), ct);

        Assert.That(space, Is.InstanceOf<SuccessCreateSpace>(),
            $"the ownership bar needs an owned space: {(space as FailedCreateSpace)?.error}");

        await AccountSeed.BackdateLastLoginAsync(idleOwner.UserId, longAgo, ct: ct);

        await AccountSeed.LockAsync(idleLocked.UserId, ct: ct);
        await AccountSeed.BackdateLastLoginAsync(idleLocked.UserId, longAgo, ct: ct);

        await GetGrainFactory()
           .GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId)
           .RunScanAsync();

        var ownerStatus  = await StatusOfAsync(idleOwner.UserId);
        var lockedStatus = await StatusOfAsync(idleLocked.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(ownerStatus, Is.EqualTo(AccountDeletionStatusKind.None),
                "the sweeper scheduled the erasure of an account that owns a space — the one thing a person "
              + "asking for the same deletion is refused outright");
            Assert.That(lockedStatus, Is.EqualTo(AccountDeletionStatusKind.None),
                "the sweeper scheduled the erasure of an account under investigation");
        });
    }

    /// <summary>
    /// Cancelling a sweep from the console keeps the account, and the next scan does not undo that.
    /// </summary>
    /// <remarks>
    /// <para>The cancel is a person answering the notice mail: they opened the console, saw the
    /// banner and said no. A scan that re-schedules them on its next pass makes that answer worthless
    /// — the account is erased anyway, a day later, and the only escape is to sign in to the desktop
    /// client, because that is the one action that writes the login row the scan reads. Which is to
    /// say: the console can warn you and take your answer, and cannot act on it.</para>
    ///
    /// <para>So the intended contract asserted here is that the cancellation is itself the sign of
    /// life the sweeper was looking for. The alternative implementation — recording the cancel as
    /// activity rather than special-casing it in the scan — satisfies the same assertion, which is
    /// why the assertion is written about the outcome and not about which of the two happened.</para>
    ///
    /// <para><b>Known defect.</b> Nothing records the cancellation anywhere the scan can see it.
    /// <c>AccountDeletionGrain.CancelDeletionAsync</c>
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs</c>) resets its own state to <c>None</c> and
    /// writes nothing else, while <c>AutoDeleteSchedulerGrain.ScanAndTriggerAsync</c>
    /// (<c>src/Argon.Api/Grains/AutoDeleteSchedulerGrain.cs</c>) decides purely from
    /// <c>max(DeviceHistories.LastLoginTime) ?? Users.CreatedAt</c> — neither of which a console
    /// action touches, since the console authenticates against Aegis and never goes through
    /// <c>UserGrain.UpdateUserDeviceHistory</c>. Observed: scan schedules, console cancel succeeds
    /// and reports <c>None</c>, the very next scan schedules the same account again. In production
    /// that is a fresh notice mail and a fresh thirty-day countdown every twenty-four hours until
    /// the person happens to sign in to the desktop client. Either the scan has to consult the
    /// deletion grain's cancellation, or the cancel has to record activity.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    [Category("KnownPresenceBug")]
    public async Task An_auto_delete_cancelled_from_the_console_is_not_reinstated_by_the_next_scan(CancellationToken ct = default)
    {
        var reprieved = await CreateSessionAsync(ct);
        var scheduler = GetGrainFactory().GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId);

        await AccountSeed.BackdateLastLoginAsync(reprieved.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ct: ct);

        await scheduler.RunScanAsync();

        Assert.That(await StatusOfAsync(reprieved.UserId), Is.EqualTo(AccountDeletionStatusKind.Scheduled),
            "the scan has to have scheduled the account for there to be anything to cancel");

        var (consoleScope, console) = AccountConsoleHarness.Console(reprieved);
        await using var scope = consoleScope;

        var cancelled = await console.CancelDeleteAccount(ct);

        Assert.That(cancelled.success, Is.True, cancelled.error.ToString());
        Assert.That(await StatusOfAsync(reprieved.UserId), Is.EqualTo(AccountDeletionStatusKind.None));

        await scheduler.RunScanAsync();

        Assert.That(await StatusOfAsync(reprieved.UserId), Is.EqualTo(AccountDeletionStatusKind.None),
            "the account holder answered the notice and said no; the next pass of the sweeper scheduled them again");
    }

    /// <summary>
    /// The auto-delete period an ordinary account may choose is one to thirty-six months, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>The bounds are the whole feature: the value decides how long an account has to be silent
    /// before it is erased without being asked again, so a zero or a negative that slipped through
    /// would mean "delete me now" and a value past the ceiling would mean "never" — neither of which
    /// the surface offers. The ends are asserted rather than the middle, and both directions of each
    /// end, because an off-by-one on an inclusive bound is the way this class of check fails.</para>
    ///
    /// <para><c>null</c> is refused too, and that is worth pinning precisely because the contract
    /// invites it: the Ion signature takes a nullable, and the entity's own comment says null means
    /// disabled. The grain rejects it — auto-deletion cannot be switched off from here — so what the
    /// nullable actually buys is a client that can send a value with no meaning.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task SetAutoDeletePeriod_AcceptsOneToThirtySixMonthsAndNothingElse(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var fresh = await session.Security.GetAutoDeletePeriod(ct);

        Assert.Multiple(() =>
        {
            Assert.That(fresh.months, Is.EqualTo(12), "the shipped default an account has before it chooses");
            Assert.That(fresh.enabled, Is.True);
        });

        var accepted = new[] { 1, 36 };
        var refused  = new int?[] { null, 0, -1, 37, 72 };

        foreach (var months in accepted)
        {
            var result = await session.Security.SetAutoDeletePeriod(months, ct);
            var stored = await session.Security.GetAutoDeletePeriod(ct);

            Assert.That(result, Is.InstanceOf<SuccessSetAutoDelete>(),
                $"{months} months is inside the range the privacy screen offers: " +
                $"{(result as FailedSetAutoDelete)?.error}");
            Assert.That(stored.months, Is.EqualTo(months), $"{months} months was accepted but not stored");
        }

        // Left at 36 by the loop above, so a refusal that silently wrote anyway is visible.
        foreach (var months in refused)
        {
            var result = await session.Security.SetAutoDeletePeriod(months, ct);
            var stored = await session.Security.GetAutoDeletePeriod(ct);

            Assert.Multiple(() =>
            {
                Assert.That(result, Is.InstanceOf<FailedSetAutoDelete>(),
                    $"{months?.ToString() ?? "null"} is outside the range an ordinary account may choose");
                Assert.That((result as FailedSetAutoDelete)?.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD),
                    $"{months?.ToString() ?? "null"}: the client can only correct the value if it is told the value is the problem");
                Assert.That(stored.months, Is.EqualTo(36),
                    $"{months?.ToString() ?? "null"} was refused but written anyway");
            });
        }
    }

    /// <summary>
    /// A subscriber may go to seventy-two months, and not one further.
    /// </summary>
    /// <remarks>
    /// The ceiling is the paid half of the feature, so it has to move with the subscription rather
    /// than with anything cached: the same account is asked before and after the flag is set, and the
    /// value that was refused a moment ago is accepted. The upper bound is asserted on the far side
    /// too — a subscription raises the ceiling, it does not remove it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task SetAutoDeletePeriod_LetsASubscriberGoToSeventyTwoMonths(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var beforeSubscription = await session.Security.SetAutoDeletePeriod(72, ct);

        Assert.Multiple(() =>
        {
            Assert.That(beforeSubscription, Is.InstanceOf<FailedSetAutoDelete>(),
                "seventy-two months is the subscriber ceiling, not the ordinary one");
            Assert.That((beforeSubscription as FailedSetAutoDelete)?.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
        });

        await AccountSeed.SetUltimaAsync(session.UserId, true, ct);

        var withSubscription = await session.Security.SetAutoDeletePeriod(72, ct);
        var stored           = await session.Security.GetAutoDeletePeriod(ct);
        var pastTheCeiling   = await session.Security.SetAutoDeletePeriod(73, ct);

        Assert.Multiple(() =>
        {
            Assert.That(withSubscription, Is.InstanceOf<SuccessSetAutoDelete>(),
                $"the ceiling has to follow the subscription: {(withSubscription as FailedSetAutoDelete)?.error}");
            Assert.That(stored.months, Is.EqualTo(72));
            Assert.That(stored.enabled, Is.True);
            Assert.That(pastTheCeiling, Is.InstanceOf<FailedSetAutoDelete>(),
                "a subscription raises the ceiling; it does not remove it");
        });
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<AccountDeletionStatusKind> StatusOfAsync(Guid userId)
        => (await GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId).GetDeletionStatusAsync()).Status;

    /// <summary>What one raw Ion unary POST answered, in the terms a refusal is written in.</summary>
    private sealed record IonProbe(HttpStatusCode Status, string? IonStatus, string Body)
    {
        /// <summary>
        /// Whether the call was served rather than refused.
        /// </summary>
        /// <remarks>
        /// The transport signals a refusal in one of two places — an HTTP status of its own, or an
        /// <c>X-Ion-Status</c> code on an otherwise ordinary response — so neither alone is enough
        /// to tell the two apart. A served unary is the remaining case: 200, no status code, and a
        /// payload, since every method here returns a message with fields in it.
        /// </remarks>
        public bool WasAnswered => Status == HttpStatusCode.OK
                                && string.IsNullOrEmpty(IonStatus)
                                && Body.Length > 0;

        public override string ToString() => $"HTTP {(int)Status} {Status}; X-Ion-Status: {IonStatus ?? "(none)"}; body: {Body}";
    }

    /// <summary>
    /// Posts an unauthenticated Ion unary call at the route <c>MapRpcEndpoints</c> publishes.
    /// </summary>
    /// <remarks>
    /// The body is the CBOR for an empty argument array, and the content type is the one the
    /// transport demands — without both, the call is refused for the wrong reason and says nothing
    /// about authentication. The response body is rendered printable rather than decoded: what is
    /// being read out of it is an error code, and a CBOR reader would throw on a payload that turned
    /// out not to be one.
    /// </remarks>
    private async Task<IonProbe> PostIonAsync(string interfaceName, string methodName, string? bearerToken, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/ion/{interfaceName}/{methodName}.unary")
        {
            Content = new ByteArrayContent([0x80])
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/ion");

        if (bearerToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        var response = await HttpClient.SendAsync(request, ct);
        var bytes    = await response.Content.ReadAsByteArrayAsync(ct);
        var ionStatus = response.Headers.TryGetValues("X-Ion-Status", out var values)
            ? values.FirstOrDefault()
            : null;

        var printable = new StringBuilder(bytes.Length);

        foreach (var b in bytes)
            printable.Append(b is >= 0x20 and < 0x7f ? (char)b : '.');

        return new IonProbe(response.StatusCode, ionStatus, printable.ToString());
    }
}
