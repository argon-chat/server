namespace ArgonComplexTest.Tests;

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Argon.Features.Storage;
using Argon.Features.Testing;
using Argon.Grains;
using Argon.Grains.Interfaces;
using Argon.Grains.Persistence.States;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

/// <summary>
/// The GDPR export's recovery paths: an archive whose grain went away, an assembly resumed by a later
/// activation, and the pump that keeps running exports alive.
/// </summary>
/// <remarks>
/// <para><c>DataExportArchiveTests</c> runs whole exports and waits out the archive's lifetime and
/// the rate-limit window, which is what makes it the slowest fixture in the suite. Nothing here runs an
/// export from the start or waits for a window. Every test writes the export grain's record as a
/// previous activation would have left it — completed, mid-assembly, collected — and then observes the
/// one transition it is about, so the only clock involved is the one-second export tick and, in one
/// test, the few seconds an archive has left to live.</para>
///
/// <para>The pump is tested on a pump of its own (the same grain class under a fresh key) because the
/// production singleton is shared with every running export in the process, and its ticks are
/// delivered through <c>IRemindable</c> rather than waited for: the pump's reminder is due a minute
/// after it is armed and repeats every two.</para>
/// </remarks>
[TestFixture]
public class DataExportEdgeTests : TestBase
{
    private const string ExportStateName = "user-data-export-store";
    private const string PumpStateName   = "export-pump-store";
    private const string ExpiryReminder  = "export-archive-ttl";
    private const string PumpReminder    = "export-pump";

    private IExportS3Service Store => FactoryAsp.Services.GetRequiredService<IExportS3Service>();

    private static IUserDataExportGrain Export(Guid userId)
        => LifecycleDataHarness.Grains.GetGrain<IUserDataExportGrain>(userId);

    private static GrainId ExportId(Guid userId) => Export(userId).GetGrainId();

    // ── a finished archive and its lifetime ─────────────────────────────────────────────────────

    /// <summary>
    /// A live archive whose grain wakes up without an expiry reminder gets one, and keeps it.
    /// </summary>
    /// <remarks>
    /// Finding R15: a reminder that could not be registered when the export completed used to be lost
    /// for good, and the archive with it — nothing else ever reactivated the grain. Any activation of a
    /// grain holding a live archive now re-arms the reminder, and an activation that finds one already
    /// armed leaves it exactly as it was rather than pushing the archive's deletion further out.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_live_archive_gets_its_expiry_reminder_back_when_its_grain_wakes_and_keeps_it(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var key     = await PutArchiveAsync(session.UserId, ct);
        var id      = ExportId(session.UserId);

        await SeedCompletedAsync(session.UserId, key, DateTimeOffset.UtcNow);

        var status = await Export(session.UserId).GetExportStatusAsync();
        var armed  = await LifecycleDataHarness.ReminderAsync(id, ExpiryReminder);

        await LifecycleDataHarness.DeactivateAsync(id, ct);

        var again = await Export(session.UserId).GetExportStatusAsync();
        var kept  = await LifecycleDataHarness.ReminderAsync(id, ExpiryReminder);

        Assert.Multiple(() =>
        {
            Assert.That(status.Status, Is.EqualTo(ExportStatusKind.Completed), "premise: the seeded archive is live");
            Assert.That(armed, Is.Not.Null,
                "a grain holding a live archive woke up without re-arming the reminder that deletes it");
            Assert.That(again.Status, Is.EqualTo(ExportStatusKind.Completed));
            Assert.That(kept?.StartAt, Is.EqualTo(armed?.StartAt),
                "a second activation re-registered a reminder that was already armed, moving the deletion");
        });
    }

    /// <summary>
    /// A re-armed expiry reminder fires when the archive's lifetime runs out, not a lifetime later.
    /// </summary>
    /// <remarks>
    /// <para><c>export_ready.html</c> promises in writing that the archive is deleted when its lifetime
    /// is up. The reminder that keeps that promise used to be re-armed with the whole lifetime as its
    /// due time whatever the archive's age, so an archive whose reminder was re-armed near the end of
    /// its life lived for up to twice as long — an e-mail address, a phone number, a date of birth and
    /// every message the person wrote, behind a presigned link, for up to another 48 hours in
    /// production.</para>
    ///
    /// <para>The archive here has a few seconds left when its grain wakes. Nothing reads the export's
    /// status afterwards — a read would expire it lazily and hide the reminder's timing — so the only
    /// thing that can remove the object inside the window is the reminder.</para>
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task A_rearmed_expiry_reminder_fires_when_the_archive_expires_not_a_lifetime_later(CancellationToken ct = default)
    {
        var session   = await CreateSessionAsync(ct);
        var key       = await PutArchiveAsync(session.UserId, ct);
        var id        = ExportId(session.UserId);
        var left      = TimeSpan.FromSeconds(6);
        var completed = DateTimeOffset.UtcNow - (AccountTimings.ArchiveTtl - left);
        var expiresAt = (completed + AccountTimings.ArchiveTtl).UtcDateTime;

        await SeedCompletedAsync(session.UserId, key, completed);

        var woken = await Export(session.UserId).GetExportStatusAsync();
        var armed = await LifecycleDataHarness.ReminderAsync(id, ExpiryReminder);

        // A tick that arrives before the archive is due must neither expire it early nor leave the
        // next attempt a whole period away.
        await LifecycleDataHarness.FireReminderAsync(id, ExpiryReminder);

        var early   = await Store.ListObjectsAsync(key, ct);
        var reaimed = await LifecycleDataHarness.ReminderAsync(id, ExpiryReminder);

        Assert.Multiple(() =>
        {
            Assert.That(woken.Status, Is.EqualTo(ExportStatusKind.Completed), "premise: the archive had time left");
            Assert.That(armed?.StartAt, Is.EqualTo(expiresAt).Within(AccountTimings.Slack),
                "the re-armed reminder is not due when the archive expires");
            Assert.That(early, Is.Not.Empty, "an early tick deleted the archive before its lifetime was up");
            Assert.That(reaimed?.StartAt, Is.EqualTo(expiresAt).Within(AccountTimings.Slack),
                "an early tick left the reminder a whole period away from the archive's expiry");
        });

        var gone = await Poll.UntilAsync(
            async () => (await Store.ListObjectsAsync(key, ct)).Count == 0,
            left + TestPresenceTimings.ReminderFloor * 2 + AccountTimings.Slack * 2,
            TimeSpan.FromMilliseconds(250),
            ct);

        Assert.That(gone, Is.True,
            $"the archive had {left} to live and is still in the bucket well after that; its reminder was " +
            $"re-armed for a whole {AccountTimings.ArchiveTtl} lifetime instead of the time it had left");
    }

    /// <summary>
    /// An archive whose reminder was lost is expired by the next read of the export.
    /// </summary>
    [Test, CancelAfter(60_000)]
    public async Task An_archive_whose_reminder_was_lost_is_expired_by_the_next_read(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var key     = await PutArchiveAsync(session.UserId, ct);

        await SeedCompletedAsync(session.UserId, key, DateTimeOffset.UtcNow - AccountTimings.ArchiveTtl - AccountTimings.Slack);

        var status   = await session.Security.GetDataExportStatus(ct);
        var leftover = await Store.ListObjectsAsync(key, ct);

        Assert.Multiple(() =>
        {
            Assert.That(status.status, Is.EqualTo(DataExportStatusKind.EXPIRED),
                $"an archive past its lifetime is reported as {status.status}");
            Assert.That(status.downloadUrl, Is.Null, "an expired archive is still offered for download");
            Assert.That(leftover, Is.Empty, "the read noticed the expiry but left the archive in the bucket");
        });
    }

    /// <summary>
    /// An expiry reminder that has outlived its archive stands itself down, and ignores other names.
    /// </summary>
    /// <remarks>
    /// A new export discards the previous archive without cancelling that archive's reminder, so the
    /// reminder can fire over a grain that has no archive at all. It must not expire anything, and it
    /// must not keep waking the grain: it unregisters itself. The grain's record is rewritten to
    /// <c>Idle</c> under the armed reminder and the grain restarted, which is exactly that state.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task An_expiry_reminder_that_outlived_its_archive_stands_itself_down(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var key     = await PutArchiveAsync(session.UserId, ct);
        var id      = ExportId(session.UserId);

        try
        {
            await SeedCompletedAsync(session.UserId, key, DateTimeOffset.UtcNow);
            await Export(session.UserId).GetExportStatusAsync();

            Assert.That(await LifecycleDataHarness.ReminderAsync(id, ExpiryReminder), Is.Not.Null,
                "premise: the live archive's reminder is armed");

            await LifecycleDataHarness.WriteStateAsync(id, ExportStateName, new UserDataExportGrainState
            {
                Status                = ExportStatus.Idle,
                LastExportCompletedAt = DateTimeOffset.UtcNow
            });
            await LifecycleDataHarness.DeactivateAsync(id, ct);

            await LifecycleDataHarness.FireReminderAsync(id, "some-other-reminder");
            var ignored = await LifecycleDataHarness.ReminderAsync(id, ExpiryReminder);

            await LifecycleDataHarness.FireReminderAsync(id, ExpiryReminder);
            var stoodDown = await LifecycleDataHarness.ReminderAsync(id, ExpiryReminder);
            var status    = await Export(session.UserId).GetExportStatusAsync();

            Assert.Multiple(() =>
            {
                Assert.That(ignored, Is.Not.Null, "a tick of somebody else's reminder cancelled the archive's");
                Assert.That(stoodDown, Is.Null, "a reminder with no archive to expire keeps waking the grain");
                Assert.That(status.Status, Is.EqualTo(ExportStatusKind.Idle), "the stale tick changed the export");
            });
        }
        finally
        {
            await Store.DeleteObjectAsync(key, ct);
        }
    }

    // ── assembly resumed by a later activation ──────────────────────────────────────────────────

    /// <summary>
    /// An assembly that finds nothing collected fails, rather than handing over an empty archive.
    /// </summary>
    /// <remarks>
    /// The collectors always write at least the profile, so an empty working prefix means the files
    /// were lost — a store that dropped them, a prefix deleted under a running export. Finding R29: the
    /// guard used to count the manifest the assembly had just written and never fired. The person is
    /// told the export failed, and the pump stops tracking it.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task An_assembly_that_finds_nothing_collected_fails_rather_than_ship_an_empty_archive(CancellationToken ct = default)
    {
        var session  = await CreateSessionAsync(ct);
        var exportId = Guid.NewGuid();

        await SeedAssemblingAsync(session.UserId, exportId, archiveKey: null);

        var failed = await WaitForExportAsync(session.UserId, ExportStatusKind.Failed, ct);
        var mail   = await AccountTimings.Emails.WaitForAsync(
            session.Credentials.email, EmailKinds.ExportFailed, AccountTimings.Slack, ct);
        var pumped = await ActiveExportsAsync(IExportPumpGrain.SingletonId);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Status, Is.EqualTo(ExportStatusKind.Failed), $"the empty assembly ended in {failed.Status}");
            Assert.That(failed.FailureReason, Is.EqualTo("No data collected"));
            Assert.That(failed.DownloadUrl, Is.Null);
            Assert.That(mail, Is.Not.Null, "the person was never told their export will not arrive");
            Assert.That(pumped, Does.Not.Contain(session.UserId), "the pump still tracks an export that has failed");
        });
    }

    /// <summary>
    /// An assembly resumed after its upload hands over the archive that is already in the store.
    /// </summary>
    /// <remarks>
    /// Finding R17: the archive key is persisted the instant the upload succeeds, so an activation lost
    /// after that resumes with a key and an intact prefix. The resumed assembly must not build a second
    /// archive — the one in the store is the one the key names — and must still finish everything that
    /// follows the upload: the download link, the clean-up of the working files, and the mail.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task An_assembly_resumed_after_its_upload_hands_over_the_archive_already_in_the_store(CancellationToken ct = default)
    {
        var session  = await CreateSessionAsync(ct);
        var exportId = Guid.NewGuid();
        var prefix   = Prefix(session.UserId, exportId);
        var key      = ArchiveKey(session.UserId, exportId);

        await PutObjectAsync(key, Zip(("profile.json", """{"Marker":"the archive uploaded before the restart"}""")), ct);
        await PutObjectAsync($"{prefix}profile.json", Encoding.UTF8.GetBytes("""{"Marker":"a working file"}"""), ct);

        await SeedAssemblingAsync(session.UserId, exportId, archiveKey: key);

        var done    = await WaitForExportAsync(session.UserId, ExportStatusKind.Completed, ct);
        var entries = done.DownloadUrl is { } url ? await ExportArchive.DownloadAsync(url, ct) : null;
        var working = await Store.ListObjectsAsync(prefix, ct);
        var ready   = await AccountTimings.Emails.WaitForAsync(
            session.Credentials.email, EmailKinds.ExportReady, AccountTimings.Slack, ct);

        Assert.Multiple(() =>
        {
            Assert.That(done.Status, Is.EqualTo(ExportStatusKind.Completed), $"the resumed assembly ended in {done.Status}");
            Assert.That(entries?["profile.json"], Does.Contain("the archive uploaded before the restart"),
                "the link does not serve the archive the resumed assembly was handed");
            Assert.That(working, Is.Empty, "the working files outlived the completed export");
            Assert.That(ready, Is.Not.Null, "the person was never told their archive is ready");
        });
    }

    /// <summary>
    /// Assembly builds one archive out of what a previous activation collected.
    /// </summary>
    /// <remarks>
    /// A channel paged over several ticks arrives as its file plus one <c>.partN.json</c> page per tick
    /// (finding R20) and has to leave as one file, in order, with no page of its own in the zip. The
    /// order is the pages' offsets, not the store's listing: the store lists <c>.part10</c> before
    /// <c>.part5</c>, and an archive merged in that order would scramble the channel from its second
    /// page on. A working file that is not JSON is not a page, whatever its name, and travels as it is.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task Assembly_builds_one_archive_out_of_what_a_previous_activation_collected(CancellationToken ct = default)
    {
        var session  = await CreateSessionAsync(ct);
        var exportId = Guid.NewGuid();
        var prefix   = Prefix(session.UserId, exportId);
        var channel  = $"channels/{Guid.NewGuid()}/{Guid.NewGuid()}";

        await PutObjectAsync($"{prefix}profile.json", Encoding.UTF8.GetBytes("""{"Marker":"profile"}"""), ct);
        await PutObjectAsync($"{prefix}{channel}.json", Page(1, 5, """{"ChannelName":"paged","Messages":{0}}"""), ct);
        await PutObjectAsync($"{prefix}{channel}.part5.json", Page(6, 5), ct);
        await PutObjectAsync($"{prefix}{channel}.part10.json", Page(11, 1), ct);
        await PutObjectAsync($"{prefix}notes.part.txt", Encoding.UTF8.GetBytes("not a page"), ct);

        await SeedAssemblingAsync(session.UserId, exportId, archiveKey: null,
            counts: new() { ["profile.json"] = 1, [$"{channel}.json"] = 11 });

        var done    = await WaitForExportAsync(session.UserId, ExportStatusKind.Completed, ct);
        var entries = done.DownloadUrl is { } url ? await ExportArchive.DownloadAsync(url, ct) : null;

        Assert.That(entries, Is.Not.Null, $"the assembly ended in {done.Status} ('{done.FailureReason}')");

        Assert.Multiple(() =>
        {
            Assert.That(entries!.Keys, Is.EquivalentTo(new[] { "profile.json", $"{channel}.json", "notes.part.txt", "manifest.json" }),
                "the archive does not hold exactly the collected files and its manifest");
            Assert.That(MessageIdsOf(entries[$"{channel}.json"]), Is.EqualTo(Enumerable.Range(1, 11)),
                "the channel's pages were not merged back into its file in offset order");
            Assert.That(entries["notes.part.txt"], Is.EqualTo("not a page"));
        });
    }

    /// <summary>
    /// A page with no channel file to merge into fails the export rather than dropping the page.
    /// </summary>
    /// <remarks>
    /// Offset zero always writes the channel file before any page is written, so an orphaned page means
    /// the working prefix is not what the collectors left — and an archive assembled from it would be
    /// silently short of that channel's later messages, the very loss paging exists to prevent. It
    /// fails loudly instead, uploads nothing, and cleans up after itself.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_page_with_no_channel_file_to_merge_into_fails_the_export_rather_than_drop_it(CancellationToken ct = default)
    {
        var session  = await CreateSessionAsync(ct);
        var exportId = Guid.NewGuid();
        var prefix   = Prefix(session.UserId, exportId);

        await PutObjectAsync($"{prefix}profile.json", Encoding.UTF8.GetBytes("""{"Marker":"profile"}"""), ct);
        await PutObjectAsync($"{prefix}channels/{Guid.NewGuid()}/{Guid.NewGuid()}.part5.json",
            Encoding.UTF8.GetBytes("""[{"MessageId":6}]"""), ct);

        await SeedAssemblingAsync(session.UserId, exportId, archiveKey: null);

        var failed  = await WaitForExportAsync(session.UserId, ExportStatusKind.Failed, ct);
        var objects = await Store.ListObjectsAsync($"exports/{session.UserId}/", ct);

        Assert.Multiple(() =>
        {
            Assert.That(failed.Status, Is.EqualTo(ExportStatusKind.Failed),
                $"an archive missing a channel's pages was handed over as {failed.Status}");
            Assert.That(failed.FailureReason, Does.Contain("no file to merge into"));
            Assert.That(objects, Is.Empty, $"the failed export left {string.Join(", ", objects)} in the bucket");
        });
    }

    /// <summary>
    /// A record the tick cannot work from fails the export, takes the archive it names with it, and
    /// frees the account to ask again.
    /// </summary>
    /// <remarks>
    /// The record says an assembly is under way and names an uploaded archive, but names no export —
    /// what an older or damaged write leaves behind. The tick cannot continue from it, and the two
    /// ways that could go wrong are a timer that throws on every tick for ever, holding the account's
    /// export "in progress", and a failure that forgets the archive's key while the object stays in
    /// the bucket (finding R17: only a key the state names can be deleted). So: <c>Failed</c>, the
    /// archive gone from the store, nothing offered for download, and a new request accepted.
    /// </remarks>
    [Test, CancelAfter(60_000)]
    public async Task A_record_the_tick_cannot_work_from_fails_the_export_and_takes_its_archive_with_it(
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var key     = await PutArchiveAsync(session.UserId, ct);

        await LifecycleDataHarness.WriteStateAsync(ExportId(session.UserId), ExportStateName, new UserDataExportGrainState
        {
            Status       = ExportStatus.Assembling,
            StartedAt    = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20),
            ArchiveS3Key = key,
            DownloadUrl  = "https://example.invalid/a-link-to-an-archive-that-must-not-survive",
            Cursor       = new ExportCursor { DataPhaseComplete = true }
        });

        var failed   = await WaitForExportAsync(session.UserId, ExportStatusKind.Failed, ct);
        var leftover = await Store.ListObjectsAsync(key, ct);
        var again    = await Export(session.UserId).RequestExportAsync();

        await Export(session.UserId).CancelExportAsync();

        Assert.Multiple(() =>
        {
            Assert.That(failed.Status, Is.EqualTo(ExportStatusKind.Failed),
                $"a record the tick cannot work from left the export in {failed.Status}");
            Assert.That(failed.DownloadUrl, Is.Null, "the failed export still offers the archive");
            Assert.That(leftover, Is.Empty, "the failure forgot the archive and left it in the bucket");
            Assert.That(again.Success, Is.True, $"the failed export kept the account from asking again: {again.Error}");
        });
    }

    // ── the pump ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The pump keeps the exports that are running and the ones it cannot read, and forgets the rest.
    /// </summary>
    /// <remarks>
    /// <para>What the pump is for: an export's tick is a grain timer, which dies with its activation, so
    /// every couple of minutes the pump asks each export it knows whether it is still running — which
    /// reactivates it and restarts the timer — and forgets the ones that have finished. One export here
    /// is really running, one is idle, and one cannot be read at all because its grain cannot activate.
    /// The unreadable one is kept, not forgotten: dropping it would leave an export nothing will ever
    /// wake again, and the next tick can ask again.</para>
    ///
    /// <para>Around that: a pump whose activation was lost re-arms its reminder when it wakes, a tick
    /// of another reminder is ignored, a tick that leaves nothing to pump stands the reminder down, and
    /// so does a tick that finds nothing to pump in the first place.</para>
    /// </remarks>
    [Test, CancelAfter(90_000)]
    public async Task The_pump_keeps_running_and_unreadable_exports_and_forgets_the_rest(CancellationToken ct = default)
    {
        var pump    = LifecycleDataHarness.Grains.GetGrain<IExportPumpGrain>(Guid.NewGuid());
        var pumpKey = pump.GetPrimaryKey();
        var pumpId  = pump.GetGrainId();

        var running    = await CreateSessionAsync(ct);
        var idle       = Guid.NewGuid();
        var unreadable = Guid.NewGuid();

        var requested = await running.Security.RequestDataExport(ct);
        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(), "premise: an export is running");

        var original = await LifecycleDataHarness.BreakActivationAsync(ExportId(unreadable), ExportStateName, ct);

        HashSet<Guid> afterForeign, afterTick;
        ReminderEntry? armed, rearmed;

        try
        {
            await pump.RegisterActiveExportAsync(running.UserId);
            await pump.RegisterActiveExportAsync(idle);
            await pump.RegisterActiveExportAsync(unreadable);

            armed = await LifecycleDataHarness.ReminderAsync(pumpId, PumpReminder);

            await LifecycleDataHarness.DeactivateAsync(pumpId, ct);

            await LifecycleDataHarness.FireReminderAsync(pumpId, ExpiryReminder);
            rearmed      = await LifecycleDataHarness.ReminderAsync(pumpId, PumpReminder);
            afterForeign = await ActiveExportsAsync(pumpKey);

            await LifecycleDataHarness.FireReminderAsync(pumpId, PumpReminder);
            afterTick = await ActiveExportsAsync(pumpKey);
        }
        finally
        {
            await LifecycleDataHarness.RestoreRecordAsync(ExportId(unreadable), ExportStateName, original);
            await running.Security.CancelDataExport(ct);
        }

        await LifecycleDataHarness.FireReminderAsync(pumpId, PumpReminder);

        var drained      = await ActiveExportsAsync(pumpKey);
        var drainedArmed = await LifecycleDataHarness.ReminderAsync(pumpId, PumpReminder);

        Assert.Multiple(() =>
        {
            Assert.That(armed, Is.Not.Null, "registering an export did not arm the pump");
            Assert.That(rearmed?.StartAt ?? DateTime.MinValue, Is.GreaterThan(armed?.StartAt ?? DateTime.MaxValue),
                "the pump's activation did not re-arm its reminder, so a lost reminder is never replaced");
            Assert.That(afterForeign, Is.EquivalentTo(new[] { running.UserId, idle, unreadable }),
                "a tick of another reminder changed what the pump tracks");
            Assert.That(afterTick, Is.EquivalentTo(new[] { running.UserId, unreadable }),
                "the pump should forget only the idle export: the running one is still working and the " +
                "unreadable one could not be asked");
            Assert.That(drained, Is.Empty, "the pump kept exports that have finished");
            Assert.That(drainedArmed, Is.Null, "a pump with nothing left to pump kept its reminder");
        });

        // A pump that wakes to a tick with nothing to do — its record emptied under a standing reminder.
        await pump.RegisterActiveExportAsync(idle);
        await LifecycleDataHarness.WriteStateAsync(pumpId, PumpStateName, new ExportPumpGrainState());
        await LifecycleDataHarness.DeactivateAsync(pumpId, ct);

        Assert.That(await LifecycleDataHarness.ReminderAsync(pumpId, PumpReminder), Is.Not.Null,
            "premise: the reminder stands over an empty record");

        await LifecycleDataHarness.FireReminderAsync(pumpId, PumpReminder);

        Assert.That(await LifecycleDataHarness.ReminderAsync(pumpId, PumpReminder), Is.Null,
            "a pump woken with nothing to pump kept its reminder");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static string Prefix(Guid userId, Guid exportId) => $"exports/{userId}/{exportId}/intermediate/";

    private static string ArchiveKey(Guid userId, Guid exportId)
        => $"exports/{userId}/{exportId}/export-{DateTime.UtcNow:yyyy-MM-dd}.zip";

    private async Task<string> PutArchiveAsync(Guid userId, CancellationToken ct)
    {
        var key = ArchiveKey(userId, Guid.NewGuid());

        await PutObjectAsync(key, Zip(("profile.json", "{}")), ct);

        return key;
    }

    private async Task PutObjectAsync(string key, byte[] content, CancellationToken ct)
    {
        await using var stream = new MemoryStream(content);

        Assert.That(await Store.PutObjectAsync(key, stream, "application/octet-stream", ct), Is.True,
            $"could not write '{key}' into the export bucket");
    }

    /// <summary>A page of <paramref name="count"/> messages from <paramref name="first"/>, optionally wrapped in a channel file.</summary>
    private static byte[] Page(int first, int count, string? wrapper = null)
    {
        var messages = $"[{string.Join(",", Enumerable.Range(first, count).Select(id => $"{{\"MessageId\":{id}}}"))}]";

        return Encoding.UTF8.GetBytes(wrapper is null ? messages : wrapper.Replace("{0}", messages));
    }

    private static byte[] Zip(params (string Path, string Content)[] files)
    {
        using var buffer = new MemoryStream();

        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var entry = new StreamWriter(zip.CreateEntry(path).Open(), Encoding.UTF8);
                entry.Write(content);
            }
        }

        return buffer.ToArray();
    }

    private static Task SeedCompletedAsync(Guid userId, string key, DateTimeOffset completedAt)
        => LifecycleDataHarness.WriteStateAsync(ExportId(userId), ExportStateName, new UserDataExportGrainState
        {
            Status                = ExportStatus.Completed,
            CurrentExportId       = Guid.NewGuid(),
            StartedAt             = completedAt - TimeSpan.FromSeconds(20),
            CompletedAt           = completedAt,
            LastExportCompletedAt = completedAt,
            ArchiveS3Key          = key,
            DownloadUrl           = "https://example.invalid/the-link-the-person-was-sent"
        });

    private static Task SeedAssemblingAsync(
        Guid userId, Guid exportId, string? archiveKey, Dictionary<string, int>? counts = null)
        => LifecycleDataHarness.WriteStateAsync(ExportId(userId), ExportStateName, new UserDataExportGrainState
        {
            Status          = ExportStatus.Assembling,
            CurrentExportId = exportId,
            StartedAt       = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(20),
            ArchiveS3Key    = archiveKey,
            Cursor          = new ExportCursor { DataPhaseComplete = true },
            CategoryCounts  = counts ?? new() { ["profile.json"] = 1 }
        });

    /// <summary>Wakes the grain and polls it until it reports <paramref name="status"/>.</summary>
    private static Task<ExportStatusDto> WaitForExportAsync(Guid userId, ExportStatusKind status, CancellationToken ct)
        => Poll.ForValueAsync(
            async () => await Export(userId).GetExportStatusAsync(),
            reported => reported.Status == status,
            AccountTimings.ExportTick * 10 + AccountTimings.Slack,
            AccountTimings.ExportTick / 4,
            ct);

    private static async Task<HashSet<Guid>> ActiveExportsAsync(Guid pumpKey)
    {
        var pumpId = LifecycleDataHarness.Grains.GetGrain<IExportPumpGrain>(pumpKey).GetGrainId();

        return (await LifecycleDataHarness.ReadStateAsync<ExportPumpGrainState>(pumpId, PumpStateName)).ActiveExports;
    }

    private static List<int> MessageIdsOf(string channelFile)
    {
        using var document = JsonDocument.Parse(channelFile);

        return document.RootElement.GetProperty("Messages")
           .EnumerateArray()
           .Select(message => message.GetProperty("MessageId").GetInt32())
           .ToList();
    }
}
