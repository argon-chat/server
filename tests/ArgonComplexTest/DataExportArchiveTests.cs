namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text.Json;
using AccountContracts;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Storage;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The GDPR archive: what an "export my data" request actually hands a person, and what happens to
/// that archive afterwards.
/// </summary>
/// <remarks>
/// <para>An Art. 15 response is judged on two things a status field cannot express — whether the
/// archive is <em>complete</em>, and whether it is <em>only</em> about the person who asked. Neither
/// is observable from the export's own reporting: the grain says <c>Completed</c> whether it wrote
/// nine files or six, whether a channel contributed every message or only the first batch, and
/// whether the account it described still exists. So this fixture opens the zip. One heavily seeded
/// account is exported once in <see cref="SeedAnAccountAndExportIt"/> and the entries are read into
/// memory; the tests that follow are assertions about that archive's contents, and they are cheap
/// because the expensive part happened once.</para>
///
/// <para>The other half is the lifecycle around the archive — the phases the client polls, the
/// thirty-day rate limit, cancellation, a tick that throws, the presigned link's lifetime, and what
/// survives in the object store when an export expires or its account is erased. Those need their own
/// accounts (one export per account per rate-limit window) and they are what the compressed
/// <c>DataExport</c> clocks exist for: a one-second tick, a twenty-second rate limit and a forty-second
/// archive lifetime turn month-scale contracts into assertions a test run can actually make. Every
/// wait is read off <see cref="AccountTimings"/> rather than written as a number, so the fixture keeps
/// meaning what it says if those clocks move.</para>
///
/// <para>Two things are deliberately reached for below the public API. Retention is asserted against
/// the export bucket itself through <c>IExportS3Service</c>, because "the object is gone" is the whole
/// claim and no client surface reports it. And the two failure tests corrupt a jsonb column with raw
/// SQL — the value converter on the way back out is the only deterministic way to make one export tick
/// throw without touching <c>src/</c>, and a non-deterministic failure (racing a bucket delete against
/// assembly) would test the race rather than the failure path.</para>
/// </remarks>
[TestFixture]
public class DataExportArchiveTests : TestBase
{
    /// <summary>The seven files every export writes, whatever the account holds.</summary>
    private static readonly string[] AlwaysWritten =
    [
        "profile.json", "friends.json", "blocks.json", "settings.json",
        "stats.json", "devices.json", "subscriptions.json"
    ];

    private const string SeededBio        = "the bio that has to survive the export";
    private const string SeededDeviceName = "gdpr-export-device";
    private const string SeededDeviceIp   = "203.0.113.77";

    // ── the account exported once for the whole fixture ─────────────────────────────────────────

    private TestUserSession subject      = null!;
    private TestUserSession peer         = null!;
    private TestUserSession outgoing     = null!;
    private TestUserSession pendingIn    = null!;
    private TestUserSession pendingOut   = null!;
    private TestUserSession blockedUser  = null!;

    private Guid ownedSpace;
    private Guid joinedSpace;
    private Guid talkChannel;
    private Guid bulkChannel;
    private Guid guestChannel;
    private Guid conversationId;

    private Guid savedGifId;
    private Guid passkeyId;
    private Guid uploadedFileId;

    private IReadOnlyDictionary<string, string> archive = null!;
    private IReadOnlyList<DataExportStatus>     observed = null!;
    private DataExportStatus                    finalStatus = null!;
    private ExportStatusDto                     finalGrainStatus = null!;

    /// <summary>
    /// Builds one account that holds a row in every table the export could plausibly care about,
    /// exports it, and reads the archive.
    /// </summary>
    /// <remarks>
    /// <para>Done once because it is the only slow thing here: six registrations, two spaces, three
    /// channels, a conversation, a dozen messages and eight seeded rows, then roughly twenty export
    /// ticks. Every archive assertion below is a statement about this one zip, so they cost nothing
    /// each and can be separate tests — which matters, because "the categories that are exported are
    /// right" and "the categories that are not exported are missing" have different verdicts and must
    /// not share a pass/fail.</para>
    ///
    /// <para>The seeding mixes real API calls with direct rows on purpose. Friendships, requests,
    /// blocks, the auto-delete period, the privacy rule, the spaces, the memberships and every message
    /// go through the product, because a fixture that hand-wrote those would be asserting against its
    /// own idea of the schema. Daily stats, the level row, mute settings, a saved GIF, a passkey and an
    /// uploaded file are written directly: they are produced by machinery (the XP pump, the GIF
    /// provider's HMAC, WebAuthn, a multipart upload) that has no place in an export test, and the
    /// export reads all six straight out of their tables.</para>
    /// </remarks>
    [OneTimeSetUp]
    public async Task SeedAnAccountAndExportIt()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));

        var ct = cts.Token;

        subject     = await CreateSessionAsync(ct);
        peer        = await CreateSessionAsync(ct);
        outgoing    = await CreateSessionAsync(ct);
        pendingIn   = await CreateSessionAsync(ct);
        pendingOut  = await CreateSessionAsync(ct);
        blockedUser = await CreateSessionAsync(ct);

        // Profile. Only the bio: everything else on UserEditInput is premium-gated and answers
        // PREMIUM_REQUIRED for an ordinary account, so a fixture that set them would be asserting on
        // an error path instead of on a profile.
        var edited = await subject.Users.UpdateMe(
            new UserEditInput(null, null, null, null, null, null, null, null, null, null, SeededBio), ct);

        Assert.That(edited, Is.InstanceOf<SuccessUpdateMe>(),
            $"could not write the bio the archive is checked for: {(edited as FailedUpdateMe)?.error}");

        // Friendships in both directions. FriendsGrain writes the pair symmetrically, so both end up
        // in the exported set — which is the point: a friendship the account did not initiate is still
        // its data.
        await BefriendAsync(peer, subject, ct);
        await BefriendAsync(subject, outgoing, ct);

        // A pending request each way, and a block.
        await RequestAsync(pendingIn, subject, ct);
        await RequestAsync(subject, pendingOut, ct);
        await subject.Friends.BlockUser(blockedUser.UserId, ct);

        // The privacy screen's own settings.
        var autoDelete = await subject.Security.SetAutoDeletePeriod(6, ct);

        Assert.That(autoDelete, Is.InstanceOf<SuccessSetAutoDelete>(),
            $"could not set the auto-delete period: {(autoDelete as FailedSetAutoDelete)?.error}");

        var privacy = await subject.Privacy.SetPrivacyRule(
            "stream.draw", PrivacyRuleMode.CONTACTS, null, IonArray<Guid>.Empty, IonArray<Guid>.Empty, ct);

        Assert.That(privacy, Is.True, "the privacy rule the archive is checked for was not written");

        // A device the account signed in from, which is what devices.json is.
        await AccountSeed.BackdateLastLoginAsync(
            subject.UserId, DateTimeOffset.UtcNow.AddHours(-2), SeededDeviceName, ct);

        // Two spaces: one the account owns, one it was invited into. Both hold messages by the
        // account and by somebody else, so "only mine" is falsifiable.
        ownedSpace  = await CreateSpaceAsync(subject, "Export Owned", ct);
        talkChannel = await CreateChannelAsync(subject, ownedSpace, "talk", ct);
        bulkChannel = await CreateChannelAsync(subject, ownedSpace, "bulk", ct);

        await JoinAsync(subject, peer, ownedSpace, ct);

        joinedSpace  = await CreateSpaceAsync(peer, "Export Joined", ct);
        guestChannel = await CreateChannelAsync(peer, joinedSpace, "lounge", ct);

        await JoinAsync(peer, subject, joinedSpace, ct);

        await SayAsync(subject, ownedSpace, talkChannel, "mine in the owned space", ct);
        await SayAsync(peer, ownedSpace, talkChannel, "PEER TEXT in the owned space", ct);
        await SayAsync(subject, joinedSpace, guestChannel, "mine in the joined space", ct);
        await SayAsync(peer, joinedSpace, guestChannel, "PEER TEXT in the joined space", ct);

        // One message more than a batch, so the truncation is visible as a missing message rather
        // than as an arithmetic argument.
        for (var i = 1; i <= AccountTimings.MessageBatchSize + 1; i++)
            await SayAsync(subject, ownedSpace, bulkChannel, $"bulk message {i}", ct);

        // A conversation with traffic in both directions.
        await subject.Chats.SendDirectMessage(
            peer.UserId, "mine in the conversation", new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);
        await peer.Chats.SendDirectMessage(
            subject.UserId, "PEER TEXT in the conversation", new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

        conversationId = await ReadConversationIdAsync(subject.UserId, peer.UserId, ct);

        await SeedRowsWithNoApiAsync(ct);

        // ── the export itself ───────────────────────────────────────────────────────────────────

        var requested = await subject.Security.RequestDataExport(ct);

        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the seeded account's export was refused: {(requested as FailedRequestDataExport)?.error}");

        var seen = new List<DataExportStatus>();

        finalStatus = await Poll.ForValueAsync(
            async () =>
            {
                var status = await subject.Security.GetDataExportStatus(ct);
                seen.Add(status);
                return status;
            },
            status => status.status is DataExportStatusKind.COMPLETED or DataExportStatusKind.FAILED,
            AccountTimings.ExportTick * 90 + AccountTimings.Slack,
            AccountTimings.ExportTick / 4,
            ct);

        observed = seen;

        Assert.That(finalStatus.status, Is.EqualTo(DataExportStatusKind.COMPLETED),
            $"the seeded account's export ended in {finalStatus.status} after {seen.Count} reads " +
            $"and {finalStatus.itemsProcessed} items");
        Assert.That(finalStatus.downloadUrl, Is.Not.Null.And.Not.Empty,
            "a completed export with no download url is an archive nobody can reach");

        finalGrainStatus = await GetGrainFactory()
           .GetGrain<IUserDataExportGrain>(subject.UserId).GetExportStatusAsync();

        archive = await ExportArchive.DownloadAsync(finalStatus.downloadUrl!, ct);
    }

    // ── E1 / E2: what the archive holds ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every file the export writes is present, and each one carries this account's own data.
    /// </summary>
    /// <remarks>
    /// Asserted by value rather than by shape throughout. An archive of well-formed empty files, or
    /// one describing a neighbouring account, is a far likelier failure than a malformed one — the
    /// collectors are nine independent queries keyed on a user id, and the way that goes wrong is a
    /// predicate that matched nothing or matched somebody else. So every file is checked for a value
    /// that only this account could have produced.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void The_archive_holds_a_file_per_category_and_each_carries_the_accounts_own_data()
    {
        Assert.That(archive.Keys, Is.SupersetOf(AlwaysWritten),
            $"the archive holds {string.Join(", ", archive.Keys.Order())}");

        Assert.Multiple(() =>
        {
            Assert.That(archive["profile.json"], Does.Contain(subject.Credentials.email));
            Assert.That(archive["profile.json"], Does.Contain(subject.Credentials.username));
            Assert.That(archive["profile.json"], Does.Contain(SeededBio),
                "the profile file carries the identity but not the profile");

            Assert.That(archive["friends.json"], Does.Contain(peer.UserId.ToString()),
                "the friendship the peer initiated is missing from the exported friend list");
            Assert.That(archive["friends.json"], Does.Contain(outgoing.UserId.ToString()),
                "the friendship this account initiated is missing from the exported friend list");

            Assert.That(archive["blocks.json"], Does.Contain(blockedUser.UserId.ToString()));

            Assert.That(archive["settings.json"], Does.Contain(ownedSpace.ToString()),
                "the muted space is missing from the exported settings");
            Assert.That(archive["settings.json"], Does.Contain("\"Months\": 6"),
                "the auto-delete period the account chose is missing from the exported settings");

            Assert.That(archive["stats.json"], Does.Contain("\"MessagesSent\": 42"));
            Assert.That(archive["stats.json"], Does.Contain("\"CurrentLevel\": 7"));

            Assert.That(archive["devices.json"], Does.Contain(SeededDeviceName));
            Assert.That(archive["devices.json"], Does.Contain(SeededDeviceIp),
                "an export of the device history without the addresses it recorded omits its most sensitive column");

            Assert.That(archive.Keys, Does.Contain($"dm/{conversationId}.json"),
                "the conversation is not in the archive at all");
            Assert.That(archive[$"dm/{conversationId}.json"], Does.Contain("mine in the conversation"));

            Assert.That(archive.Keys, Does.Contain($"channels/{ownedSpace}/{talkChannel}.json"),
                "the owned space's channel is not in the archive");
            Assert.That(archive[$"channels/{ownedSpace}/{talkChannel}.json"], Does.Contain("mine in the owned space"));

            Assert.That(archive.Keys, Does.Contain($"channels/{joinedSpace}/{guestChannel}.json"),
                "the joined space's channel is not in the archive");
            Assert.That(archive[$"channels/{joinedSpace}/{guestChannel}.json"], Does.Contain("mine in the joined space"));
        });
    }

    /// <summary>
    /// The archive also accounts for the personal data the nine files do not cover.
    /// </summary>
    /// <remarks>
    /// <para>Every category below is data the product holds <em>about this account</em>, keyed on its
    /// user id, and every one of them is seeded by this fixture before the export runs: two pending
    /// friend requests naming the other party, a privacy rule, a saved GIF, a passkey, and a file the
    /// account uploaded. An Art. 15 response that silently omits them is incomplete, and nothing in
    /// the archive or the status says so — the export reports <c>Completed</c> either way, which is
    /// what makes the omission worth a test rather than a note.</para>
    ///
    /// <para>The assertion is deliberately weak in form — the ids have to appear <em>somewhere</em> in
    /// the archive, in any file, under any shape — so that it stays green under any reasonable fix
    /// (new files, extra keys in <c>settings.json</c>, a single <c>other.json</c>) and can only be red
    /// because the data is genuinely absent.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void The_archive_accounts_for_the_personal_data_the_nine_files_leave_out()
    {
        var everything = string.Join('\n', archive.Values);

        Assert.Multiple(() =>
        {
            Assert.That(everything, Does.Contain(pendingIn.UserId.ToString()),
                "the pending friend request this account received names a person and is not exported");
            Assert.That(everything, Does.Contain(pendingOut.UserId.ToString()),
                "the pending friend request this account sent names a person and is not exported");
            Assert.That(everything, Does.Contain("stream.draw"),
                "the account's privacy rules are not exported");
            Assert.That(everything, Does.Contain(savedGifId.ToString()),
                "the account's saved GIFs are not exported");
            Assert.That(everything, Does.Contain(passkeyId.ToString()),
                "the account's passkeys are not exported, not even as names and creation dates");
            Assert.That(everything, Does.Contain(uploadedFileId.ToString()),
                "the files the account uploaded are not exported, not even as metadata");
        });
    }

    /// <summary>
    /// A channel where the account wrote more messages than one batch exports all of them.
    /// </summary>
    /// <remarks>
    /// <c>UserDataExportGrain.CollectChannelMessagesBatchAsync</c> takes <c>MessageBatchSize</c>
    /// messages ordered by id with no offset, and <c>ExportCursor</c> has no field that could carry
    /// one, so the channel contributes its first batch and the tick moves on. Nothing records that
    /// anything was dropped: the file looks complete, the status says <c>Completed</c>, and the
    /// person receiving the archive has no way to tell. Seeded with exactly one message more than the
    /// batch so the failure reads as "the last one is missing" rather than as a size argument.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void A_channel_with_more_messages_than_one_batch_exports_all_of_them()
    {
        var expected = AccountTimings.MessageBatchSize + 1;
        var path     = $"channels/{ownedSpace}/{bulkChannel}.json";

        Assert.That(archive.Keys, Does.Contain(path), "the busy channel is not in the archive at all");

        var messages = CountMessages(archive[path]);

        Assert.That(messages, Is.EqualTo(expected),
            $"the account wrote {expected} messages in that channel and the archive carries {messages}; " +
            $"the batch ceiling is {AccountTimings.MessageBatchSize}");
        Assert.That(archive[path], Does.Contain($"bulk message {expected}"),
            "the message past the batch ceiling is the one that vanished");
    }

    /// <summary>
    /// Nothing anybody else wrote is in the archive.
    /// </summary>
    /// <remarks>
    /// The mirror of completeness, and the one that would be a data breach rather than a shortfall: an
    /// Art. 15 response is about the person who asked, so the peer's half of a conversation and other
    /// members' channel messages must not travel with it. The peer wrote in both spaces and in the
    /// conversation, with text tagged so a leak is unmistakable in whichever file it turns up.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void Nothing_written_by_anybody_else_is_in_the_archive()
    {
        var everything = string.Join('\n', archive.Values);

        Assert.That(everything, Does.Not.Contain("PEER TEXT"),
            "somebody else's messages travelled with this account's archive");
    }

    // ── E3 / H27: the status surface ────────────────────────────────────────────────────────────

    /// <summary>
    /// The status the client polls walks the export through its phases and never goes backwards.
    /// </summary>
    /// <remarks>
    /// This is the only thing a desktop client can see while an export runs, so it has to be a
    /// faithful account of the job: the phases in order, an item count that rises, one export id
    /// throughout, and a download url that appears exactly when the archive does. The reads were
    /// collected during the seeded account's own export, four times a tick, so the sequence below is
    /// the sequence a real poller would have seen.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void The_status_surface_walks_the_export_through_its_phases()
    {
        var order = new[]
        {
            DataExportStatusKind.QUEUED, DataExportStatusKind.COLLECTING,
            DataExportStatusKind.ASSEMBLING, DataExportStatusKind.COMPLETED
        };

        var phases = new List<DataExportStatusKind>();

        foreach (var status in observed.Where(status => phases.Count == 0 || phases[^1] != status.status))
            phases.Add(status.status);

        Assert.Multiple(() =>
        {
            // A subsequence rather than an equality: a poll cannot be guaranteed to land inside every
            // phase. What must hold is that no phase was ever revisited and none arrived out of turn.
            Assert.That(phases, Is.SubsetOf(order), $"the export reported {string.Join(" -> ", phases)}");
            Assert.That(phases.Select(phase => Array.IndexOf(order, phase)), Is.Ordered.Ascending,
                $"the reported phase went backwards: {string.Join(" -> ", phases)}");
            Assert.That(phases, Does.Contain(DataExportStatusKind.COLLECTING));
            Assert.That(phases, Does.Contain(DataExportStatusKind.ASSEMBLING),
                "assembly was never visible, so a client can never say what the export is doing at the end");
            Assert.That(phases[^1], Is.EqualTo(DataExportStatusKind.COMPLETED));

            Assert.That(observed.Select(status => status.itemsProcessed), Is.Ordered.Ascending,
                "the progress counter fell during the export");
            Assert.That(observed.Select(status => status.exportId).Distinct(), Has.Exactly(1).Items,
                "the export changed identity mid-flight");

            Assert.That(observed.Where(status => status.status != DataExportStatusKind.COMPLETED)
                                .Select(status => status.downloadUrl), Is.All.Null,
                "a download url was offered before the archive existed");

            // The grain is the source of truth; the ion surface in front of it must not lose or
            // reshape anything the client renders.
            Assert.That(finalStatus.exportId, Is.EqualTo(finalGrainStatus.ExportId));
            Assert.That(finalStatus.downloadUrl, Is.EqualTo(finalGrainStatus.DownloadUrl));
            Assert.That(finalStatus.itemsProcessed, Is.EqualTo(finalGrainStatus.ItemsProcessed));
            Assert.That(finalStatus.startedAt, Is.EqualTo(finalGrainStatus.StartedAt));
        });
    }

    /// <summary>
    /// The status carries a total the client can show progress against.
    /// </summary>
    /// <remarks>
    /// <c>DataExportStatus.totalItemsEstimate</c> exists for one purpose — it is the denominator of
    /// the progress bar the desktop client draws — and <c>UserDataExportGrain</c> assigns it once, to
    /// zero, in <c>RequestExportAsync</c>, and never computes it. Every export therefore reports "N of
    /// 0", which is either a division by zero or an infinite bar depending on the client. Asserted
    /// against the seeded account, which processed well over a dozen items, so a green here means a
    /// real estimate and not a coincidence.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void The_status_carries_a_total_a_client_can_render_progress_against()
    {
        Assert.That(finalStatus.itemsProcessed, Is.GreaterThan(1),
            "the seeded export did no work, so there is nothing to compare an estimate against");
        Assert.That(finalStatus.totalItemsEstimate, Is.GreaterThan(0),
            $"the export processed {finalStatus.itemsProcessed} items against an estimate of " +
            $"{finalStatus.totalItemsEstimate}, which no client can turn into progress");
    }

    // ── E3 / E4: lifetime and rate limit ────────────────────────────────────────────────────────

    /// <summary>
    /// Once the archive's lifetime is up it is unreachable and gone from the object store.
    /// </summary>
    /// <remarks>
    /// <para>Two claims, and they belong together because the second is what makes the first mean
    /// anything. The status flipping to <c>EXPIRED</c> with a null url only describes what the product
    /// will admit to; the archive is a zip holding an e-mail address, a phone number, a date of birth,
    /// up to a hundred IP addresses and every message the person wrote, and "expired" has to mean it
    /// is not in the bucket any more.</para>
    ///
    /// <para><c>UserDataExportGrain.CheckExpiration</c> sets the status, nulls the url and nulls
    /// <c>ArchiveS3Key</c> — and never calls <c>DeleteObjectAsync</c>. Nulling the key also throws away
    /// the only reference to the object, so nothing can delete it afterwards either; every export ever
    /// run accumulates in the export bucket permanently. The store is asked directly here because no
    /// client surface reports it.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_archive_past_its_lifetime_is_unreachable_and_gone_from_the_store(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var status  = await ExportAndWaitAsync(session, ct);

        var url  = status.downloadUrl!;
        var keys = await ArchiveKeysAsync(session.UserId, ct);

        Assert.That(keys, Has.Exactly(1).Items, $"the completed export left {keys.Count} objects behind");

        // A fixed wait, and it is the right instrument: what is being waited for is a deadline passing,
        // and there is no state to poll for until it has.
        await Task.Delay(AccountTimings.ArchiveTtl + AccountTimings.Slack, ct);

        var expired  = await session.Security.GetDataExportStatus(ct);
        var fetched  = await ExportArchive.TryDownloadAsync(url, ct);
        var leftover = await ArchiveKeysAsync(session.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(expired.status, Is.EqualTo(DataExportStatusKind.EXPIRED),
                $"the archive is {AccountTimings.ArchiveTtl} old and the status still says {expired.status}");
            Assert.That(expired.downloadUrl, Is.Null, "an expired archive must not still be offered for download");
            Assert.That(fetched.Status, Is.Not.EqualTo(HttpStatusCode.OK),
                "the link the person was e-mailed still serves the archive after it expired");
            Assert.That(leftover, Is.Empty,
                $"the expired archive is still in the export bucket as {string.Join(", ", leftover)}");
        });
    }

    /// <summary>
    /// A completed export is refused until the rate-limit window has passed, and accepted after.
    /// </summary>
    /// <remarks>
    /// Both halves matter and the second is the one that would rot silently. A rate limit that never
    /// lifts is indistinguishable from one that lifts correctly until somebody waits out the period,
    /// which in production is thirty days — nobody waits, so nothing tests it. Here the window is
    /// twenty seconds. Asserted over both surfaces because they map the same grain error to different
    /// enums and the console's has no "already in progress" for it to be confused with.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_completed_export_is_rate_limited_until_the_window_passes(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        await ExportAndWaitAsync(session, ct);

        var immediate = await session.Security.RequestDataExport(ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);

        await using var scope = consoleScope;

        var fromConsole = await console.RequestExportGDRP(ct);

        Assert.Multiple(() =>
        {
            Assert.That(immediate, Is.InstanceOf<FailedRequestDataExport>(),
                "a second archive was started inside the rate-limit window");
            Assert.That((immediate as FailedRequestDataExport)?.error, Is.EqualTo(DataExportError.RATE_LIMITED));
            Assert.That(fromConsole, Is.EqualTo(RequestExportGDRPStatus.RateLimit),
                "the console cannot tell the person why their export was refused");
        });

        // Again, a deadline rather than a state change: nothing happens at the end of the window
        // except that the next request stops being refused.
        await Task.Delay(AccountTimings.ExportRateLimit + AccountTimings.Slack, ct);

        var afterwards = await session.Security.RequestDataExport(ct);

        Assert.That(afterwards, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the rate limit did not lift after {AccountTimings.ExportRateLimit}: " +
            $"{(afterwards as FailedRequestDataExport)?.error}");

        await session.Security.CancelDataExport(ct);
    }

    /// <summary>
    /// The account console can start an export and reports one that is running.
    /// </summary>
    /// <remarks>
    /// The console is the web surface, and it has exactly one bit to show for the whole feature:
    /// <c>MeDetails.gdrpExportInProgress</c>, which gates its export button. That bit has to be true
    /// while the export runs and false once it does not, or the button is either stuck disabled or
    /// invites a request the server will refuse. Nothing else about the archive is reachable from
    /// there — no id, no status, no url — so this is the console's entire view of an export.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_account_console_can_start_an_export_and_reports_it_while_it_runs(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);

        await using var scope = consoleScope;

        Assert.That((await console.GetMe(ct)).gdrpExportInProgress, Is.False,
            "a fresh account is reported as already exporting");

        var requested = await console.RequestExportGDRP(ct);

        Assert.That(requested, Is.EqualTo(RequestExportGDRPStatus.Ok), "the console could not start an export");

        Assert.That((await console.GetMe(ct)).gdrpExportInProgress, Is.True,
            "the console does not report the export it just started, so its button stays enabled");

        Assert.That(await console.RequestExportGDRP(ct), Is.EqualTo(RequestExportGDRPStatus.Already),
            "a second request while one is running must be reported as already running, not as a new export");

        var finished = await AccountConsoleHarness.WaitForExportAsync(session, DataExportStatusKind.COMPLETED, ct: ct);

        Assert.That(finished.status, Is.EqualTo(DataExportStatusKind.COMPLETED),
            $"the console-started export stalled in {finished.status}");

        Assert.That((await console.GetMe(ct)).gdrpExportInProgress, Is.False,
            "the console still reports an export that has finished");
    }

    // ── E5: cancellation ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancelling a running export deletes what it had already written and frees the next request.
    /// </summary>
    /// <remarks>
    /// <para>The intermediates are the reason this is not just a state test. Collection writes one
    /// unencrypted JSON object per category straight into the export bucket under
    /// <c>exports/{user}/{export}/intermediate/</c> — the profile with e-mail and date of birth, the
    /// device history with its addresses — and they are only swept up when assembly finishes. A
    /// cancellation that left them there would leave a person's data in an object store with no TTL,
    /// no reference from anywhere, and nothing that would ever remove it.</para>
    ///
    /// <para>The export is polled to <c>COLLECTING</c> before cancelling rather than cancelled
    /// immediately, so there is certainly something to clean up; cancelling out of <c>QUEUED</c> would
    /// pass whether or not the cleanup works.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Cancelling_a_running_export_wipes_what_it_wrote_and_frees_the_next_request(CancellationToken ct = default)
    {
        var session   = await CreateSessionAsync(ct);
        var requested = await session.Security.RequestDataExport(ct);

        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the export was refused: {(requested as FailedRequestDataExport)?.error}");

        var started = (SuccessRequestDataExport)requested;

        var running = await AccountConsoleHarness.WaitForExportAsync(session, DataExportStatusKind.COLLECTING, ct: ct);

        Assert.That(running.status, Is.EqualTo(DataExportStatusKind.COLLECTING),
            $"the export never reached collection; it was {running.status}");

        await session.Security.CancelDataExport(ct);

        var after     = await session.Security.GetDataExportStatus(ct);
        var leftovers = await KeysUnderAsync($"exports/{session.UserId}/{started.exportId}/intermediate/", ct);
        var restarted = await session.Security.RequestDataExport(ct);

        Assert.Multiple(() =>
        {
            Assert.That(after.status, Is.EqualTo(DataExportStatusKind.IDLE),
                "a cancelled export must leave the account able to ask for another one");
            Assert.That(after.exportId, Is.Null, "the cancelled job is still the account's current export");
            Assert.That(leftovers, Is.Empty,
                $"cancellation left {leftovers.Count} intermediate files of personal data in the bucket: " +
                $"{string.Join(", ", leftovers)}");
            Assert.That(restarted, Is.InstanceOf<SuccessRequestDataExport>(),
                $"a cancelled export counted against the rate limit: {(restarted as FailedRequestDataExport)?.error}");
            Assert.That((restarted as SuccessRequestDataExport)?.exportId, Is.Not.EqualTo(started.exportId),
                "the new request resumed the cancelled job instead of starting a fresh one");
            Assert.That(AccountTimings.Emails.Sent(session.Credentials.email, EmailKinds.ExportReady), Is.Empty,
                "a cancelled export announced an archive by e-mail");
        });

        await session.Security.CancelDataExport(ct);
    }

    // ── E6: failure ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An export tick that cannot read a row fails the export, says so, and lets the account ask again.
    /// </summary>
    /// <remarks>
    /// <para>The failure is injected by writing a JSON object into the profile's <c>Badges</c> column,
    /// which is jsonb behind a value converter that deserialises it into a <c>List&lt;string&gt;</c>.
    /// The read throws, inside <c>CollectProfileAsync</c>, on the first collection tick. That is the
    /// only deterministic way to make a tick throw from outside <c>src/</c>: the alternatives — racing
    /// a bucket deletion against assembly, or killing the store — test a race or the store rather than
    /// the grain's failure path.</para>
    ///
    /// <para>What has to hold afterwards is that the job is terminal and honest rather than stuck: a
    /// <c>FAILED</c> status with a reason recorded for an operator, no download url, and — the part
    /// that matters to the person — the ability to ask again straight away, because a failed export
    /// produced no archive and so must not consume the one request they get per rate-limit window.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_tick_that_cannot_read_a_row_fails_the_export_and_lets_the_account_ask_again(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        await BreakTheProfileAsync(session.UserId, ct);

        var failed = await RunUntilFailedAsync(session, ct);

        var grainStatus = await GetGrainFactory()
           .GetGrain<IUserDataExportGrain>(session.UserId).GetExportStatusAsync();

        await RepairTheProfileAsync(session.UserId, ct);

        var again = await session.Security.RequestDataExport(ct);

        Assert.Multiple(() =>
        {
            Assert.That(failed.status, Is.EqualTo(DataExportStatusKind.FAILED),
                $"the export ended in {failed.status} rather than failing");
            Assert.That(failed.downloadUrl, Is.Null, "a failed export offered a download");
            Assert.That(grainStatus.FailureReason, Is.Not.Null.And.Not.Empty,
                "the failure was recorded without a reason, so nobody can find out what broke");
            Assert.That(again, Is.InstanceOf<SuccessRequestDataExport>(),
                $"a failed export consumed the rate-limit window: {(again as FailedRequestDataExport)?.error}");
        });

        await session.Security.CancelDataExport(ct);
    }

    /// <summary>
    /// A failed export does not leave its intermediate files in the bucket.
    /// </summary>
    /// <remarks>
    /// The same argument as cancellation, on the path that is far likelier to be taken: an export that
    /// throws part-way has already written every category before the failing one into
    /// <c>exports/{user}/{export}/intermediate/</c>, and <c>ProcessTickAsync</c>'s catch records the
    /// failure and stops the timer without touching the store. Those objects are plain JSON holding the
    /// account's e-mail, phone number, date of birth and device addresses, they have no TTL, and after
    /// the state write nothing in the system still refers to them. Failure is injected late here — in a
    /// channel's message list, by corrupting the polymorphic entity column of one message — precisely
    /// so that seven files exist by the time the tick throws.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_failed_export_does_not_leave_its_intermediate_files_behind(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var space   = await CreateSpaceAsync(session, "Export Failure", ct);
        var channel = await CreateChannelAsync(session, space, "broken", ct);

        var messageId = await SayAsync(session, space, channel, "the message that cannot be read back", ct);

        await BreakTheMessageAsync(messageId, ct);

        var failed   = await RunUntilFailedAsync(session, ct);
        var exportId = failed.exportId;

        Assert.That(failed.status, Is.EqualTo(DataExportStatusKind.FAILED),
            $"the export ended in {failed.status} rather than failing, so there is nothing to clean up");
        Assert.That(exportId, Is.Not.Null, "the failed export forgot its own id");

        var leftovers = await KeysUnderAsync($"exports/{session.UserId}/{exportId}/intermediate/", ct);

        Assert.That(leftovers, Is.Empty,
            $"the failed export left {leftovers.Count} files of personal data in the bucket with nothing " +
            $"left referring to them: {string.Join(", ", leftovers)}");
    }

    /// <summary>
    /// The person who asked for an export is told when it fails.
    /// </summary>
    /// <remarks>
    /// They are told when it starts — <c>SendExportStartedAsync</c> — and when the archive is ready,
    /// and those two mails are the whole conversation. When a tick throws there is no third mail and
    /// no kind for one in <c>EmailKinds</c>, so a person who asked for their data over the web console
    /// receives "we have started preparing your archive" and then nothing, ever: the console shows no
    /// export status at all, and the only surface that reports <c>FAILED</c> is the desktop client's
    /// privacy screen. Under Art. 12(4) the controller has to say that it is not acting on the request.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_person_who_asked_for_an_export_is_told_when_it_fails(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var email   = session.Credentials.email;

        await BreakTheProfileAsync(session.UserId, ct);

        var failed = await RunUntilFailedAsync(session, ct);

        Assert.That(failed.status, Is.EqualTo(DataExportStatusKind.FAILED),
            $"the export ended in {failed.status} rather than failing");

        var announced = await AccountTimings.Emails.WaitForAsync(email, EmailKinds.ExportStarted, AccountTimings.Slack, ct);

        Assert.That(announced, Is.Not.Null, "the export was never announced, so there is no promise to break");

        var afterwards = AccountTimings.Emails.Sent(email)
           .Where(mail => mail.Kind != EmailKinds.ExportStarted && mail.At >= announced!.At)
           .ToList();

        Assert.That(afterwards, Is.Not.Empty,
            "the account was told its archive was being prepared and never told that it was not");
    }

    // ── E8: concurrency, and deletion seen from the export side ─────────────────────────────────

    /// <summary>
    /// Two requests fired at the same instant start exactly one export.
    /// </summary>
    /// <remarks>
    /// The client's own button can be pressed twice, and the console and the desktop app are two
    /// surfaces onto the same grain. Two archives of one account built at once is wasted work at best;
    /// at worst the second overwrites the first's url while somebody is downloading it. The guard is
    /// the grain's own turn-based execution, so what this pins is that the guard is read and written
    /// inside a single turn — a check that spanned an <c>await</c> would let both through.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Two_requests_at_the_same_instant_start_exactly_one_export(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var both = await Task.WhenAll(
            session.Security.RequestDataExport(ct),
            session.Security.RequestDataExport(ct));

        var accepted = both.OfType<SuccessRequestDataExport>().ToList();
        var status   = await session.Security.GetDataExportStatus(ct);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Has.Exactly(1).Items,
                $"{accepted.Count} of two simultaneous requests started an export");
            Assert.That(both.OfType<FailedRequestDataExport>().Select(failure => failure.error),
                Is.All.EqualTo(DataExportError.ALREADY_IN_PROGRESS),
                "the losing request was refused for the wrong reason");
            Assert.That(status.exportId, Is.EqualTo(accepted.FirstOrDefault()?.exportId),
                "the account is running an export neither request was told about");
        });

        await session.Security.CancelDataExport(ct);
    }

    /// <summary>
    /// An account inside its deletion grace can still ask for its data.
    /// </summary>
    /// <remarks>
    /// The moment a person schedules the erasure of their account is exactly the moment they are most
    /// likely to want a copy of it, and during the grace nothing has been erased yet — the row is
    /// untouched until execution. So the request must be answered, not refused. This is the benign half
    /// of the deletion/export interaction; the half that is not benign is what happens to the archive
    /// once execution runs, which is the next test.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_account_inside_its_deletion_grace_can_still_ask_for_its_data(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);

        await using var scope = consoleScope;

        var scheduled = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.That(scheduled.success, Is.True, $"could not schedule the deletion: {scheduled.error}");

        var requested = await session.Security.RequestDataExport(ct);

        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(),
            $"an account inside its deletion grace was refused its own data: " +
            $"{(requested as FailedRequestDataExport)?.error}");

        // Put the account back before the compressed grace runs out under the rest of the fixture.
        await session.Security.CancelDataExport(ct);

        var cancelled = await console.CancelDeleteAccount(ct);

        Assert.That(cancelled.success, Is.True, $"could not cancel the deletion: {cancelled.error}");
    }

    /// <summary>
    /// Erasing the account destroys the archive it exported.
    /// </summary>
    /// <remarks>
    /// <para><c>AccountDeletionGrain.ExecuteDeletionAsync</c> has ten steps and none of them mentions
    /// the export bucket or <c>IUserDataExportGrain</c>. A person who exports their data and then
    /// deletes their account leaves <c>exports/{user}/{export}/export-*.zip</c> behind indefinitely:
    /// <c>profile.json</c> with their e-mail, phone number and date of birth, <c>devices.json</c> with
    /// up to a hundred of their IP addresses, and every message they wrote — sitting unencrypted in an
    /// object store after the account is gone, with a presigned link that stays valid for the rest of
    /// its lifetime.</para>
    ///
    /// <para>Asserted against the bucket rather than against the link, deliberately: the presigned url
    /// expires on its own schedule, so a refused download would be ambiguous evidence, while an object
    /// listing is not. Only the export side is asserted here — what deletion does to the account row
    /// itself is the deletion fixture's business.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Erasing_the_account_destroys_the_archive_it_exported(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        await ExportAndWaitAsync(session, ct);

        var before = await ArchiveKeysAsync(session.UserId, ct);

        Assert.That(before, Has.Exactly(1).Items,
            $"the export did not leave exactly one archive behind: {string.Join(", ", before)}");

        var (consoleScope, console) = AccountConsoleHarness.Console(session);

        await using var scope = consoleScope;

        var scheduled = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.That(scheduled.success, Is.True, $"could not schedule the deletion: {scheduled.error}");

        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, ct: ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the account was not erased; deletion status was {reached}");

        var after = await ArchiveKeysAsync(session.UserId, ct);

        Assert.That(after, Is.Empty,
            $"the erased account's archive is still in the export bucket as {string.Join(", ", after)}");
    }

    // ── E9: the edges ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An account with nothing in it still gets an archive worth having.
    /// </summary>
    /// <remarks>
    /// The commonest export in production is this one — somebody who signed up, looked around and
    /// asked what the service holds about them — and the answer has to be a real archive rather than
    /// an empty file or a failure. Seven files, no conversations, no channels, and a profile that
    /// identifies them. The absence of the other two paths is asserted as well: an account in no space
    /// must not produce a <c>channels/</c> entry, and an empty archive of six files with no
    /// <c>profile.json</c> is the shape the next test is about.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_account_with_nothing_in_it_still_gets_a_usable_archive(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var status  = await ExportAndWaitAsync(session, ct);

        var entries = await ExportArchive.DownloadAsync(status.downloadUrl!, ct);

        Assert.Multiple(() =>
        {
            Assert.That(entries.Keys.Order(), Is.EqualTo(AlwaysWritten.Order()).AsCollection,
                $"a fresh account's archive holds {string.Join(", ", entries.Keys.Order())}");
            Assert.That(entries["profile.json"], Does.Contain(session.Credentials.email),
                "the one file that identifies the person the archive is about does not identify them");
            Assert.That(entries["profile.json"], Does.Contain(session.Credentials.username));
        });
    }

    /// <summary>
    /// An export for an account that does not exist fails instead of completing empty.
    /// </summary>
    /// <remarks>
    /// <para><c>CollectProfileAsync</c> returns silently when the user row is missing, and every other
    /// collector writes its file regardless of whether the list it queried was empty. So assembly finds
    /// six objects, succeeds, and the grain reports <c>Completed</c> with a downloadable archive of
    /// empty arrays and no <c>profile.json</c> — an Art. 15 response about nobody, presented as a
    /// finished one. The <c>"No data collected"</c> branch the grain does have is only reachable if the
    /// object store itself refuses every put.</para>
    ///
    /// <para>This is not only reachable through a mistyped id. The same state is what an export sees
    /// once its account has been erased — the row is soft-deleted and the collectors read through the
    /// global filter — and neither the started nor the ready mail is sent either, because both bail on
    /// a null user. Driven against the grain directly because a user that does not exist has no session
    /// to call from.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_export_for_an_account_that_does_not_exist_fails_instead_of_completing_empty(CancellationToken ct = default)
    {
        var nobody = GetGrainFactory().GetGrain<IUserDataExportGrain>(Guid.NewGuid());

        var requested = await nobody.RequestExportAsync();

        Assert.That(requested.Success, Is.True,
            $"the export was refused before it could be judged: {requested.Error}");

        var status = await Poll.ForValueAsync(
            async () => await nobody.GetExportStatusAsync(),
            reported => reported.Status is ExportStatusKind.Completed or ExportStatusKind.Failed,
            AccountTimings.ExportBudget,
            AccountTimings.ExportTick / 4,
            ct);

        Assert.That(status.Status, Is.EqualTo(ExportStatusKind.Failed),
            $"an export for an account with no row reported {status.Status} " +
            $"after {status.ItemsProcessed} items, with a download url of '{status.DownloadUrl}'");
    }

    // ── seeding ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The six rows the product's own surfaces cannot produce in a test host.
    /// </summary>
    /// <remarks>
    /// Daily stats and the level row are written by the XP pump over days of real use; mute settings
    /// have no ion surface of their own; a saved GIF needs an HMAC minted by the upstream GIF provider;
    /// a passkey needs a WebAuthn authenticator; and an uploaded file needs a multipart upload whose
    /// finalisation is a story of its own. All six are read straight out of their tables by the export
    /// — or, for the last four, not read at all, which is what the completeness test is about.
    /// </remarks>
    private async Task SeedRowsWithNoApiAsync(CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var now = DateTimeOffset.UtcNow;

        savedGifId     = Guid.NewGuid();
        passkeyId      = Guid.NewGuid();
        uploadedFileId = Guid.NewGuid();

        db.MuteSettings.Add(new MuteSettingsEntity
        {
            UserId     = subject.UserId,
            TargetId   = ownedSpace,
            TargetType = MuteTargetType.Space,
            MuteLevel  = MuteLevel.OnlyMentions,
            CreatedAt  = now
        });

        db.UserDailyStats.Add(new UserDailyStatsEntity
        {
            UserId             = subject.UserId,
            Date               = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)),
            MessagesSent       = 42,
            CallsMade          = 3,
            TimeInVoiceSeconds = 1234,
            XpEarned           = 77
        });

        db.UserLevels.Add(new UserLevelEntity
        {
            UserId         = subject.UserId,
            CurrentLevel   = 7,
            TotalXpAllTime = 4242,
            CurrentCycleXp = 12,
            LastXpAward    = now,
            CreatedAt      = now,
            UpdatedAt      = now
        });

        db.Files.Add(new FileEntity
        {
            Id          = uploadedFileId,
            OwnerId     = subject.UserId,
            Purpose     = FilePurpose.Gif,
            S3Key       = $"gifs/{uploadedFileId:N}.webm",
            BucketName  = "argon-test",
            FileSize    = 2048,
            ContentType = "video/webm",
            FileName    = "the-file-the-account-uploaded.webm",
            Finalized   = true,
            CreatedAt   = now,
            UpdatedAt   = now
        });

        db.SavedGifs.Add(new SavedGifEntity
        {
            Id        = savedGifId,
            UserId    = subject.UserId,
            Slug      = $"saved_{savedGifId:N}",
            FileId    = uploadedFileId,
            Width     = 320,
            Height    = 240,
            AddedAt   = now,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Passkeys.Add(new UserPasskeyEntity
        {
            Id           = passkeyId,
            UserId       = subject.UserId,
            Name         = "the passkey the account registered",
            CredentialId = Guid.NewGuid().ToByteArray(),
            PublicKey    = Guid.NewGuid().ToByteArray(),
            SignCount    = 1,
            IsCompleted  = true,
            CreatedAt    = now,
            UpdatedAt    = now
        });

        await db.SaveChangesAsync(ct);

        // The device row the harness writes carries a fixed address; the archive is checked for it, so
        // it has to be the one this fixture named rather than whatever a login happened to record.
        await db.DeviceHistories
           .Where(device => device.UserId == subject.UserId && device.MachineId == SeededDeviceName)
           .ExecuteUpdateAsync(set => set.SetProperty(device => device.LastKnownIP, SeededDeviceIp), ct);
    }

    private static async Task BefriendAsync(TestUserSession from, TestUserSession to, CancellationToken ct)
    {
        await RequestAsync(from, to, ct);
        await to.Friends.AcceptFriendRequest(from.UserId, ct);
    }

    private static async Task RequestAsync(TestUserSession from, TestUserSession to, CancellationToken ct)
    {
        var status = await from.Friends.SendFriendRequest(to.Credentials.username, ct);

        Assert.That(status, Is.EqualTo(SendFriendStatus.SuccessSent).Or.EqualTo(SendFriendStatus.AutoAccepted),
            $"the friend request the fixture needs was answered {status}");
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(
            new CreateServerRequest(name, "GDPR export fixture", string.Empty), ct);

        if (result is SuccessCreateSpace success)
            return success.space.spaceId;

        Assert.Fail($"could not create the space '{name}': {(result as FailedCreateSpace)!.error}");
        return Guid.Empty;
    }

    private static async Task<Guid> CreateChannelAsync(
        TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Text, "GDPR export fixture", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(channel => channel.channel.name == name);

        if (created is not null)
            return created.channel.channelId;

        Assert.Fail($"could not find the channel '{name}' after creating it");
        return Guid.Empty;
    }

    private static async Task JoinAsync(
        TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"the guest could not join the space: {(joined as FailedJoin)?.error}");
    }

    private static Task<long> SayAsync(
        TestUserSession who, Guid spaceId, Guid channelId, string text, CancellationToken ct)
        => who.Channels.SendMessage(
            spaceId, channelId, text, new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

    private static async Task<Guid> ReadConversationIdAsync(Guid userId, Guid peerId, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var conversation = await db.UserConversations
           .AsNoTracking()
           .FirstOrDefaultAsync(row => row.UserId == userId && row.PeerId == peerId, ct);

        Assert.That(conversation, Is.Not.Null, "the direct messages did not produce a conversation to export");

        return conversation!.ConversationId;
    }

    // ── driving and observing an export ─────────────────────────────────────────────────────────

    private async Task<DataExportStatus> ExportAndWaitAsync(TestUserSession session, CancellationToken ct)
    {
        var requested = await session.Security.RequestDataExport(ct);

        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the export was refused: {(requested as FailedRequestDataExport)?.error}");

        var status = await AccountConsoleHarness.WaitForExportAsync(session, DataExportStatusKind.COMPLETED, ct: ct);

        Assert.That(status.status, Is.EqualTo(DataExportStatusKind.COMPLETED),
            $"the export stalled in {status.status} after {AccountTimings.ExportBudget}");
        Assert.That(status.downloadUrl, Is.Not.Null.And.Not.Empty);

        return status;
    }

    private static Task<DataExportStatus> RunUntilFailedAsync(TestUserSession session, CancellationToken ct)
        => Poll.ForValueAsync(
            async () =>
            {
                var status = await session.Security.GetDataExportStatus(ct);

                if (status.status is DataExportStatusKind.IDLE)
                    await session.Security.RequestDataExport(ct);

                return status;
            },
            status => status.status is DataExportStatusKind.FAILED or DataExportStatusKind.COMPLETED,
            AccountTimings.ExportBudget,
            AccountTimings.ExportTick / 4,
            ct);

    private Task<List<string>> KeysUnderAsync(string prefix, CancellationToken ct)
        => FactoryAsp.Services.GetRequiredService<IExportS3Service>().ListObjectsAsync(prefix, ct);

    private Task<List<string>> ArchiveKeysAsync(Guid userId, CancellationToken ct)
        => KeysUnderAsync($"exports/{userId}/", ct);

    private static int CountMessages(string channelFile)
    {
        using var document = JsonDocument.Parse(channelFile);

        return document.RootElement.GetProperty("Messages").GetArrayLength();
    }

    // ── deterministic failure injection ─────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a JSON object into a column whose value converter expects a JSON array.
    /// </summary>
    /// <remarks>
    /// Raw SQL because the point is to produce a row EF <em>cannot read back</em>, and anything written
    /// through the model would be re-serialised into a shape it can. The identifiers are taken from the
    /// model rather than spelled out, so a renamed table or column fails the update loudly instead of
    /// silently leaving the export healthy and the test green for the wrong reason.
    /// </remarks>
    private static async Task OverwriteJsonAsync<TEntity>(
        string property, string json, string keyProperty, string keyLiteral, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var entity = db.Model.FindEntityType(typeof(TEntity));

        Assert.That(entity, Is.Not.Null, $"{typeof(TEntity).Name} is not in the model");

        var schema = entity!.GetSchema();
        var table  = schema is null ? $"\"{entity.GetTableName()}\"" : $"\"{schema}\".\"{entity.GetTableName()}\"";
        var column = entity.FindProperty(property)!.GetColumnName();
        var filter = entity.FindProperty(keyProperty)!.GetColumnName();

        // EF runs the raw string through string.Format before it ever reaches the driver, so the
        // braces of the JSON payload have to be doubled or the call dies on "expected an ASCII digit"
        // long before the database sees anything.
        var payload = json.Replace("{", "{{").Replace("}", "}}");

        var affected = await db.Database.ExecuteSqlRawAsync(
            "UPDATE " + table + " SET \"" + column + "\" = '" + payload + "' WHERE \"" + filter + "\" = " + keyLiteral, ct);

        Assert.That(affected, Is.GreaterThan(0),
            $"the row the failure was to be injected into ({table}.{filter} = {keyLiteral}) was not there");
    }

    private static Task BreakTheProfileAsync(Guid userId, CancellationToken ct)
        => OverwriteJsonAsync<UserProfileEntity>(
            nameof(UserProfileEntity.Badges), "{\"not\":\"an array\"}",
            nameof(UserProfileEntity.UserId), $"'{userId}'", ct);

    private static Task RepairTheProfileAsync(Guid userId, CancellationToken ct)
        => OverwriteJsonAsync<UserProfileEntity>(
            nameof(UserProfileEntity.Badges), "[]",
            nameof(UserProfileEntity.UserId), $"'{userId}'", ct);

    private static Task BreakTheMessageAsync(long messageId, CancellationToken ct)
        => OverwriteJsonAsync<ArgonMessageEntity>(
            nameof(ArgonMessageEntity.Entities), "[{\"no\":\"discriminator\"}]",
            nameof(ArgonMessageEntity.MessageId), messageId.ToString(), ct);
}
