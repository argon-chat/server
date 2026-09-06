namespace ArgonComplexTest.Tests;

using AccountContracts;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;

/// <summary>
/// Proof that the account-lifecycle harness observes what it claims to observe.
/// </summary>
/// <remarks>
/// <para>The deletion and export campaigns rest on four pieces of machinery that are themselves
/// untested code: compressed clocks reaching the grains through configuration, an e-mail sink that
/// sees a message the host will never actually send, an account console driven with a hand-made
/// request context, and a presigned archive fetched from a host name that does not resolve. Every one
/// of those can fail in a way that makes a fixture pass for the wrong reason — a grace that silently
/// stayed at thirty days turns "the account was not deleted yet" into a tautology, and a sink that
/// never receives anything turns "no reminder was sent twice" into the same.</para>
///
/// <para>So this fixture drives each of them end to end and asserts the thing that could not be true
/// if the harness were inert: an export that actually completes and whose archive actually contains
/// the account's e-mail address, a deletion that actually anonymises a row within seconds, and
/// reminders that actually arrive — twice, once each, not once and not three times. It is the
/// fixture that fails first when the harness breaks, so the campaign's own fixtures can be read as
/// statements about the product.</para>
/// </remarks>
[TestFixture]
public class AccountHarnessSmokeTests : TestBase
{
    /// <summary>
    /// An export requested the way the desktop client requests it runs to completion, the archive is
    /// downloadable, it carries the account's own data, and both mails were observed.
    /// </summary>
    /// <remarks>
    /// Four independent things at once, deliberately: the compressed <c>DataExport</c> tick (at the
    /// shipped thirty seconds a dozen-step export would outlast the case budget), the presigned url
    /// against the real object store, the zip's contents, and the sink. They are one test because
    /// they are one chain — an archive that cannot be downloaded makes the sink assertion moot, and a
    /// sink that saw nothing makes the archive assertion the only thing proved.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_export_completes_downloads_and_is_announced_by_email(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var email   = session.Credentials.email;

        var requested = await session.Security.RequestDataExport(ct);

        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the export was refused: {(requested as FailedRequestDataExport)?.error}");

        var started = (SuccessRequestDataExport)requested;

        var status = await AccountConsoleHarness.WaitForExportAsync(session, DataExportStatusKind.COMPLETED, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(status.status, Is.EqualTo(DataExportStatusKind.COMPLETED),
                $"the export stalled in {status.status} after {AccountTimings.ExportBudget} " +
                $"at {AccountTimings.ExportTick} per tick, having processed {status.itemsProcessed} items");
            Assert.That(status.exportId, Is.EqualTo(started.exportId), "the status must describe the job that was started");
            Assert.That(status.downloadUrl, Is.Not.Null.And.Not.Empty);
        });

        var entries = await ExportArchive.DownloadAsync(status.downloadUrl!, ct);

        Assert.That(entries.Keys, Does.Contain("profile.json"),
            $"the archive holds {string.Join(", ", entries.Keys.Order())}");

        // The identifying half of profile.json. Asserted by value rather than by shape because an
        // export that produced a well-formed file about the wrong account is the failure worth
        // catching, and it is one a schema check would pass.
        Assert.Multiple(() =>
        {
            Assert.That(entries["profile.json"], Does.Contain(email));
            Assert.That(entries["profile.json"], Does.Contain(session.Credentials.username));
        });

        var startedMail = await AccountTimings.Emails.WaitForAsync(email, EmailKinds.ExportStarted, AccountTimings.Slack, ct);
        var readyMail   = await AccountTimings.Emails.WaitForAsync(email, EmailKinds.ExportReady, AccountTimings.Slack, ct);

        Assert.Multiple(() =>
        {
            Assert.That(startedMail, Is.Not.Null, "no 'export started' mail reached the sink");
            Assert.That(readyMail, Is.Not.Null, "no 'export ready' mail reached the sink");
            Assert.That(startedMail?.At, Is.LessThanOrEqualTo(readyMail?.At),
                "the mail announcing the export must precede the one announcing the archive");
            Assert.That(readyMail?.Body, Does.Contain(status.downloadUrl!),
                "the ready mail is the only place a person is given the link, so it has to carry it");
        });
    }

    /// <summary>
    /// A deletion asked for on the account console waits out its grace and then actually erases the
    /// account, and the console can report every step of it.
    /// </summary>
    /// <remarks>
    /// <para>The console is driven the way <c>AdminConsoleTests</c> drives the admin one — the service
    /// resolved from a scope with the request context set by hand, because the Ion port in front of it
    /// wants an OIDC token no test host issues. See <see cref="AccountConsoleHarness"/> for why that
    /// is equivalent for everything except the interceptor itself.</para>
    ///
    /// <para>Both sides of the grace are asserted, and the first is the one that matters: a poll
    /// before the deadline leaving the account alone is what proves the grace exists at all. Without
    /// it, a harness that had silently set the grace to zero would still pass the second half.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_console_deletion_waits_out_the_grace_and_then_anonymises_the_account(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var email   = session.Credentials.email;
        var grace   = AccountTimings.Grace;

        var (consoleScope, console) = AccountConsoleHarness.Console(session);

        await using var scope = consoleScope;

        var requestedAt = DateTimeOffset.UtcNow;
        var requested   = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.Multiple(() =>
        {
            Assert.That(requested.success, Is.True, requested.error.ToString());
            Assert.That(requested.error, Is.EqualTo(DeleteAccountError.None));
            Assert.That(requested.executionAt, Is.Not.Null);
            Assert.That(requested.executionAt, Is.GreaterThanOrEqualTo(requestedAt + grace));
            Assert.That(requested.executionAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow + grace));
        });

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);

        // The poll body, run while the grace still has most of its life left. It is the same call the
        // grain's own timer makes, so this is the production path and not a test-only shortcut.
        await grain.CheckAndExecuteAsync();

        Assert.That((await console.GetMe(ct)).deletionStatus, Is.EqualTo(DeletionStatusKind.Scheduled),
            $"a poll {grace} before the deadline must leave the account scheduled, not delete it");

        // A fixed wait, and the one place in this fixture that is allowed one: what is being waited
        // for is a deadline passing, not a state change, and there is nothing to poll for until it has.
        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, ct: ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the deletion did not finish within {AccountTimings.ExecutionBudget} of its grace elapsing; " +
            $"status was {reached}, reason: {(await grain.GetDeletionStatusAsync()).FailureReason}");

        var row = await AccountSeed.ReadUserAsync(session.UserId, ct);

        Assert.That(row, Is.Not.Null, "the row is anonymised in place, never removed");
        Assert.Multiple(() =>
        {
            Assert.That(row!.IsDeleted, Is.True);
            Assert.That(row.Username, Does.StartWith("deleted_"), $"username is '{row.Username}'");
            Assert.That(row.Email, Does.EndWith("@void.local"), $"email is '{row.Email}'");
        });

        Assert.That((await console.GetMe(ct)).deletionStatus, Is.EqualTo(DeletionStatusKind.Completed),
            "the console has to be able to render what happened to the account it just deleted");

        var scheduledMail = await AccountTimings.Emails.WaitForAsync(email, EmailKinds.DeletionScheduled, AccountTimings.Slack, ct);
        var completedMail = await AccountTimings.Emails.WaitForAsync(email, EmailKinds.DeletionCompleted, AccountTimings.Slack, ct);

        Assert.Multiple(() =>
        {
            Assert.That(scheduledMail, Is.Not.Null, "no 'deletion scheduled' mail reached the sink");
            Assert.That(completedMail, Is.Not.Null, "no 'deletion completed' mail reached the sink");
            Assert.That(scheduledMail?.At, Is.LessThanOrEqualTo(completedMail?.At));
        });
    }

    /// <summary>
    /// Each reminder threshold produces exactly one mail, in order, and none of them repeats.
    /// </summary>
    /// <remarks>
    /// <para>The bookkeeping this pins is the grain's <c>RemindersSent</c> set, which is the only thing
    /// standing between a person and one warning mail per poll for the whole grace period — six hours
    /// apart in production, two seconds apart here. The count is the assertion: a threshold that fired
    /// twice and one that never fired at all are both defects, and both look identical to a test that
    /// only checks "a reminder arrived".</para>
    ///
    /// <para>On the compressed clocks the two mails are indistinguishable by content — the template
    /// renders whole days and both thresholds are under a second of a day — so what separates them is
    /// arrival: the first must be alone when it appears, which can only be true if it was sent on
    /// crossing the further threshold rather than on crossing both at once.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Each_reminder_threshold_fires_exactly_once(CancellationToken ct = default)
    {
        var thresholds = AccountTimings.Reminders;

        Assert.That(thresholds, Has.Count.GreaterThanOrEqualTo(2),
            "the host is configured with fewer than two reminder thresholds, so ordering is untestable");

        var session = await CreateSessionAsync(ct);
        var email   = session.Credentials.email;
        var grain   = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);

        var requested = await grain.RequestDeletionAsync(session.Credentials.password);
        Assert.That(requested.Success, Is.True, requested.Error?.ToString());

        // Drive the poll rather than wait for the grain's timer: the timer is running too, but which
        // of the two crosses a threshold first is a race, and the count below is the same either way.
        async Task<IReadOnlyList<SentEmail>> PollOnce()
        {
            await grain.CheckAndExecuteAsync();

            return AccountTimings.Emails.Sent(email, EmailKinds.DeletionReminder);
        }

        var afterFirst = await Poll.ForValueAsync(PollOnce, sent => sent.Count >= 1,
            AccountTimings.GraceAndABit, TimeSpan.FromMilliseconds(100), ct);

        Assert.That(afterFirst, Has.Count.EqualTo(1),
            $"the first reminder must arrive alone on crossing {thresholds[0]}, before {thresholds[1]} is due");

        var afterSecond = await Poll.ForValueAsync(PollOnce, sent => sent.Count >= 2,
            AccountTimings.GraceAndABit, TimeSpan.FromMilliseconds(100), ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterSecond, Has.Count.EqualTo(2),
                $"one reminder per threshold and no more; the host has {thresholds.Count}");
            Assert.That(afterSecond[1].At, Is.GreaterThanOrEqualTo(afterSecond[0].At));
        });

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, ct: ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the deletion did not finish; status was {reached}");

        // The polls above ran continuously from the request to the execution, so every opportunity to
        // send a duplicate has been taken. Anything beyond one per threshold now is a repeat.
        Assert.That(AccountTimings.Emails.Sent(email, EmailKinds.DeletionReminder),
            Has.Count.EqualTo(thresholds.Count),
            "a threshold was reminded about more than once across the whole grace period");
    }
}
