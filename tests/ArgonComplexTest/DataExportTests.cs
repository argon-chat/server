namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// «Экспорт данных» on the privacy screen — asking for an archive of everything the account holds.
/// </summary>
/// <remarks>
/// <para>The work outlives the request by minutes, so this is a job and not a download: ask once,
/// poll, then follow the url. That shape is what the tests below pin — a request that answers with
/// an id, a status that can be read back, and a second request that is refused rather than quietly
/// starting a duplicate build.</para>
///
/// <para>The archive's contents are the export grain's business and covered where that lives. What
/// matters here is that the ion surface reports the job faithfully, including the refusals, since a
/// client with no way to tell "already running" from "failed" would poll forever or retry forever.</para>
/// </remarks>
[TestFixture]
public class DataExportTests : TestBase
{
    private ISecurityInteraction Security(IServiceProvider provider)
        => IonClient.ForService<ISecurityInteraction>(provider);

    [Test, CancelAfter(120_000)]
    public async Task GetDataExportStatus_ForAFreshAccount_IsIdle(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var status = await Security(scope.ServiceProvider).GetDataExportStatus(ct);

        Assert.Multiple(() =>
        {
            Assert.That(status.status, Is.EqualTo(DataExportStatusKind.IDLE));
            Assert.That(status.downloadUrl, Is.Null);
            Assert.That(status.exportId, Is.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDataExport_EitherStartsAJobOrExplainsWhyNot(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var result = await Security(scope.ServiceProvider).RequestDataExport(ct);

        // Storage is not configured in every test environment, and NOT_CONFIGURED is a legitimate
        // answer rather than a failure of this surface. Both branches are asserted so the test says
        // something either way instead of being skipped.
        if (result is SuccessRequestDataExport started)
        {
            Assert.That(started.exportId, Is.Not.EqualTo(Guid.Empty));

            var status = await Security(scope.ServiceProvider).GetDataExportStatus(ct);

            Assert.That(status.status, Is.Not.EqualTo(DataExportStatusKind.IDLE));
            Assert.That(status.exportId, Is.EqualTo(started.exportId), "the status must describe the job just started");
        }
        else
        {
            Assert.That(((FailedRequestDataExport)result).error, Is.EqualTo(DataExportError.NOT_CONFIGURED));
        }
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDataExport_Twice_DoesNotStartASecondBuild(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var first = await Security(scope.ServiceProvider).RequestDataExport(ct);

        if (first is not SuccessRequestDataExport started)
            Assert.Ignore("data export storage is not configured in this environment");
        else
        {
            var second = await Security(scope.ServiceProvider).RequestDataExport(ct);

            // Two archives of the same account built at once is wasted work at best, and the second
            // would overwrite the first's url mid-download.
            Assert.That(second, Is.InstanceOf<FailedRequestDataExport>());
            Assert.That(((FailedRequestDataExport)second).error,
                Is.EqualTo(DataExportError.ALREADY_IN_PROGRESS).Or.EqualTo(DataExportError.RATE_LIMITED));

            var status = await Security(scope.ServiceProvider).GetDataExportStatus(ct);
            Assert.That(status.exportId, Is.EqualTo(started.exportId), "the refusal must not have replaced the running job");
        }
    }

    [Test, CancelAfter(120_000)]
    public async Task CancelDataExport_WithNothingRunning_IsHarmless(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        // The screen has one button and no way to know whether a job is live, so cancelling into
        // the void has to be a no-op rather than an error.
        Assert.That(async () => await Security(scope.ServiceProvider).CancelDataExport(ct), Throws.Nothing);

        var status = await Security(scope.ServiceProvider).GetDataExportStatus(ct);
        Assert.That(status.status, Is.EqualTo(DataExportStatusKind.IDLE));
    }

    [Test, CancelAfter(120_000)]
    public async Task DataExport_IsScopedToTheCaller(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var stranger = await CreateSessionAsync(ct);
        await stranger.Security.RequestDataExport(ct);

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        // No id is accepted from the client anywhere in this flow, and this is why: the export is
        // keyed by the caller, so another account's archive is not addressable at all.
        var mine = await Security(scope.ServiceProvider).GetDataExportStatus(ct);

        Assert.That(mine.status, Is.EqualTo(DataExportStatusKind.IDLE));
        Assert.That(mine.downloadUrl, Is.Null);
    }
}
