namespace ArgonComplexTest.Tests;

using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using Genbox.SimpleS3.Core.Abstracts.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Scheduled account deletion and GDPR data export. Both are grain-driven, both are destructive or
/// privacy-relevant, and neither had a single test — the deletion grain in particular decides
/// whether a real account gets erased.
/// </summary>
[TestFixture]
public class AccountLifecycleTests : TestBase
{
    private async Task<(Guid UserId, string Password)> RegisterAsync(CancellationToken ct)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);
        return (me.userId, FakedTestCreds.password);
    }

    // ── Deletion ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task GetDeletionStatus_ForAFreshAccount_IsNotScheduled(CancellationToken ct = default)
    {
        var (userId, _) = await RegisterAsync(ct);

        var status = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId).GetDeletionStatusAsync();

        Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.None));
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeletion_WithTheWrongPassword_IsRefused(CancellationToken ct = default)
    {
        var (userId, _) = await RegisterAsync(ct);

        var result = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId)
           .RequestDeletionAsync("definitely-not-the-password");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(AccountDeletionRequestError.InvalidPassword));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeletion_WhileOwningASpace_IsRefused(CancellationToken ct = default)
    {
        // Deleting an owner would orphan the space and everyone in it, so ownership has to be
        // handed over first. This is the guard that enforces it.
        var (userId, password) = await RegisterAsync(ct);
        await CreateSpaceAndGetIdAsync(ct);

        var result = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId).RequestDeletionAsync(password);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Error, Is.EqualTo(AccountDeletionRequestError.OwnsSpaces));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDeletion_ThenCancel_RoundTrips(CancellationToken ct = default)
    {
        var (userId, password) = await RegisterAsync(ct);

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId);

        var requestedAt = DateTimeOffset.UtcNow;
        var requested   = await grain.RequestDeletionAsync(password);

        // Against the host's own grace rather than a literal: the integration host runs the deletion
        // clocks compressed (see TestServerConfiguration.AccountDeletion), so a hard-coded "more than
        // 29 days" would have stopped meaning anything the moment that changed — and would have
        // passed for the wrong reason at any grace longer than it.
        var grace = AccountTimings.Grace;

        Assert.Multiple(() =>
        {
            Assert.That(requested.Success, Is.True, requested.Error?.ToString());
            Assert.That(requested.ScheduledDeletionAt, Is.Not.Null);
            Assert.That(requested.ScheduledDeletionAt, Is.GreaterThanOrEqualTo(requestedAt + grace),
                $"the deletion is scheduled a full grace period ({grace}) after the request");
            Assert.That(requested.ScheduledDeletionAt,
                Is.LessThanOrEqualTo(DateTimeOffset.UtcNow + grace),
                $"and no further out than that — the grace is {grace}, not a multiple of it");
        });

        Assert.That((await grain.GetDeletionStatusAsync()).Status, Is.EqualTo(AccountDeletionStatusKind.Scheduled));

        // A second request while one is pending must not silently reset the clock.
        var again = await grain.RequestDeletionAsync(password);
        Assert.Multiple(() =>
        {
            Assert.That(again.Success, Is.False);
            Assert.That(again.Error, Is.EqualTo(AccountDeletionRequestError.AlreadyScheduled));
        });

        var cancelled = await grain.CancelDeletionAsync();
        Assert.That(cancelled.Success, Is.True, cancelled.Error?.ToString());

        Assert.That((await grain.GetDeletionStatusAsync()).Status, Is.EqualTo(AccountDeletionStatusKind.None));
    }

    [Test, CancelAfter(120_000)]
    public async Task CancelDeletion_WhenNothingIsScheduled_IsRefused(CancellationToken ct = default)
    {
        var (userId, _) = await RegisterAsync(ct);

        var result = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId).CancelDeletionAsync();

        Assert.That(result.Success, Is.False);
    }

    [Test, CancelAfter(120_000)]
    public async Task CheckAndExecute_LongBeforeTheDeadline_LeavesTheAccountAlone(CancellationToken ct = default)
    {
        // The timer callback runs on every scheduled account; it must be a no-op until the grace
        // period actually elapses.
        var (userId, password) = await RegisterAsync(ct);

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId);
        await grain.RequestDeletionAsync(password);

        await grain.CheckAndExecuteAsync();

        Assert.That((await grain.GetDeletionStatusAsync()).Status, Is.EqualTo(AccountDeletionStatusKind.Scheduled));
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestAutoDelete_SkipsThePasswordCheck(CancellationToken ct = default)
    {
        // The inactivity worker has no password to offer, so this entry point trades the password
        // check for the same ownership and subscription guards.
        var (userId, _) = await RegisterAsync(ct);

        var grain  = GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId);
        var result = await grain.RequestAutoDeleteAsync();

        Assert.That(result.Success, Is.True, result.Error?.ToString());
        Assert.That((await grain.GetDeletionStatusAsync()).Status, Is.EqualTo(AccountDeletionStatusKind.Scheduled));
    }

    // ── Data export ─────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task GetExportStatus_ForAFreshAccount_IsIdle(CancellationToken ct = default)
    {
        var (userId, _) = await RegisterAsync(ct);

        var grain = GetGrainFactory().GetGrain<IUserDataExportGrain>(userId);

        Assert.Multiple(async () =>
        {
            Assert.That((await grain.GetExportStatusAsync()).Status, Is.EqualTo(ExportStatusKind.Idle));
            Assert.That(await grain.IsExportInProgressAsync(), Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task CancelExport_WhenNoneIsRunning_DoesNotThrow(CancellationToken ct = default)
    {
        var (userId, _) = await RegisterAsync(ct);

        Assert.DoesNotThrowAsync(async () =>
            await GetGrainFactory().GetGrain<IUserDataExportGrain>(userId).CancelExportAsync());
    }

    /// <summary>
    /// The export store says whether this deployment has a bucket at all, separately from whether the
    /// bucket is working.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the flag exists (finding F3).</b> "No export bucket" is a supported deployment
    /// shape: <c>StorageOptions.ExportBucketName</c> ships as <c>""</c>, and
    /// <c>ObjectStorageHealthCheck</c> filters an empty export bucket out of its probe on purpose, so
    /// such an instance is healthy and simply has no data-export feature. Every call on
    /// <c>ExportS3Service</c> would then name a bucket called <c>""</c>, which the store answers with
    /// an error — and since finding R16 <c>ListObjectsAsync</c> throws on a non-success page, which is
    /// right for a bucket that is failing and wrong for one that does not exist.</para>
    ///
    /// <para><b>What that cost.</b> <c>AccountDeletionGrain.PurgeExportArchivesAsync</c> is step 2 of
    /// ten and is deliberately allowed to fail its attempt, so on such an instance every account
    /// erasure threw there, three times, and then the poll was unregistered for good. The person could
    /// not cancel it either, and nothing on the instance said the feature was inoperable. The grain
    /// now asks this first and skips the purge with a log line; the flag is what makes that skip
    /// distinguishable from swallowing a real fault, so it is what is pinned here.</para>
    ///
    /// <para><b>Why the grain's skip is not driven end-to-end.</b> The integration host configures a
    /// real MinIO export bucket and one host is shared by every fixture in the process, so there is no
    /// way to make the deployment unconfigured for one test without breaking storage for whatever else
    /// is running — the same constraint
    /// <c>AccountDeletionTests.An_archive_purge_the_cursor_never_recorded_is_run_by_the_next_attempt</c>
    /// documents. What is testable without a seam is the flag itself, over an options value the
    /// service is constructed with, in both directions.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void ExportStorage_SaysWhetherThisDeploymentHasABucketAtAll()
    {
        var pool      = FactoryAsp.Services.GetRequiredService<IS3ClientPool>();
        var presigner = FactoryAsp.Services.GetRequiredService<S3PresignedUrlGenerator>();

        var configured = FactoryAsp.Services.GetRequiredService<IExportS3Service>();

        var unset = new ExportS3Service(pool, Options.Create(new StorageOptions { ExportBucketName = "" }), presigner);
        var blank = new ExportS3Service(pool, Options.Create(new StorageOptions { ExportBucketName = "   " }), presigner);

        Assert.Multiple(() =>
        {
            Assert.That(configured.IsConfigured, Is.True,
                "the host under test does configure an export bucket, so a false here means the flag " +
                "reads something other than the bucket name and every export call is about to be skipped");
            Assert.That(unset.IsConfigured, Is.False,
                "a deployment that never set Storage:ExportBucketName is reported as having an export " +
                "store, so its account erasures will fail at step 2 for ever");
            Assert.That(blank.IsConfigured, Is.False,
                "a bucket name of whitespace names nothing, and a config file copied by hand produces it");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestExport_ReturnsAResultRatherThanThrowing(CancellationToken ct = default)
    {
        // Whether an export can start depends on whether object storage is configured; the test host
        // has none, so the meaningful assertion is that the grain reports that cleanly instead of
        // faulting the caller.
        var (userId, _) = await RegisterAsync(ct);

        var result = await GetGrainFactory().GetGrain<IUserDataExportGrain>(userId).RequestExportAsync();

        Assert.That(result.Success || result.Error is not null, Is.True,
            "an unsuccessful export request must say why");
    }
}
