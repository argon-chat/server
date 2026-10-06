namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Data.Common;
using Argon.Api.Grains.Interfaces;
using Argon.Entities;
using Argon.Features.EF;
using Argon.Features.Storage;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Shared objects under files: two uploads of the same bytes end up on one object, a copy of a file
/// is a new file over the same object, and the object lives exactly as long as some file names it.
/// </summary>
/// <remarks>
/// <para>Against the real store, like <c>MediaUploadTests</c>: the verifier's evidence is what it reads
/// back from S3, and the last word on a merge is whether the duplicate's object is gone.</para>
///
/// <para>Every test uploads bytes of its own — the one-pixel PNG with a random tail — so no test
/// meets another's canonical copy, and no other fixture's one-pixel attachment meets ours. The sweeps
/// are driven by hand through <see cref="FileGcService"/>, which takes the collector's lease each time;
/// a pass that lost the lease to the background loop does nothing, so every expectation is polled
/// over several passes rather than asserted after one.</para>
/// </remarks>
[TestFixture]
public class FileDedupTests : TestBase
{
    /// <summary>A one-pixel PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private TestUserSession owner = null!;
    private Guid            spaceId;
    private Guid            channelId;

    private FileGcService Gc => FactoryAsp.Services.GetServices<IHostedService>().OfType<FileGcService>().Single();

    private IS3StorageService Store => FactoryAsp.Services.GetRequiredService<IS3StorageService>();

    [OneTimeSetUp]
    public async Task CreateSpaceAsync()
    {
        owner = await CreateSessionAsync();

        var created = await owner.Users.CreateSpace(new CreateServerRequest("Dedup", "FileDedupTests", string.Empty));

        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>(), "setup: could not create the space");

        spaceId = ((SuccessCreateSpace)created).space.spaceId;

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, "files", ChannelType.Text, "FileDedupTests", null)).Ok();

        var channels = await owner.Servers.GetChannels(spaceId);

        channelId = channels.Values.Single(c => c.channel.name == "files").channel.channelId;
    }

    // ── convergence ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The same bytes uploaded twice, with no claim from either client, become one object: the second
    /// file is pointed at the first's blob once the verifier has read both back, and the second object
    /// is removed after its delay.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Two_uploads_of_the_same_bytes_converge_on_one_object(CancellationToken ct = default)
    {
        var bytes = Fresh();

        var first  = await StoreAsync(bytes, ct: ct);
        var second = await StoreAsync(bytes, ct: ct);

        var (firstRow, secondRow) = (await FileRowAsync(first.FileId, ct), await FileRowAsync(second.FileId, ct));

        Assert.Multiple(() =>
        {
            Assert.That(firstRow.BlobId, Is.Not.Null, "a finalized file has no blob");
            Assert.That(secondRow.BlobId, Is.Not.Null.And.Not.EqualTo(firstRow.BlobId),
                "the second upload was merged before anything had read the bytes back");
        });

        await UntilAsync(async () => (await FileRowAsync(second.FileId, ct)).BlobId == firstRow.BlobId,
            () => Gc.VerifyBlobsAsync(ct), "the verifier merging the second upload into the first", ct);

        var duplicate = await BlobAsync(secondRow.BlobId!.Value, ct);
        var canonical = await BlobAsync(firstRow.BlobId!.Value, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(canonical.Sha256, Is.EqualTo(SHA256.HashData(bytes)), "the canonical copy carries a hash the server did not compute");
            Assert.That(canonical.Links, Is.EqualTo(2));
            Assert.That(duplicate.CanonicalId, Is.EqualTo(canonical.Id));
            Assert.That(duplicate.DeleteAfter, Is.Not.Null.And.GreaterThan(DateTimeOffset.UtcNow),
                "the duplicate's object is due for deletion before the redirect cache can have forgotten its key");
            Assert.That(await Store.HeadFileAsync(duplicate.S3Key, ct), Is.Not.Null, "the duplicate object went before its delay");
        });

        await DueNowAsync(duplicate.Id, ct);

        await UntilAsync(async () => (await BlobAsync(duplicate.Id, ct)).IsDeleted,
            () => Gc.SweepObjectsAsync(ct), "the object sweep removing the duplicate", ct);

        Assert.Multiple(async () =>
        {
            Assert.That(await Store.HeadFileAsync(duplicate.S3Key, ct), Is.Null, "the duplicate object is still in the store");
            Assert.That(await Store.HeadFileAsync(canonical.S3Key, ct), Is.Not.Null, "the canonical object went with the duplicate");
        });
    }

    /// <summary>
    /// A client that says what its bytes hash to, and is right about it, is merged at finalize without
    /// the object being read back.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task A_true_claim_merges_at_finalize_without_a_read_back(CancellationToken ct = default)
    {
        var bytes     = Fresh();
        var canonical = await CanonicalAsync(bytes, ct);

        var claimed = await StoreAsync(bytes, claim: SHA256.HashData(bytes), ct: ct);
        var row     = await FileRowAsync(claimed.FileId, ct);

        Assert.That(row.BlobId, Is.EqualTo(canonical.Id), "a true claim against a verified copy was not enough to merge on the spot");

        var own = await OwnBlobOfAsync(claimed, canonical.Id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(own.CanonicalId, Is.EqualTo(canonical.Id));
            Assert.That(own.VerifyRequestedAt, Is.Null, "the bytes were queued for hashing although the claim settled it");
        });
    }

    /// <summary>
    /// A claim decides nothing on its own: a wrong claim over the right bytes is still merged once the
    /// bytes are read, and the right claim over the wrong bytes is not merged at all.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task The_bytes_decide_when_the_claim_disagrees(CancellationToken ct = default)
    {
        var bytes     = Fresh();
        var other     = Fresh();
        var canonical = await CanonicalAsync(bytes, ct);

        var misclaimed = await StoreAsync(bytes, claim: SHA256.HashData(other), ct: ct);
        var forged     = await StoreAsync(other, claim: SHA256.HashData(bytes), ct: ct);

        var misclaimedRow = await FileRowAsync(misclaimed.FileId, ct);
        var forgedRow     = await FileRowAsync(forged.FileId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(misclaimedRow.BlobId, Is.Not.EqualTo(canonical.Id), "a wrong claim merged without the bytes being read");
            Assert.That((await BlobAsync(misclaimedRow.BlobId!.Value, ct)).VerifyRequestedAt, Is.Not.Null,
                "the right bytes under a wrong claim were not queued for hashing");
            Assert.That(forgedRow.BlobId, Is.Not.EqualTo(canonical.Id), "a claim alone moved a file onto somebody else's bytes");
            Assert.That((await BlobAsync(forgedRow.BlobId!.Value, ct)).VerifyRequestedAt, Is.Null,
                "bytes whose MD5 matches nothing were queued for hashing on the strength of a claim");
        });

        await UntilAsync(async () => (await FileRowAsync(misclaimed.FileId, ct)).BlobId == canonical.Id,
            () => Gc.VerifyBlobsAsync(ct), "the verifier merging the misclaimed upload by its bytes", ct);

        Assert.That((await FileRowAsync(forged.FileId, ct)).BlobId, Is.EqualTo(forgedRow.BlobId), "the forged upload was merged after all");
    }

    // ── links ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A link is a file of its own — id, name, counter — over the source's object, and the object stays
    /// until the last file over it is collected.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task A_link_is_a_new_file_and_the_object_outlives_the_original(CancellationToken ct = default)
    {
        var bytes  = Fresh();
        var source = await StoreAsync(bytes, ct: ct);

        var linked = await Files(owner.UserId).LinkAsync(source.FileId, Attachment(), "copy.png", ct);

        Assert.That(linked.IsSuccess, Is.True, "the owner's own file could not be linked");

        var link      = linked.Value;
        var sourceRow = await FileRowAsync(source.FileId, ct);
        var linkRow   = await FileRowAsync(link.FileId, ct);
        var blob      = await BlobAsync(sourceRow.BlobId!.Value, ct);

        Assert.Multiple(() =>
        {
            Assert.That(link.FileId, Is.Not.EqualTo(source.FileId));
            Assert.That(link.FileName, Is.EqualTo("copy.png"));
            Assert.That(link.FileSize, Is.EqualTo(bytes.Length));
            Assert.That(link.S3Key, Is.EqualTo(source.S3Key), "the link does not name the source's object");
            Assert.That(linkRow.BlobId, Is.EqualTo(sourceRow.BlobId));
            Assert.That(linkRow.OwnerId, Is.EqualTo(owner.UserId));
            Assert.That(linkRow.ChannelId, Is.EqualTo(channelId));
            Assert.That(blob.Links, Is.EqualTo(2));
        });

        await ReleaseAsync(source.FileId, ct);

        await UntilAsync(async () => (await FileRowAsync(source.FileId, ct)).IsDeleted,
            async () => { await Gc.SweepOrphanFilesAsync(ct); await Gc.SweepObjectsAsync(ct); },
            "the collector taking the original file", ct);

        blob = await BlobAsync(blob.Id, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(blob.Links, Is.EqualTo(1));
            Assert.That(blob.DeleteAfter, Is.Null, "the object was scheduled for deletion while a link still names it");
            Assert.That(await Store.HeadFileAsync(blob.S3Key, ct), Is.Not.Null, "the object went with the original although the link was live");
        });

        await ReleaseAsync(link.FileId, ct);

        await UntilAsync(async () => (await BlobAsync(blob.Id, ct)).IsDeleted,
            async () => { await Gc.SweepOrphanFilesAsync(ct); await Gc.SweepObjectsAsync(ct); },
            "the collector taking the last file and the object", ct);

        Assert.That(await Store.HeadFileAsync(blob.S3Key, ct), Is.Null, "the object is still in the store with no file over it");
    }

    /// <summary>
    /// Expression uploads keep an object each — the grain writes the re-encoded bytes back over the key —
    /// so two identical stickers do not converge and neither can be linked from.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task Expression_objects_are_never_shared(CancellationToken ct = default)
    {
        var bytes  = Fresh();
        var first  = await StoreAsync(bytes, FilePurpose.Sticker, ct: ct);
        var second = await StoreAsync(bytes, FilePurpose.Sticker, ct: ct);

        var firstBlob  = await BlobAsync((await FileRowAsync(first.FileId, ct)).BlobId!.Value, ct);
        var secondBlob = await BlobAsync((await FileRowAsync(second.FileId, ct)).BlobId!.Value, ct);

        var linked = await Files(owner.UserId).LinkAsync(first.FileId, Attachment(), null, ct);

        Assert.Multiple(() =>
        {
            Assert.That(firstBlob.Dedupable, Is.False);
            Assert.That(secondBlob.Id, Is.Not.EqualTo(firstBlob.Id));
            Assert.That(secondBlob.VerifyRequestedAt, Is.Null, "a sticker was queued for hashing");
            Assert.That(linked.IsSuccess, Is.False, "a sticker's object was linked into a channel");
            Assert.That(linked.Error, Is.EqualTo(AttachExistingFileError.SOURCE_NOT_FOUND));
        });
    }

    /// <summary>Links land in chats; nothing else is a target.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_link_goes_into_a_chat_and_nowhere_else(CancellationToken ct = default)
    {
        var source = await StoreAsync(Fresh(), ct: ct);

        Assert.That(() => Files(owner.UserId).LinkAsync(source.FileId, new FileUploadRequest(FilePurpose.Banner, "", 0, spaceId), null, ct),
            Throws.InstanceOf<InvalidOperationException>());
    }

    [Test, CancelAfter(120_000)]
    public async Task A_referenced_due_batch_is_recounted_with_one_grouped_query(CancellationToken ct = default)
    {
        await using var holder = await NewDbAsync(ct);
        await using var lease = await AcquireGcLeaseAsync(holder, ct);
        await using var db = await NewDbAsync(ct);
        var blobs = Enumerable.Range(0, 4).Select(i => new BlobEntity
        {
            S3Key = $"dedup-batch-test/{Guid.NewGuid()}", Links = 0,
            DeleteAfter = new DateTimeOffset(2000, 1, 1, 0, 0, i, TimeSpan.Zero),
        }).ToArray();
        db.Blobs.AddRange(blobs);
        foreach (var blob in blobs)
            db.Files.Add(SweepFile(blob.Id));
        db.Files.Add(SweepFile(blobs[0].Id));
        await db.SaveChangesAsync(ct);

        var observer = new SweepReadObserver();
        var store = new SweepStorage();
        var dedup = ObservedDedup(db, observer, store);
        var removed = await dedup.SweepDeletableAsync(blobs.Length, ct);

        db.ChangeTracker.Clear();
        var ids = blobs.Select(b => b.Id).ToArray();
        var rows = await db.Blobs.Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.Zero);
            Assert.That(store.DeletedKeys, Is.Empty);
            Assert.That(observer.ReadCommands, Is.EqualTo(2), "one due-batch query and one live-count query suffice");
            Assert.That(observer.GroupedCounts, Is.EqualTo(1));
            Assert.That(rows, Has.Count.EqualTo(4));
            Assert.That(rows.All(b => b.DeleteAfter == null), Is.True);
            Assert.That(rows.Single(b => b.Id == blobs[0].Id).Links, Is.EqualTo(2));
            Assert.That(rows.Where(b => b.Id != blobs[0].Id).All(b => b.Links == 1), Is.True);
        });
    }

    [TestCase(false), TestCase(true), CancelAfter(120_000)]
    public async Task The_sweep_ignores_tombstones_but_checks_new_live_files_before_deleting(bool lateReference, CancellationToken ct = default)
    {
        await using var holder = await NewDbAsync(ct);
        await using var lease = await AcquireGcLeaseAsync(holder, ct);
        await using var db = await NewDbAsync(ct);
        var first = new BlobEntity { S3Key = $"dedup-guard-test/{Guid.NewGuid()}", Links = 99,
            DeleteAfter = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        var second = new BlobEntity { S3Key = $"dedup-guard-test/{Guid.NewGuid()}", Links = 99,
            DeleteAfter = first.DeleteAfter.Value.AddSeconds(1) };
        db.Blobs.AddRange(first, second);
        db.Files.Add(SweepFile(second.Id) with { IsDeleted = true });
        await db.SaveChangesAsync(ct);

        var observer = new SweepReadObserver();
        var store = new SweepStorage();
        if (lateReference)
            store.OnDelete = async key =>
            {
                if (key != first.S3Key) return;
                await using var raced = await NewDbAsync(ct);
                raced.Files.Add(SweepFile(second.Id));
                await raced.SaveChangesAsync(ct);
            };
        var removed = await ObservedDedup(db, observer, store).SweepDeletableAsync(2, ct);

        var secondRow = await BlobAsync(second.Id, ct);
        Assert.Multiple(() =>
        {
            Assert.That(removed, Is.EqualTo(lateReference ? 1 : 2));
            Assert.That(store.DeletedKeys, Is.EquivalentTo(lateReference ? new[] { first.S3Key } : new[] { first.S3Key, second.S3Key }));
            Assert.That(observer.GroupedCounts, Is.EqualTo(1));
            Assert.That(secondRow.IsDeleted, Is.EqualTo(!lateReference));
            if (lateReference)
            {
                Assert.That(secondRow.Links, Is.EqualTo(1));
                Assert.That(secondRow.DeleteAfter, Is.Null);
            }
        });
    }

    private static async Task<SchemaReconcileLease> AcquireGcLeaseAsync(ApplicationDbContext holder, CancellationToken ct)
    {
        // The hosted collector must not consume the synthetic due rows before the observed sweep.
        // Hold its own lease through setup, sweep and assertions, on a separate open connection.
        await holder.Database.OpenConnectionAsync(ct);
        SchemaReconcileLease? lease;
        while ((lease = await SchemaReconcileLease.TryAcquireAsync(holder.Database.GetDbConnection(),
                   NullLogger.Instance, "file-dedup-sweep-test", TimeSpan.FromMinutes(5), FileGcService.LockTable, ct)) is null)
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        return lease;
    }

    private FileEntity SweepFile(Guid blobId) => new()
    {
        BlobId = blobId, OwnerId = owner.UserId, Purpose = FilePurpose.ChannelAttachment,
        BucketName = "dedup-sweep-test", Finalized = true,
    };

    private BlobDedupService ObservedDedup(ApplicationDbContext source, SweepReadObserver observer, SweepStorage store)
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(
            (DbContextOptions<ApplicationDbContext>)source.GetService<IDbContextOptions>()).AddInterceptors(observer).Options;
        return new(new SweepDbFactory(options, FactoryAsp.Services.GetRequiredService<IOptions<DatabaseRegionOptions>>()),
            store, Options.Create(new DedupOptions()), NullLogger<BlobDedupService>.Instance);
    }

    private sealed class SweepDbFactory(DbContextOptions<ApplicationDbContext> options,
        IOptions<DatabaseRegionOptions> regions) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options, regions);
    }

    private sealed class SweepReadObserver : DbCommandInterceptor
    {
        public int ReadCommands { get; private set; }
        public int GroupedCounts { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                ReadCommands++;
            if (command.CommandText.Contains("GROUP BY", StringComparison.OrdinalIgnoreCase))
                GroupedCounts++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class SweepStorage : IS3StorageService
    {
        public List<string> DeletedKeys { get; } = [];
        public Func<string, Task>? OnDelete { get; set; }
        public async Task<bool> DeleteFileAsync(string objectKey, CancellationToken ct = default)
        {
            DeletedKeys.Add(objectKey);
            if (OnDelete is { } callback) await callback(objectKey);
            return true;
        }
        public Task<bool> FileExistsAsync(string objectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<S3FileMetadata?> HeadFileAsync(string objectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream?> GetObjectStreamAsync(string objectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream?> OpenReadAsync(string objectKey, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream?> OpenReadRangeAsync(string objectKey, long from, long toInclusive, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> CreateMultipartUploadAsync(string objectKey, string contentType, string? cacheControl, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> CompleteMultipartUploadAsync(string objectKey, string uploadId, IReadOnlyList<(int PartNumber, string ETag)> parts,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> AbortMultipartUploadAsync(string objectKey, string uploadId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> PutObjectAsync(string objectKey, Stream content, string? contentType = null, string? cacheControl = null,
            CancellationToken ct = default) => throw new NotSupportedException();
        public string GetFileDownloadUrl(Guid fileId) => throw new NotSupportedException();
        public string GetDownloadUrl(string objectKey) => throw new NotSupportedException();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bytes nobody else has: the PNG with a random tail, which every store accepts and nothing decodes.</summary>
    private static byte[] Fresh()
        => [..Png, ..RandomNumberGenerator.GetBytes(16)];

    private FileUploadRequest Attachment()
        => new(FilePurpose.ChannelAttachment, "", 0, spaceId, channelId);

    private IFileStorageGrain Files(Guid userId)
        => GetGrainFactory().GetGrain<IFileStorageGrain>(userId);

    private async Task<FileInfoResponse> StoreAsync(byte[] bytes, FilePurpose purpose = FilePurpose.ChannelAttachment, byte[]? claim = null,
        CancellationToken ct = default)
    {
        var grain  = Files(owner.UserId);
        var ticket = await grain.RequestUploadAsync(
            new FileUploadRequest(purpose, "image/png", bytes.Length, spaceId, purpose == FilePurpose.ChannelAttachment ? channelId : null,
                ClaimedSha256: claim), ct);

        await UploadAsync(ticket, bytes, "image/png");

        return await grain.FinalizeUploadAsync(ticket.BlobId, ct);
    }

    /// <summary>A verified canonical copy of the bytes: two uploads, then the verifier over both.</summary>
    private async Task<BlobEntity> CanonicalAsync(byte[] bytes, CancellationToken ct)
    {
        var first  = await StoreAsync(bytes, ct: ct);
        var second = await StoreAsync(bytes, ct: ct);
        var blobId = (await FileRowAsync(first.FileId, ct)).BlobId!.Value;

        await UntilAsync(async () => (await FileRowAsync(second.FileId, ct)).BlobId == blobId,
            () => Gc.VerifyBlobsAsync(ct), "the verifier settling a canonical copy", ct);

        return await BlobAsync(blobId, ct);
    }

    /// <summary>The blob an upload was finalized onto before it was merged: the one whose canonical is the given blob and whose key is the upload's.</summary>
    private async Task<BlobEntity> OwnBlobOfAsync(FileInfoResponse upload, Guid canonicalId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        return await db.Blobs.IgnoreQueryFilters().AsNoTracking()
           .SingleAsync(b => b.S3Key == upload.S3Key && b.CanonicalId == canonicalId, ct);
    }

    /// <summary>Takes the file's own reference away and back-dates the counter past the collector's grace period.</summary>
    private async Task ReleaseAsync(Guid fileId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        await db.FileCounters
           .Where(c => c.Id == fileId)
           .ExecuteUpdateAsync(s => s
               .SetProperty(c => c.RefCount, 0L)
               .SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow.AddDays(-1)), ct);
    }

    private async Task DueNowAsync(Guid blobId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        await db.Blobs
           .Where(b => b.Id == blobId)
           .ExecuteUpdateAsync(s => s.SetProperty(b => b.DeleteAfter, DateTimeOffset.UtcNow.AddMinutes(-1)), ct);
    }

    private async Task<FileEntity> FileRowAsync(Guid id, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Files.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == id, ct);
    }

    private async Task<BlobEntity> BlobAsync(Guid id, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Blobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == id, ct);
    }

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

    /// <summary>Runs a sweep until the condition holds; a pass can lose the lease to the loop, so one is never enough.</summary>
    private static async Task UntilAsync(Func<Task<bool>> done, Func<Task> pass, string what, CancellationToken ct, int attempts = 40)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            await pass();

            if (await done())
                return;

            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }

        Assert.Fail($"{what} did not happen in {attempts} passes");
    }

    /// <summary>Sends the bytes to the signed URL with exactly the fields it was signed with.</summary>
    private static async Task UploadAsync(FileUploadResponse ticket, byte[] payload, string contentType)
    {
        using var client  = DirectToStore();
        using var content = new ByteArrayContent(payload);

        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        foreach (var (key, value) in ticket.Fields)
            content.Headers.TryAddWithoutValidation(key, value);

        using var response = await client.PutAsync(ticket.Url, content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"the object store refused the presigned upload: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>An HTTP client that dials the store's mapped port whatever host the URL names.</summary>
    private static HttpClient DirectToStore()
    {
        var port = int.Parse(ArgonTestEnvironment.Instance.S3Endpoint.Split(':')[1]);

        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                await socket.ConnectAsync(IPAddress.Loopback, port, token);

                return new NetworkStream(socket, ownsSocket: true);
            }
        });
    }
}
