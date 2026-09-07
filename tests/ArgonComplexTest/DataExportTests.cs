namespace ArgonComplexTest.Tests;

using System.Formats.Cbor;
using Argon.Features.Storage;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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

    /// <summary>The export bucket the host under test is actually running with, if it has one.</summary>
    /// <remarks>
    /// Read off the running host rather than assumed, because it is what decides which answer
    /// <c>RequestDataExport</c> owes: <c>StorageOptions.ExportBucketName</c> ships empty and
    /// <c>ObjectStorageHealthCheck</c> deliberately leaves an empty export bucket out of its probe, so
    /// a deployment with no export store is a supported shape rather than an outage — and one the
    /// grain has to name rather than accept.
    /// </remarks>
    private string ExportBucket
        => FactoryAsp.Services.GetRequiredService<IOptions<StorageOptions>>().Value.ExportBucketName;

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

    /// <summary>
    /// A request starts a job, or names the one thing that makes the feature inoperable — and which of
    /// the two is decided by the host's configuration rather than by whichever happens.
    /// </summary>
    /// <remarks>
    /// <para>This used to accept either answer, which made it a test of nothing: a deployment that had
    /// quietly lost its export bucket passed it. Both branches are now tied to
    /// <see cref="ExportBucket"/>. The suite runs with MinIO, so the branch this run takes is the
    /// configured one; the other is written out so that a host that lost the setting fails here, with
    /// a sentence, rather than three ticks later inside the object store.</para>
    ///
    /// <para><c>NOT_CONFIGURED</c> is the reason it is worth pinning at all. The member has been in
    /// <c>DataExportError</c>, and mapped on both surfaces, since before anything produced it: a
    /// deployment with no export bucket accepted the request, mailed the person to say their archive
    /// was being prepared, and then failed a tick later against a bucket with an empty name — with the
    /// account's own erasure inheriting the same failure and never completing. The refusal is what
    /// makes "this instance cannot do exports" something the product says out loud.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task RequestDataExport_EitherStartsAJobOrExplainsWhyNot(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var configured = !string.IsNullOrWhiteSpace(ExportBucket);
        var result     = await Security(scope.ServiceProvider).RequestDataExport(ct);

        if (!configured)
        {
            Assert.That(result, Is.InstanceOf<FailedRequestDataExport>(),
                "this host has no export bucket, so the request was accepted for a job that cannot be "
              + "written anywhere");
            Assert.That(((FailedRequestDataExport)result).error, Is.EqualTo(DataExportError.NOT_CONFIGURED),
                "a deployment with no export store has to say so, not fail as something else");

            return;
        }

        Assert.That(result, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the host exports to '{ExportBucket}' and the request was refused: "
          + $"{(result as FailedRequestDataExport)?.error}");

        var started = (SuccessRequestDataExport)result;

        Assert.That(started.exportId, Is.Not.EqualTo(Guid.Empty));

        var status = await Security(scope.ServiceProvider).GetDataExportStatus(ct);

        Assert.Multiple(() =>
        {
            Assert.That(status.status, Is.Not.EqualTo(DataExportStatusKind.IDLE));
            Assert.That(status.exportId, Is.EqualTo(started.exportId), "the status must describe the job just started");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task RequestDataExport_Twice_DoesNotStartASecondBuild(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var first = await Security(scope.ServiceProvider).RequestDataExport(ct);

        // Asserted rather than skipped over. The host this suite runs against configures an export
        // bucket, so a refusal here is a fault in the surface under test and not an environment to
        // step around — RequestDataExport_EitherStartsAJobOrExplainsWhyNot is where the deployment
        // without one is described.
        Assert.That(first, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the first export was refused ({(first as FailedRequestDataExport)?.error}), so there is no "
          + "running job for a second request to be refused against");

        var started = (SuccessRequestDataExport)first;
        var second  = await Security(scope.ServiceProvider).RequestDataExport(ct);

        // Two archives of the same account built at once is wasted work at best, and the second
        // would overwrite the first's url mid-download.
        Assert.That(second, Is.InstanceOf<FailedRequestDataExport>());
        Assert.That(((FailedRequestDataExport)second).error,
            Is.EqualTo(DataExportError.ALREADY_IN_PROGRESS).Or.EqualTo(DataExportError.RATE_LIMITED));

        var status = await Security(scope.ServiceProvider).GetDataExportStatus(ct);
        Assert.That(status.exportId, Is.EqualTo(started.exportId), "the refusal must not have replaced the running job");
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

    /// <summary>
    /// <c>DataExportStatus</c> is six elements on the wire, and the seventh ordinal stays retired.
    /// </summary>
    /// <remarks>
    /// <para>An ion <c>msg</c> body is a positional CBOR array, so a field's index <em>is</em> its
    /// identity and the index is the field's declaration order. <c>totalItemsEstimate</c> was
    /// dropped from ordinal 6 (defect X8: it was assigned zero on every request and never computed,
    /// so a client dividing by it rendered "N of 0"), and the ordinal has to stay empty. If a later
    /// change declares a seventh field it takes 6 back, and a client built against the old schema
    /// decodes whatever that field is as <c>i4</c> and reports nothing — a silent type confusion
    /// rather than the loud arity error a shortened array gives.</para>
    ///
    /// <para>This is the only enforcement there is, which is why it is a test and not a lock entry
    /// (finding R13). <c>ionc</c> has no reservation syntax — the diagnostic that suggests one names
    /// a facility the grammar does not have — indices are recomputed from the declaration order on
    /// every compile, and <c>ion.lock.json</c>'s <c>nextIndex</c> is recomputed from the field list
    /// with it, so the lock cannot record a retired ordinal and a hand-edit to it is undone by the
    /// next regeneration. Asserted through the generated formatter rather than by reflecting over
    /// the record, because the array length is the thing an old client actually breaks on.</para>
    ///
    /// <para>When this goes red the answer is not to change the number. It is a schema revision that
    /// the release gates on a minimum client version — see <c>docs/internal/release/wire-compatibility.md</c>,
    /// "<c>DataExportStatus</c> lost a field", for what breaks in a mixed deployment and what the
    /// rollout has to do about it.</para>
    /// </remarks>
    [Test]
    public void The_data_export_status_stays_a_six_field_message()
    {
        var writer = new CborWriter();

        IonFormatterStorage.GetFormatter<DataExportStatus>()
           .Write(writer, new DataExportStatus(DataExportStatusKind.IDLE, null, null, null, null, 3));

        var reader = new CborReader(writer.Encode());

        Assert.That(reader.ReadStartArray(), Is.EqualTo(6),
            "DataExportStatus changed arity. Every shipped client reads it as a fixed-length array and " +
            "a seventh field would take back the ordinal totalItemsEstimate vacated, so an old client " +
            "would decode the new field as that i4 without an error. This needs a coordinated release, " +
            "not a bigger number here.");
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
