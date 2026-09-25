namespace ArgonComplexTest;

using Argon.Entities;
using Argon.Grains.Interfaces;
using Argon.Services;
using ArgonComplexTest.Infrastructure.Account;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The inactivity scan's own timer, and what a pass leaves behind when part of it cannot be read.
/// </summary>
/// <remarks>
/// <para><c>AccountConsoleTests</c> and <c>AdminConsoleTests</c> drive the scan through
/// <see cref="IAutoDeleteSchedulerGrain.RunScanAsync"/>, the operator's button. What they cannot reach
/// is the reminder — the daily pass that is the whole point of the grain in production — because the
/// integration host runs with <c>AccountDeletion:AutoDeleteEnabled</c> off and the first tick is five
/// minutes out. Here the tick is delivered by hand, through <see cref="IRemindable"/>, exactly as the
/// reminder service delivers it, and the switch is turned on for the length of one test by writing the
/// host's bound options — the same object the grain reads at each pass, which is what "the switch is
/// read at each pass, not at registration" promises.</para>
///
/// <para><b>Non-parallelizable, for three concrete reasons.</b> The switch is host-wide, and
/// <c>AccountConsoleTests</c> asserts it is off. Two tests plant a fault every pass trips over — a
/// settings row whose threshold overflows, and an account whose deletion state cannot be read — and a
/// neighbour's pass would trip over it too. And every pass reconciles the one cluster-wide queue.</para>
///
/// <para>Each test uses a scheduler activation of its own, keyed by a fresh id, so the singleton's
/// status — which the admin console reads — is not rewritten by a test, and so each test starts from
/// "armed, never run". The reminder each activation arms is removed again afterwards.</para>
/// </remarks>
[TestFixture, NonParallelizable]
public class AutoDeleteSchedulerTests : TestBase
{
    /// <summary>Durable state: the name the reminder is registered under in every live cluster.</summary>
    private const string ScanReminder = "auto-delete-scan";

    private static readonly TimeSpan ScanPeriod = TimeSpan.FromHours(24);

    private readonly List<IAutoDeleteSchedulerGrain> schedulers = [];

    [TearDown]
    public async Task DisarmTheSchedulersThisTestActivated()
    {
        var table = FactoryAsp.Services.GetRequiredService<IReminderTable>();

        foreach (var scheduler in schedulers)
        {
            if (await table.ReadRow(scheduler.GetGrainId(), ScanReminder) is { } row)
                await table.RemoveRow(scheduler.GetGrainId(), ScanReminder, row.ETag);
        }

        schedulers.Clear();
        AccountTimings.Deletion.AutoDeleteEnabled = false;
    }

    /// <summary>
    /// A tick for a reminder the scan does not own runs nothing, even with the switch on.
    /// </summary>
    /// <remarks>
    /// The switch is on so the assertion means something: with it off, a missing name check and a
    /// present one would both leave the status untouched.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_tick_for_a_reminder_the_scan_does_not_own_runs_nothing(CancellationToken ct = default)
    {
        var scheduler = await ArmedSchedulerAsync();
        var armed     = await scheduler.GetScanStatusAsync();

        AccountTimings.Deletion.AutoDeleteEnabled = true;

        await TickAsync(scheduler, "some-other-reminder");

        var after = await scheduler.GetScanStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(after.Runs, Is.EqualTo(armed.Runs));
            Assert.That(after.LastStartedAt, Is.Null, "a foreign reminder started a pass");
            Assert.That(after.NextDueAt, Is.EqualTo(armed.NextDueAt));
        });
    }

    /// <summary>
    /// With the switch off, a tick is skipped: no pass, and the schedule it reports is left alone.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task With_the_switch_off_a_tick_is_skipped(CancellationToken ct = default)
    {
        Assume.That(AccountTimings.Deletion.AutoDeleteEnabled, Is.False, "premise: the host runs with the switch off");

        var scheduler = await ArmedSchedulerAsync();
        var armed     = await scheduler.GetScanStatusAsync();

        await TickAsync(scheduler, ScanReminder);

        var after = await scheduler.GetScanStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(armed.ArmedAt, Is.Not.Null, "premise: activation arms the scan");
            Assert.That(after.Enabled, Is.False);
            Assert.That(after.Runs, Is.Zero, "a tick ran a pass with the switch off");
            Assert.That(after.LastTrigger, Is.Null);
            Assert.That(after.NextDueAt, Is.EqualTo(armed.NextDueAt));
        });
    }

    /// <summary>
    /// With the switch on, a tick runs a pass attributed to the reminder, proposes a dormant account and
    /// moves the next due time a period out — and an operator's pass afterwards does not move it.
    /// </summary>
    /// <remarks>
    /// This is the daily pass itself, the only thing that fills the operator's queue without somebody
    /// pressing a button. The switch is turned on after the grain was activated and armed, which is the
    /// production incident the grain's remarks describe: a scan armed on a pod that read "off" has to
    /// start working the day the fleet carries "on", with no restart and no re-registration.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task With_the_switch_on_a_tick_runs_a_pass_for_the_reminder(CancellationToken ct = default)
    {
        var dormant = await CreateSessionAsync(ct);
        await AccountSeed.BackdateLastLoginAsync(dormant.UserId, DateTimeOffset.UtcNow - TimeSpan.FromDays(400), ct: ct);

        var scheduler = await ArmedSchedulerAsync();

        AccountTimings.Deletion.AutoDeleteEnabled = true;

        var before = DateTimeOffset.UtcNow;
        await TickAsync(scheduler, ScanReminder);
        var after = DateTimeOffset.UtcNow;

        var ticked = await scheduler.GetScanStatusAsync();
        var queued = await QueuedAsync(dormant.UserId);

        await scheduler.RunScanAsync();

        var forced = await scheduler.GetScanStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(ticked.Enabled, Is.True);
            Assert.That(ticked.Runs, Is.EqualTo(1));
            Assert.That(ticked.LastTrigger, Is.EqualTo("reminder"));
            Assert.That(ticked.LastFinishedAt, Is.GreaterThanOrEqualTo(before), "the pass did not finish");
            Assert.That(ticked.LastError, Is.Null);
            Assert.That(ticked.LastProposed, Is.GreaterThanOrEqualTo(1));
            Assert.That(ticked.NextDueAt, Is.InRange(before + ScanPeriod, after + ScanPeriod),
                "a timed pass should report the next one a period out");

            Assert.That(queued, Is.Not.Null, "the reminder's pass did not propose an account idle for 400 days");

            Assert.That(forced.Runs, Is.EqualTo(2));
            Assert.That(forced.LastTrigger, Is.EqualTo("operator"));
            Assert.That(forced.NextDueAt, Is.EqualTo(ticked.NextDueAt), "a forced pass moved the timer's schedule");
        });

        await AccountSeed.BackdateLastLoginAsync(dormant.UserId, DateTimeOffset.UtcNow, ct: ct);
    }

    /// <summary>
    /// A pass that throws says why on the status, is rethrown to the operator who asked, is swallowed
    /// on the timer, and the next clean pass clears it.
    /// </summary>
    /// <remarks>
    /// <para>The status is the answer to "did it run at all?" — the question a production morning went
    /// on — so a failure has to land there and not only in a log line on some pod.</para>
    ///
    /// <para>The fault is a settings row no API can write: a chosen period of <c>int.MaxValue</c>
    /// months, whose threshold overflows <see cref="TimeSpan"/> when the pass reaches it.
    /// <c>SecurityGrain.SetAutoDeletePeriodAsync</c> caps the period at seventy-two months, so this is
    /// not a defect a person can cause; it is the one failure a test can make the pass hit
    /// deterministically from outside the grain.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_pass_that_throws_says_why_and_the_next_clean_pass_clears_it(CancellationToken ct = default)
    {
        var poisoned  = await CreateSessionAsync(ct);
        var scheduler = await ArmedSchedulerAsync();

        await AccountSeed.SetAutoDeleteAsync(poisoned.UserId, int.MaxValue, enabled: true, ct);

        Exception? operatorSaw;
        AutoDeleteScanReport afterOperator, afterTick, afterRepair;

        try
        {
            operatorSaw   = await CaptureAsync(() => scheduler.RunScanAsync().AsTask());
            afterOperator = await scheduler.GetScanStatusAsync();

            AccountTimings.Deletion.AutoDeleteEnabled = true;

            var tickSaw = await CaptureAsync(() => TickAsync(scheduler, ScanReminder));
            afterTick = await scheduler.GetScanStatusAsync();

            Assert.That(tickSaw, Is.Null, "a failed timed pass escaped the reminder; Orleans would log it as a fault");
        }
        finally
        {
            AccountTimings.Deletion.AutoDeleteEnabled = false;

            await using var db = await AccountSeed.NewDbAsync(ct);
            await db.AutoDeleteSettings.Where(s => s.UserId == poisoned.UserId).ExecuteDeleteAsync(ct);
        }

        await scheduler.RunScanAsync();
        afterRepair = await scheduler.GetScanStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(operatorSaw, Is.Not.Null, "the operator's pass failed silently");
            Assert.That(afterOperator.LastError, Is.Not.Null.And.EqualTo(operatorSaw?.Message));
            Assert.That(afterOperator.LastErrorAt, Is.Not.Null);
            Assert.That(afterOperator.LastTrigger, Is.EqualTo("operator"));

            Assert.That(afterTick.LastTrigger, Is.EqualTo("reminder"));
            Assert.That(afterTick.LastError, Is.Not.Null, "the timed pass's failure is not on the status");
            Assert.That(afterTick.LastErrorAt, Is.GreaterThanOrEqualTo(afterOperator.LastErrorAt));

            Assert.That(afterRepair.LastError, Is.Null, "a clean pass left the previous failure on the status");
            Assert.That(afterRepair.LastErrorAt, Is.Null);
            Assert.That(afterRepair.LastFinishedAt, Is.GreaterThanOrEqualTo(afterTick.LastStartedAt));
        });
    }

    /// <summary>
    /// An account whose deletion state cannot be read is left out, and the pass completes for everyone
    /// else.
    /// </summary>
    /// <remarks>
    /// <para>The deletion grain is the only thing that knows whether an account is already scheduled or
    /// has declined, so the pass asks it about every candidate. A store that cannot answer for one of
    /// them must cost that one proposal — not the pass, and not the queue that pass would have
    /// refreshed.</para>
    ///
    /// <para>The unreadable account is seeded as a row and never signed into, because a sign-in
    /// activates its deletion grain (<c>UserGrain</c> tells it about every sign-in) and an active grain
    /// never rereads its state. Its stored state is then overwritten with bytes the storage serializer
    /// cannot read, which is what a store blip or a half-written record looks like to the pass.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_candidate_whose_deletion_state_cannot_be_read_is_left_out(CancellationToken ct = default)
    {
        var longAgo = DateTimeOffset.UtcNow - TimeSpan.FromDays(400);

        var readable = await CreateSessionAsync(ct);
        await AccountSeed.BackdateLastLoginAsync(readable.UserId, longAgo, ct: ct);

        var unreadable = await SeedNeverSignedInAsync(longAgo, ct);
        var grain      = GetGrainFactory().GetGrain<IAccountDeletionGrain>(unreadable);
        var stateKey   = $"@grains/{grain.GetGrainId().Type}/{grain.GetGrainId()}:account-deletion-store";
        var storage    = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.OrleansStorage);

        var scheduler = await ArmedSchedulerAsync();

        AutoDeleteScanReport status;
        QueuedAccountDeletion? readableQueued, unreadableQueued;
        Exception? premise;

        try
        {
            using (var scope = storage.Rent())
                await scope.GetDatabase().StringSetAsync(stateKey, "this is not a grain state");

            premise = await CaptureAsync(() => grain.GetDeletionStatusAsync().AsTask());

            await scheduler.RunScanAsync();

            status           = await scheduler.GetScanStatusAsync();
            readableQueued   = await QueuedAsync(readable.UserId);
            unreadableQueued = await QueuedAsync(unreadable);
        }
        finally
        {
            using (var scope = storage.Rent())
                await scope.GetDatabase().KeyDeleteAsync(stateKey);

            await using var db = await AccountSeed.NewDbAsync(ct);
            await db.Users.IgnoreQueryFilters().Where(u => u.Id == unreadable)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.IsDeleted, true), ct);

            await AccountSeed.BackdateLastLoginAsync(readable.UserId, DateTimeOffset.UtcNow, ct: ct);
        }

        Assert.That(premise, Is.Not.Null, "premise: the corrupted state should make the deletion grain unreadable");

        Assert.Multiple(() =>
        {
            Assert.That(status.LastError, Is.Null, "one unreadable candidate failed the whole pass");
            Assert.That(status.LastFinishedAt, Is.Not.Null);
            Assert.That(unreadableQueued, Is.Null, "an account whose deletion state is unknown was proposed");
            Assert.That(readableQueued, Is.Not.Null, "the pass stopped proposing after the unreadable candidate");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A scheduler activation of this test's own, armed the way the startup call arms one.</summary>
    private async Task<IAutoDeleteSchedulerGrain> ArmedSchedulerAsync()
    {
        var scheduler = GetGrainFactory().GetGrain<IAutoDeleteSchedulerGrain>(Guid.NewGuid());

        schedulers.Add(scheduler);
        await scheduler.EnsureSchedulerActiveAsync();

        return scheduler;
    }

    /// <summary>Delivers one reminder tick the way the reminder service does: as a call on <see cref="IRemindable"/>.</summary>
    private static Task TickAsync(IAutoDeleteSchedulerGrain scheduler, string reminder)
        => scheduler.AsReference<IRemindable>()
           .ReceiveReminder(reminder, new TickStatus(DateTime.UtcNow, ScanPeriod, DateTime.UtcNow));

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    /// <summary>An account that exists as a row and has never signed in, dormant since <paramref name="since"/>.</summary>
    private static async Task<Guid> SeedNeverSignedInAsync(DateTimeOffset since, CancellationToken ct)
    {
        var id = Guid.NewGuid();

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            db.Users.Add(new UserEntity
            {
                Id          = id,
                Username    = $"unread_{id:N}"[..32],
                DisplayName = "Unreadable Deletion State",
                Email       = $"unread_{id:N}@test.local",
                AgreeTOS    = true,
                DateOfBirth = new DateOnly(2000, 1, 1)
            });

            await db.SaveChangesAsync(ct);
        }

        await AccountSeed.BackdateCreatedAtAsync(id, since, ct);

        return id;
    }

    /// <summary>The queue entry for one account, paging through the whole shared queue.</summary>
    private async Task<QueuedAccountDeletion?> QueuedAsync(Guid userId)
    {
        const int page = 200;

        var queue = GetGrainFactory().GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId);

        for (var offset = 0; ; offset += page)
        {
            var snapshot = await queue.ListAsync(offset, page);

            if (snapshot.Entries.FirstOrDefault(entry => entry.UserId == userId) is { } found)
                return found;

            if (snapshot.Entries.Count < page || offset + snapshot.Entries.Count >= snapshot.TotalCount)
                return null;
        }
    }
}
