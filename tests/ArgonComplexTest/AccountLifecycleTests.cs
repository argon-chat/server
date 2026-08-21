namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

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

        var requested = await grain.RequestDeletionAsync(password);
        Assert.Multiple(() =>
        {
            Assert.That(requested.Success, Is.True, requested.Error?.ToString());
            Assert.That(requested.ScheduledDeletionAt, Is.Not.Null);
            Assert.That(requested.ScheduledDeletionAt, Is.GreaterThan(DateTimeOffset.UtcNow.AddDays(29)),
                "AccountDeletion:GracePeriodDays is 30 in the test configuration");
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
