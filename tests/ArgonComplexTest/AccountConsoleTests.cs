namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AccountContracts;
using Argon.Core.Entities.Data;
using Argon.Features.Auth;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;

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

    // ── Auto-deletion: the operator queue ───────────────────────────────────────────────────────

    /// <summary>
    /// The inactivity scan proposes exactly the accounts whose arithmetic says they are inactive —
    /// and proposes them, rather than deleting them.
    /// </summary>
    /// <remarks>
    /// <para>Four branches of one calculation, one account each: the twelve-month default reached; a
    /// recent login rescuing an account created two years ago; a shorter threshold the account chose
    /// for itself; and an Ultima subscription, which takes the account out of the candidate query
    /// entirely. The failure worth catching here is never "nothing happened" — it is one account too
    /// many, and the only way to see that is to seed the near misses alongside the hits.</para>
    ///
    /// <para><b>The contract this now guards is the shape of the whole feature.</b> The scan used to
    /// call <c>RequestAutoDeleteAsync</c> on every hit, which armed a grace period and then erased the
    /// account — an irreversible operation applied by a timer with nobody in the loop, and the source
    /// of three defects in this fixture alone (CON-2, CON-3, CON-4). It now writes candidates into
    /// <c>IAccountDeletionQueueGrain</c> and an operator decides. So the assertions are in two halves:
    /// the right accounts are queued, and <em>no</em> account is scheduled or told anything by e-mail
    /// — because a proposal is not a deletion and must not read like one to the person it is about.
    /// The notice mail belongs to the approval now, and <c>AdminConsoleTests</c> asserts it there.</para>
    ///
    /// <para>The recent-login account deliberately has an ancient <c>CreatedAt</c>: the scan falls
    /// back to the creation date only when there is no login history at all, and an account that has
    /// been signing in for two years must never be read as two years idle.</para>
    ///
    /// <para>The fixture shares one queue with every other fixture in the run, so every assertion here
    /// is about the presence or absence of a named account, never about the length of the queue.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task RunScan_QueuesExactlyTheAccountsThatAreActuallyInactive(CancellationToken ct = default)
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

        await RunScanAsync();

        var queued = new Dictionary<string, QueuedAccountDeletion?>
        {
            ["idle (13 months, 12-month default)"] = await QueuedAsync(idle.UserId),
            ["active (signed in yesterday)"]       = await QueuedAsync(active.UserId),
            ["impatient (4 months, asked for 3)"]  = await QueuedAsync(impatient.UserId),
            ["subscriber (14 months, Ultima)"]     = await QueuedAsync(subscriber.UserId)
        };

        var idleStatus      = await StatusOfAsync(idle.UserId);
        var impatientStatus = await StatusOfAsync(impatient.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(queued["idle (13 months, 12-month default)"], Is.Not.Null,
                "an account idle past the default threshold is what the sweep exists to propose");
            Assert.That(queued["idle (13 months, 12-month default)"]?.ThresholdMonths, Is.EqualTo(12));
            Assert.That(queued["idle (13 months, 12-month default)"]?.Reason,
                Is.EqualTo(AccountDeletionQueueReasons.InactivityDefault),
                "an operator has to be able to tell the platform's threshold from one the account chose");

            Assert.That(queued["active (signed in yesterday)"], Is.Null,
                "the scan measures from the last login, not from the sign-up date");

            Assert.That(queued["impatient (4 months, asked for 3)"], Is.Not.Null,
                "an account that asked to be erased after three months of silence is proposed after three");
            Assert.That(queued["impatient (4 months, asked for 3)"]?.ThresholdMonths, Is.EqualTo(3));
            Assert.That(queued["impatient (4 months, asked for 3)"]?.Reason,
                Is.EqualTo(AccountDeletionQueueReasons.InactivityChosen));

            Assert.That(queued["subscriber (14 months, Ultima)"], Is.Null,
                "a paid account is never swept, however quiet it has been");

            // The other half: proposing is not deleting. Nothing is scheduled and nobody is written to
            // until a person approves it.
            Assert.That(idleStatus, Is.EqualTo(AccountDeletionStatusKind.None),
                "the scan scheduled a deletion; it is only allowed to propose one");
            Assert.That(impatientStatus, Is.EqualTo(AccountDeletionStatusKind.None));

            Assert.That(AccountTimings.Emails.Sent(idle.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty, "an account nobody has decided on yet must not be told it is being deleted");
            Assert.That(AccountTimings.Emails.Sent(impatient.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty);
            Assert.That(AccountTimings.Emails.Sent(active.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty);
            Assert.That(AccountTimings.Emails.Sent(subscriber.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty);
        });
    }

    /// <summary>
    /// An account that turned auto-deletion off is never proposed.
    /// </summary>
    /// <remarks>
    /// <para>The setting is a switch with an <c>Enabled</c> column, and the only defensible reading of
    /// "off" is that the sweep leaves the account alone — silence stops being evidence the moment the
    /// account holder says it is not. Every other reading makes the column meaningless: falling back to
    /// the shipped default turns "off" into "twelve months", which is what an account with no setting
    /// at all already gets, so the switch would have exactly one position.</para>
    ///
    /// <para><b>Contract, and how it was won (defect CON-2).</b> The off state was described in four
    /// places — the nullable <c>months</c> in the Ion signature, the <c>enabled</c> flag on
    /// <c>AutoDeletePeriod</c>, the entity's own "Null means disabled" comment, and the desktop
    /// client's "Disabled" menu item — and granted in none: <c>SecurityGrain.SetAutoDeletePeriodAsync</c>
    /// refused <c>null</c> with <c>INVALID_PERIOD</c>, so the menu item raised an error toast and
    /// snapped back. Meanwhile <c>AutoDeleteSchedulerGrain</c> projected the threshold as
    /// <c>Where(s =&gt; s.UserId == u.Id &amp;&amp; s.Enabled).Select(s =&gt; s.Months)</c>, which
    /// answers the same absent value for "switched off" as for "never chose", and then applied the
    /// twelve-month default to both. The two halves landed together, which is the only safe order:
    /// granting the switch without teaching the scan to read it would have made turning auto-delete
    /// <em>off</em> get the account proposed sooner than leaving it at thirty-six months.</para>
    ///
    /// <para>Driven through the real Ion call rather than a seeded row, precisely because that is what
    /// changed: the state is now reachable from the product, so the test that pins it should reach it
    /// the way a person does.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task RunScan_NeverTouchesAnAccountThatTurnedAutoDeleteOff(CancellationToken ct = default)
    {
        var optedOut = await CreateSessionAsync(ct);

        var turnedOff = await optedOut.Security.SetAutoDeletePeriod(null, ct);

        Assert.That(turnedOff, Is.InstanceOf<SuccessSetAutoDelete>(),
            $"the off switch the client already offers: {(turnedOff as FailedSetAutoDelete)?.error}");

        await AccountSeed.BackdateLastLoginAsync(optedOut.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(430), ct: ct);

        await RunScanAsync();

        var queued = await QueuedAsync(optedOut.UserId);
        var status = await StatusOfAsync(optedOut.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(queued, Is.Null,
                "an account that switched automatic deletion off was proposed for automatic deletion");
            Assert.That(status, Is.EqualTo(AccountDeletionStatusKind.None));
            Assert.That(AccountTimings.Emails.Sent(optedOut.Credentials.email, EmailKinds.DeleteNotice),
                Is.Empty, "and was told so by e-mail");
        });
    }

    /// <summary>
    /// The sweep applies the same bars the person themselves would have been held to.
    /// </summary>
    /// <remarks>
    /// <para>Two things the interactive path refuses outright: deleting an account that owns a space,
    /// because it would orphan the space and everyone in it, and deleting an account under lockdown,
    /// because an investigation is exactly when its data must not evaporate. Neither reason gets
    /// weaker when the request comes from a timer instead of a person — if anything the ownership one
    /// gets stronger, since nobody is present to be told "hand over your spaces first" and act on
    /// it.</para>
    ///
    /// <para><b>Contract (defect CON-3), now guarded in two places at once.</b>
    /// <c>AccountDeletionGrain.BarredAsync</c> is one list checked by both entry points, so an
    /// approval is refused by exactly what refuses a person; and the scan reads the same three facts
    /// as part of the query it already runs, so a candidate nobody could approve never reaches an
    /// operator's queue in the first place. The second half is not redundancy: a locked account cannot
    /// sign in, so it is <em>guaranteed</em> to pass the inactivity threshold and would otherwise sit
    /// in the queue for ever. Asserted on both — not queued, and not scheduled.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
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

        await RunScanAsync();

        var ownerQueued  = await QueuedAsync(idleOwner.UserId);
        var lockedQueued = await QueuedAsync(idleLocked.UserId);
        var ownerStatus  = await StatusOfAsync(idleOwner.UserId);
        var lockedStatus = await StatusOfAsync(idleLocked.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(ownerQueued, Is.Null,
                "the sweep proposed the erasure of an account that owns a space — the one thing a person "
              + "asking for the same deletion is refused outright");
            Assert.That(lockedQueued, Is.Null,
                "the sweep proposed the erasure of an account under investigation, which cannot sign in and "
              + "is therefore guaranteed to look inactive for ever");

            Assert.That(ownerStatus, Is.EqualTo(AccountDeletionStatusKind.None));
            Assert.That(lockedStatus, Is.EqualTo(AccountDeletionStatusKind.None));
        });
    }

    /// <summary>
    /// A queued account that signs in again is off the list by the next pass.
    /// </summary>
    /// <remarks>
    /// <para>The queue is a projection of the last sweep rather than a ledger of decisions still owed,
    /// and this is the property that makes that worth the trouble: an operator never acts on a
    /// proposal the world has since answered. Nothing watches logins to make it true — the account
    /// simply stops being a candidate, so the reconciliation that rebuilds the queue does not carry it
    /// forward.</para>
    ///
    /// <para>A login is written the way the scan reads one, through <c>DeviceHistories</c>: the
    /// console's own authentication goes through Aegis and writes no such row, which is the asymmetry
    /// that made a cancellation invisible to the scan (CON-4) and is worth keeping visible in the
    /// seeding here.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_queued_account_that_signs_in_again_leaves_the_queue(CancellationToken ct = default)
    {
        var returning = await CreateSessionAsync(ct);

        await AccountSeed.BackdateLastLoginAsync(returning.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ct: ct);

        await RunScanAsync();

        Assert.That(await QueuedAsync(returning.UserId), Is.Not.Null,
            "the account has to be in the queue for its departure to mean anything");

        await AccountSeed.BackdateLastLoginAsync(returning.UserId, DateTimeOffset.UtcNow, ct: ct);

        await RunScanAsync();

        var afterReturn = await QueuedAsync(returning.UserId);
        var status      = await StatusOfAsync(returning.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(afterReturn, Is.Null,
                "the account signed in again and is still queued for deletion");
            Assert.That(status, Is.EqualTo(AccountDeletionStatusKind.None));
        });
    }

    /// <summary>
    /// Cancelling an approved deletion from the console keeps the account, and no later pass undoes that.
    /// </summary>
    /// <remarks>
    /// <para>The cancel is a person answering the notice mail: they opened the console, saw the banner
    /// and said no. A sweep that proposes them again on its next pass makes that answer worthless —
    /// the account is erased anyway, a month later, and the only escape is to sign in to the desktop
    /// client, because that is the one action that writes the login row the scan reads. Which is to
    /// say: the console could warn you and take your answer, and could not act on it.</para>
    ///
    /// <para><b>Contract (defect CON-4).</b> The refusal is durable and lives in the grain that owns
    /// the decision: <c>CancelDeletionAsync</c> stamps <c>DeclinedAt</c>,
    /// <c>RequestAutoDeleteAsync</c> refuses while it stands, and the scan reads it off the deletion
    /// status before proposing anybody — so the account is neither re-queued nor re-scheduled. The
    /// alternative implementation (recording the cancellation as activity) satisfies the same
    /// assertions, which is why they are written about the outcome rather than about which of the two
    /// happened.</para>
    ///
    /// <para>The approval is driven through the queue grain rather than through the admin console
    /// because what is under test is the sweep's memory, not the operator surface;
    /// <c>AdminConsoleTests</c> covers the console's half.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task An_auto_delete_cancelled_from_the_console_is_not_reinstated_by_the_next_scan(CancellationToken ct = default)
    {
        var reprieved = await CreateSessionAsync(ct);

        await AccountSeed.BackdateLastLoginAsync(reprieved.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ct: ct);

        await RunScanAsync();

        Assert.That(await QueuedAsync(reprieved.UserId), Is.Not.Null,
            "the sweep has to have proposed the account for there to be anything to approve");

        var approved = await Queue.ApproveAsync(reprieved.UserId, SweepOperatorId, "operator@argon.test");

        Assert.That(approved.Success, Is.True, approved.Error?.ToString());
        Assert.That(await StatusOfAsync(reprieved.UserId), Is.EqualTo(AccountDeletionStatusKind.Scheduled),
            "an approval schedules the deletion the account holder is now being asked about");

        var (consoleScope, console) = AccountConsoleHarness.Console(reprieved);
        await using var scope = consoleScope;

        var cancelled = await console.CancelDeleteAccount(ct);

        Assert.That(cancelled.success, Is.True, cancelled.error.ToString());
        Assert.That(await StatusOfAsync(reprieved.UserId), Is.EqualTo(AccountDeletionStatusKind.None));

        await RunScanAsync();

        var statusAfterScan = await StatusOfAsync(reprieved.UserId);
        var queuedAfterScan = await QueuedAsync(reprieved.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(statusAfterScan, Is.EqualTo(AccountDeletionStatusKind.None),
                "the account holder answered the notice and said no; a later pass scheduled them again");
            Assert.That(queuedAfterScan, Is.Null,
                "and proposed them to an operator again, which is the same refusal ignored one step earlier");
        });
    }

    /// <summary>
    /// The auto-delete period an ordinary account may choose is one to thirty-six months, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>The bounds are the whole feature: the value decides how long an account has to be silent
    /// before it is proposed for erasure, so a zero or a negative that slipped through would mean
    /// "delete me now" and a value past the ceiling would mean "never" — neither of which the surface
    /// offers. The ends are asserted rather than the middle, and both directions of each end, because
    /// an off-by-one on an inclusive bound is the way this class of check fails.</para>
    ///
    /// <para><c>null</c> is deliberately not in the refused list: it is the off switch, and it has its
    /// own test. What is pinned here is that "off" is the <em>only</em> thing outside the range that
    /// the grain accepts.</para>
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
        var refused  = new[] { 0, -1, 37, 72 };

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
                    $"{months} is outside the range an ordinary account may choose");
                Assert.That((result as FailedSetAutoDelete)?.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD),
                    $"{months}: the client can only correct the value if it is told the value is the problem");
                Assert.That(stored.months, Is.EqualTo(36),
                    $"{months} was refused but written anyway");
                Assert.That(stored.enabled, Is.True,
                    $"{months} was refused and switched the feature off on the way past");
            });
        }
    }

    /// <summary>
    /// Sending no period at all switches automatic deletion off, and the sweep honours it.
    /// </summary>
    /// <remarks>
    /// <para>The contract the product described in four places and granted in none (defect CON-2): the
    /// Ion signature takes <c>months: i4?</c>, <c>AutoDeletePeriod</c> carries an <c>enabled</c> flag
    /// beside the number, <c>UserAutoDeleteSettingEntity.Months</c> says null means disabled, and the
    /// desktop client's privacy screen offers a "Disabled" item that sends exactly this. The grain
    /// answered <c>INVALID_PERIOD</c>, so the item raised an error toast and snapped back.</para>
    ///
    /// <para>Asserted as a round trip rather than as a write, because the interesting half is the read:
    /// a client renders "Disabled" off <c>enabled == false</c>, and a stored period left behind on a
    /// disabled row would render as a period the account is not actually subject to. Turning it back on
    /// is asserted too — an off switch that cannot be undone is a different feature.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task SetAutoDeletePeriod_WithNoPeriod_TurnsAutoDeleteOff(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var chose  = await session.Security.SetAutoDeletePeriod(24, ct);
        var stored = await session.Security.GetAutoDeletePeriod(ct);

        Assert.Multiple(() =>
        {
            Assert.That(chose, Is.InstanceOf<SuccessSetAutoDelete>(),
                $"{(chose as FailedSetAutoDelete)?.error}");
            Assert.That(stored.months, Is.EqualTo(24));
            Assert.That(stored.enabled, Is.True);
        });

        var turnedOff = await session.Security.SetAutoDeletePeriod(null, ct);
        var off       = await session.Security.GetAutoDeletePeriod(ct);

        Assert.Multiple(() =>
        {
            Assert.That(turnedOff, Is.InstanceOf<SuccessSetAutoDelete>(),
                $"no period means off, not a malformed period: {(turnedOff as FailedSetAutoDelete)?.error}");
            Assert.That(off.enabled, Is.False, "the switch has to have two positions to be a switch");
            Assert.That(off.months, Is.Null,
                "a period left on a disabled row is a number the account is not subject to");
        });

        var turnedBackOn = await session.Security.SetAutoDeletePeriod(6, ct);
        var on           = await session.Security.GetAutoDeletePeriod(ct);

        Assert.Multiple(() =>
        {
            Assert.That(turnedBackOn, Is.InstanceOf<SuccessSetAutoDelete>(),
                $"{(turnedBackOn as FailedSetAutoDelete)?.error}");
            Assert.That(on.months, Is.EqualTo(6));
            Assert.That(on.enabled, Is.True);
        });
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


    // ── Signing in ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Signing in calls off an approved inactivity deletion, and the account is told where the sign-in came from.
    /// </summary>
    /// <remarks>
    /// <para>The notice mail says it in so many words: "open the app and sign in before this date. This
    /// will cancel the deletion process". Until now nothing did — the countdown ran on, and the only
    /// escape was a button in the console the mail never mentioned. The sign-in is the cancel.</para>
    ///
    /// <para>The confirmation names the device and the address on purpose: a person who did <em>not</em>
    /// sign in is reading about somebody who has their password. And the next sweep must not propose
    /// the account again — that half is the CON-4 hold, asserted here because a cancel that is undone a
    /// day later is not a cancel.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Signing_in_calls_off_an_approved_inactivity_deletion_and_says_where_from(CancellationToken ct = default)
    {
        var returning = await CreateSessionAsync(ct);
        await AccountSeed.BackdateLastLoginAsync(returning.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ct: ct);
        await RunScanAsync();
        Assert.That(await QueuedAsync(returning.UserId), Is.Not.Null,
            "the account has to be proposed before an operator can approve it");

        var approved = await Queue.ApproveAsync(returning.UserId, SweepOperatorId, "operator@argon.test");
        Assert.That(approved.Success, Is.True, approved.Error?.ToString());
        Assert.That(await StatusOfAsync(returning.UserId), Is.EqualTo(AccountDeletionStatusKind.Scheduled),
            "the approval did not arm a countdown; nothing below is meaningful");

        await SignInFromAsync(returning,
            new DescribedDeviceInterceptor("platform=windows; os=Windows%2011; app=1.9.3; device=RETURNING-PC"), ct);

        var afterSignIn = await StatusOfAsync(returning.UserId);
        var mail = await AccountTimings.Emails.WaitForAsync(
            returning.Credentials.email, EmailKinds.DeletionCancelledBySignIn, AccountTimings.Slack, ct);

        await RunScanAsync();
        var reproposed = await QueuedAsync(returning.UserId);
        var consoleWording = AccountTimings.Emails.Sent(returning.Credentials.email, EmailKinds.DeletionCancelled);

        Assert.Multiple(() =>
        {
            Assert.That(afterSignIn, Is.EqualTo(AccountDeletionStatusKind.None),
                "the account signed in and its inactivity deletion is still counting down — the notice mail promised otherwise");
            Assert.That(mail, Is.Not.Null,
                "the deletion was called off and nobody was told why");
            Assert.That(mail?.Body, Does.Contain("RETURNING-PC").And.Contain("Windows 11"),
                "the confirmation does not name the device that signed in, so a person who did not sign in cannot tell");
            Assert.That(consoleWording, Is.Empty,
                "the account got the console's 'your request was cancelled' wording for a sign-in it may not have made");
            Assert.That(reproposed, Is.Null,
                "the next sweep proposed the account again; the sign-in bought it nothing");
        });
    }

    /// <summary>
    /// Signing in leaves a deletion the account asked for itself exactly where it was.
    /// </summary>
    /// <remarks>
    /// A person who requested their own erasure signs in to collect the export, to say goodbye, to
    /// check the date. None of that is a change of mind; the console's cancel is. Treating the sign-in
    /// as one would make the request impossible to keep for anybody who ever opens the app again.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Signing_in_leaves_a_deletion_the_account_asked_for_in_place(CancellationToken ct = default)
    {
        var leaving = await CreateSessionAsync(ct);
        var (consoleScope, console) = AccountConsoleHarness.Console(leaving);
        await using var scope = consoleScope;

        var requested = await console.RequestDeleteAccount(leaving.Credentials.password, ct);
        Assert.That(requested.success, Is.True, requested.error.ToString());

        await SignInFromAsync(leaving,
            new DescribedDeviceInterceptor("platform=macos; os=macOS%2015; app=1.9.3; device=LEAVING-MAC"), ct);

        // The cancel, had it happened, is awaited inside the sign-in; the mail is one-way, so give a
        // wrong one the moment it would need to land before saying it did not.
        var afterSignIn = await StatusOfAsync(leaving.UserId);
        await Task.Delay(AccountTimings.Slack / 4, ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterSignIn, Is.EqualTo(AccountDeletionStatusKind.Scheduled),
                "a sign-in withdrew a deletion the person asked for themselves");
            Assert.That(AccountTimings.Emails.Sent(leaving.Credentials.email, EmailKinds.DeletionCancelledBySignIn), Is.Empty,
                "the account was told its deletion was cancelled by a sign-in; it was not");
            Assert.That(AccountTimings.Emails.Sent(leaving.Credentials.email, EmailKinds.DeletionCancelled), Is.Empty,
                "the account was told its deletion was cancelled; it was not");
        });
    }

    /// <summary>
    /// A sign-in from a device the account has never been seen on is announced, once, and a known device is not.
    /// </summary>
    /// <remarks>
    /// <para>The mail is the only way a person learns that somebody else has their password before that
    /// somebody does anything with it, so it has to name the device, and it has to come from the first
    /// sign-in on that device rather than a later one.</para>
    ///
    /// <para>The very first device an account is ever seen on is deliberately silent: that is the device it
    /// registered from, and "new device" a second after the welcome mail is noise. This account signs in
    /// from three devices — the first is silent, the second is announced, the second again is silent.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_sign_in_from_a_device_never_seen_before_is_announced_once(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var to    = owner.Credentials.email;

        var first = new DescribedDeviceInterceptor("platform=windows; os=Windows%2011; app=1.9.3; device=HOME-PC");
        await SignInFromAsync(owner, first, ct);
        await Task.Delay(AccountTimings.Slack / 4, ct);
        var afterFirst = AccountTimings.Emails.Sent(to, EmailKinds.NewDeviceSignIn).Count;

        var second = new DescribedDeviceInterceptor("platform=android; os=Android%2015; app=1.9.3; device=Pixel%209");
        await SignInFromAsync(owner, second, ct);
        var announced = await AccountTimings.Emails.WaitForAsync(to, EmailKinds.NewDeviceSignIn, AccountTimings.Slack, ct);

        await SignInFromAsync(owner, second, ct);
        await SignInFromAsync(owner, first, ct);
        await Task.Delay(AccountTimings.Slack / 4, ct);
        var afterRepeats = AccountTimings.Emails.Sent(to, EmailKinds.NewDeviceSignIn).Count;

        Assert.Multiple(() =>
        {
            Assert.That(afterFirst, Is.Zero,
                "the account's very first device was announced as new — that is the welcome mail's job");
            Assert.That(announced, Is.Not.Null,
                "a sign-in from a device never seen before went unannounced");
            Assert.That(announced?.Body, Does.Contain("Pixel 9").And.Contain("Android 15"),
                "the announcement does not name the device, so the person cannot tell whether it was theirs");
            Assert.That(afterRepeats, Is.EqualTo(1),
                "a sign-in from a device already on record was announced as new");
        });
    }

    /// <summary>
    /// A password sign-in by this account from the given device, the way a first-party client does it:
    /// its own machine id, and a description of itself in the client header.
    /// </summary>
    private async Task SignInFromAsync(TestUserSession session, DescribedDeviceInterceptor device, CancellationToken ct)
    {
        var client = IonClient.Create(HttpClient, NoWebSockets);
        client.WithInterceptor(device);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Authorize(
            new UserCredentialsInput(session.Credentials.email, null, null, session.Credentials.password, null, null), ct);

        Assert.That(result, Is.InstanceOf<SuccessAuthorize>(),
            $"the sign-in this test needs was refused: {(result as FailedAuthorize)?.error}");
    }

    /// <summary>A sign-in needs no socket; refusing one loudly beats a hang if that ever changes.</summary>
    private static Task<System.Net.WebSockets.WebSocket> NoWebSockets(Uri uri, CancellationToken ct, string[]? protocols)
        => throw new InvalidOperationException("this client only signs in; it never opens a socket");

    /// <summary>A device with a machine id of its own and a first-party client's description of itself.</summary>
    private sealed class DescribedDeviceInterceptor(string clientHeader) : IIonInterceptor
    {
        private readonly Guid sessionId = Guid.CreateVersion7();

        public string MachineId { get; } = Guid.CreateVersion7().ToString();

        public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
        {
            context.RequestItems.Add("Sec-Ref", sessionId.ToString());
            context.RequestItems.Add("X-Ctt", sessionId.ToString());
            context.RequestItems.Add("Sec-Ner", "1");
            context.RequestItems.Add("Sec-Carry", MachineId);
            context.RequestItems.Add(ClientDescriptor.HeaderName, clientHeader);
            await next(context, ct);
        }
    }

    // ── Service accounts ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The sweep never proposes a bot's account, however long the bot has been silent.
    /// </summary>
    /// <remarks>
    /// <para>A bot never signs in, so its last activity is the day it was created and never moves: every
    /// bot on the platform crosses the inactivity threshold a year after it is made and stays across it
    /// for ever. The production sweep proposed the platform's own echo bot on its first pass.</para>
    ///
    /// <para>The bot is seeded the way the product makes one — a <c>Bots</c> row naming the account —
    /// and deliberately <em>without</em> <c>UserEntity.BotEntityId</c>, which is the column the obvious
    /// filter would have used and which only one bot account in twenty-three carries in production. A
    /// dormant person is proposed in the same pass, so this cannot pass by the scan having done
    /// nothing.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_sweep_never_proposes_a_bot_account(CancellationToken ct = default)
    {
        var bot    = await CreateSessionAsync(ct);
        var person = await CreateSessionAsync(ct);

        await SeedBotAsync(bot.UserId, ct);

        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(400);
        await AccountSeed.BackdateLastLoginAsync(bot.UserId, longAgo, ct: ct);
        await AccountSeed.BackdateLastLoginAsync(person.UserId, longAgo, ct: ct);

        await RunScanAsync();

        var queuedBot    = await QueuedAsync(bot.UserId);
        var queuedPerson = await QueuedAsync(person.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(queuedBot, Is.Null,
                "a bot's account was proposed for erasure; approving it would take the application's " +
                "identity, its membership of every space it serves and its messages with them");
            Assert.That(queuedPerson, Is.Not.Null,
                "the dormant person was not proposed either, so this pass proves nothing about bots");
        });
    }

    /// <summary>
    /// A bot's account is refused deletion whoever asks — the operator queue and the account's own console.
    /// </summary>
    /// <remarks>
    /// The scan filter keeps bots out of the operator's inbox; this is the bar behind it, at the one
    /// gate every caller passes. Both are wanted: a queue entry written before the filter existed, or a
    /// deletion asked for by any other route, must still be refused rather than merely unlisted.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_account_is_refused_deletion_whoever_asks(CancellationToken ct = default)
    {
        var bot = await CreateSessionAsync(ct);
        await SeedBotAsync(bot.UserId, ct);

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(bot.UserId);

        // The path an operator's approval takes.
        var approved = await grain.RequestAutoDeleteAsync();

        // And the path a person's own request takes, in case the account ever holds a session.
        var (consoleScope, console) = AccountConsoleHarness.Console(bot);
        await using var scope = consoleScope;
        var asked = await console.RequestDeleteAccount(bot.Credentials.password, ct);

        var status = await grain.GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(approved.Success, Is.False,
                "an operator's approval would have armed the erasure of a bot's account");
            Assert.That(approved.Error, Is.EqualTo(AccountDeletionRequestError.ServiceAccount),
                "the refusal has to say why, or the console renders it as a fault");
            Assert.That(asked.success, Is.False,
                "a bot's account scheduled its own erasure");
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.None),
                "something armed a countdown against a bot's account");
        });
    }

    /// <summary>
    /// The scan's reminder is armed even while auto-delete is switched off, and re-arming does not move it.
    /// </summary>
    /// <remarks>
    /// <para>Both halves are the production defect that left the sweep silent for a day after the switch
    /// was turned on. The reminder used to be registered only when the switch was on, which made arming
    /// the scan depend on which silo the singleton happened to activate on and what that silo's
    /// configuration said — and during a rolling restart that is routinely the outgoing pod, carrying
    /// the configuration the release replaced. Registering regardless, and reading the switch at each
    /// pass, is what makes the switch take effect without a restart.</para>
    ///
    /// <para>The second half: <c>RegisterOrUpdateReminder</c> resets the schedule, so re-registering on
    /// every activation pushed the next pass out — and because a tick activates the grain and the grain
    /// is collected between ticks, a daily scan ran every five minutes. This asserts the weaker,
    /// observable half of that: a second call does not move the schedule. Forcing a real deactivation
    /// would collect every other fixture's grains in the shared cluster, which is not worth the
    /// coverage.</para>
    ///
    /// <para>The reminder name is spelled out rather than read from the grain because it is durable
    /// state: renaming it silently abandons the reminder already registered against every live cluster.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_scan_reminder_is_armed_even_while_auto_delete_is_switched_off(CancellationToken ct = default)
    {
        Assert.That(AccountTimings.Deletion.AutoDeleteEnabled, Is.False,
            "premise: this host runs with the inactivity sweep switched off, and the fixtures drive it by hand");

        var scheduler = GetGrainFactory().GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId);
        await scheduler.EnsureSchedulerActiveAsync();

        var reminders = FactoryAsp.Services.GetRequiredService<IReminderTable>();
        var armed     = await reminders.ReadRow(scheduler.GetGrainId(), "auto-delete-scan");

        Assert.That(armed, Is.Not.Null,
            "the scan is not armed while the switch is off, so turning the switch on would do nothing " +
            "until a silo restart happened to activate this grain somewhere carrying the new setting");

        await scheduler.EnsureSchedulerActiveAsync();
        var again = await reminders.ReadRow(scheduler.GetGrainId(), "auto-delete-scan");

        Assert.Multiple(() =>
        {
            Assert.That(again.StartAt, Is.EqualTo(armed.StartAt),
                "arming the scan again moved its schedule; every activation would postpone the next pass");
            Assert.That(again.Period, Is.EqualTo(armed.Period));
        });
    }

    /// <summary>
    /// Makes an existing account a bot's account, the way the product does: a row in <c>Bots</c> naming it.
    /// </summary>
    /// <remarks>
    /// <c>UserEntity.BotEntityId</c> is deliberately left alone — see
    /// <see cref="The_sweep_never_proposes_a_bot_account"/> for why that is the point rather than an
    /// omission.
    /// </remarks>
    private static async Task SeedBotAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var teamId = Guid.NewGuid();

        db.TeamEntities.Add(new DevTeamEntity
        {
            TeamId  = teamId,
            OwnerId = userId,
            Name    = "Deletion fixture team"
        });

        db.BotEntities.Add(new BotEntity
        {
            AppId            = Guid.NewGuid(),
            TeamId           = teamId,
            Name             = "Deletion fixture bot",
            ClientId         = Guid.NewGuid().ToString("N"),
            ClientSecret     = Guid.NewGuid().ToString("N"),
            AppType          = DevAppType.Bot,
            BotToken         = Guid.NewGuid().ToString("N"),
            BotAsUserId      = userId,
            LifecycleState   = Argon.Core.Entities.Data.BotLifecycleState.Published,
            MaxSpaces        = 5,
            RequiredScopes   = [],
            AllowedRedirects = []
        });

        await db.SaveChangesAsync(ct);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The operator id an approval driven from this fixture is attributed to.
    /// </summary>
    /// <remarks>
    /// A bare id rather than a seeded operator row: the queue stores whoever the caller says decided, and
    /// checking that the caller is a real operator is the admin console's job, not the grain's — which is
    /// what <c>AdminConsoleTests</c> exercises. Distinct from that fixture's operator so an audit trail
    /// never confuses the two.
    /// </remarks>
    private static readonly Guid SweepOperatorId = Guid.Parse("00000000-0000-0000-0000-0000000ad0c2");

    private async Task<AccountDeletionStatusKind> StatusOfAsync(Guid userId)
        => (await GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId).GetDeletionStatusAsync()).Status;

    /// <summary>The one queue the inactivity sweep proposes into.</summary>
    private IAccountDeletionQueueGrain Queue
        => GetGrainFactory().GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId);

    /// <summary>Runs one pass of the inactivity sweep, synchronously from the test's point of view.</summary>
    private Task RunScanAsync()
        => GetGrainFactory()
          .GetGrain<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId)
          .RunScanAsync()
          .AsTask();

    /// <summary>The queue entry for one account, or <see langword="null"/> when nothing proposes it.</summary>
    /// <remarks>
    /// Pages through the whole queue rather than asking for one account, because the queue has no by-user
    /// lookup and deliberately should not: it is a worklist, and a per-account query would invite a console
    /// that renders "is this person queued?" on a profile, which is a different feature with different
    /// privacy consequences. The whole suite shares one queue — every fixture's dormant accounts land in
    /// it — so paging rather than reading the first page is what keeps this answer honest.
    /// </remarks>
    private async Task<QueuedAccountDeletion?> QueuedAsync(Guid userId)
    {
        const int page = 200;

        for (var offset = 0; ; offset += page)
        {
            var snapshot = await Queue.ListAsync(offset, page);

            if (snapshot.Entries.FirstOrDefault(entry => entry.UserId == userId) is { } found)
                return found;

            if (snapshot.Entries.Count < page || offset + snapshot.Entries.Count >= snapshot.TotalCount)
                return null;
        }
    }

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
