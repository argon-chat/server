namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Sockets;
using Argon.Api.Grains.Interfaces;
using Argon.Entities;
using Argon.Features.EF;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// The rules <c>FileStorageGrain</c> holds an upload to, purpose by purpose, from the signed URL to
/// the finished file.
/// </summary>
/// <remarks>
/// <para><c>MediaUploadTests</c> covers the round trip a client makes for an avatar and a channel
/// attachment. This fixture covers the rest of the grain's table: every purpose's size limit and the
/// key it files under, the content types a purpose accepts, what an attachment limit is raised by, and
/// every way a finalisation can be refused — expired, not yours, the file gone, the bytes too big.
/// </para>
///
/// <para>Driven through the grain, as <c>MediaUploadTests</c> drives its limits, because the grain is
/// where the rules live: <c>/api/files</c> and the Ion upload methods are pass-throughs keyed by the
/// caller. The limits are read from the host's bound <see cref="FileLimitsOptions"/> rather than
/// restated, so a change to the shipped numbers moves the tests with it.</para>
///
/// <para>Uploads go to the real MinIO container for the reason <c>MediaUploadTests</c> gives: the
/// server never sees the bytes, so the only evidence of what arrived is what the store reports.</para>
/// </remarks>
[TestFixture]
public class FileStorageRulesTests : TestBase
{
    /// <summary>A one-pixel PNG.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private TestUserSession owner = null!;
    private Guid            spaceId;
    private Guid            channelId;

    private static FileLimitsOptions Limits
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<IOptions<FileLimitsOptions>>().Value;

    [OneTimeSetUp]
    public async Task CreateSpaceAsync()
    {
        owner = await CreateSessionAsync();

        var created = await owner.Users.CreateSpace(new CreateServerRequest("File rules", "FileStorageRulesTests", string.Empty));

        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>(), "setup: could not create the space");

        spaceId = ((SuccessCreateSpace)created).space.spaceId;

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, "files", ChannelType.Text, "FileStorageRulesTests", null));

        var channels = await owner.Servers.GetChannels(spaceId);

        channelId = channels.Values.Single(c => c.channel.name == "files").channel.channelId;
    }

    // ── limits and keys ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every purpose is signed up to its own limit and not a byte further, and filed under a key that
    /// says whose it is.
    /// </summary>
    /// <remarks>
    /// <para>The limit is recorded on the blob, because it is what finalisation holds the real size
    /// to; the declared size only decides whether a URL is signed at all.</para>
    ///
    /// <para>The key is the other half. A space's emoji, banners and video live under the space
    /// (<c>s/{space}/…</c>, video under its channel as well), a direct attachment under its sender
    /// (<c>u/{user}/…</c>), and an avatar at the root under its bare id — which is what the flat-key
    /// option the host runs with means, and what the avatar redirect relies on.</para>
    /// </remarks>
    [TestCase(FilePurpose.Avatar)]
    [TestCase(FilePurpose.Emoji)]
    [TestCase(FilePurpose.Sticker)]
    [TestCase(FilePurpose.Banner)]
    [TestCase(FilePurpose.Video)]
    [TestCase(FilePurpose.InviteImage)]
    [TestCase(FilePurpose.Gif)]
    [TestCase(FilePurpose.DirectAttachment)]
    [CancelAfter(120_000)]
    public async Task Each_purpose_is_signed_up_to_its_own_limit_and_filed_under_its_owner(FilePurpose purpose, CancellationToken ct = default)
    {
        var user  = await CreateSessionAsync(ct);
        var grain = Files(user.UserId);
        var limit = LimitFor(purpose);

        Assert.That(
            async () => await grain.RequestUploadAsync(new FileUploadRequest(purpose, "application/octet-stream", limit + 1, spaceId, channelId), ct),
            Throws.InstanceOf<InvalidOperationException>(),
            $"a {purpose} one byte over its {limit}-byte limit was signed");

        var ticket = await grain.RequestUploadAsync(new FileUploadRequest(purpose, "application/octet-stream", limit, spaceId, channelId), ct);

        await using var db = await NewDbAsync(ct);

        var blob = await db.FileBlobs.AsNoTracking().SingleAsync(b => b.Id == ticket.BlobId, ct);
        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == ticket.FileId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(blob.SizeLimit, Is.EqualTo(limit), "the blob carries a limit other than the purpose's");
            Assert.That(blob.OwnerId, Is.EqualTo(user.UserId));
            Assert.That(file.Finalized, Is.False, "nothing has been uploaded yet");
            Assert.That(file.S3Key, Is.EqualTo(ExpectedKey(purpose, ticket.FileId, user.UserId)));
        });
    }

    /// <summary>
    /// An attachment limit is raised by the uploader's Ultima subscription.
    /// </summary>
    [TestCase(FilePurpose.ChannelAttachment)]
    [TestCase(FilePurpose.DirectAttachment)]
    [CancelAfter(120_000)]
    public async Task An_attachment_limit_is_raised_by_an_ultima_subscription(FilePurpose purpose, CancellationToken ct = default)
    {
        Assert.That(Limits.AttachmentUltimaMaxBytes, Is.GreaterThan(Limits.AttachmentBaseMaxBytes),
            "premise: Ultima raises the attachment limit in this configuration");

        var user  = await CreateSessionAsync(ct);
        var grain = Files(user.UserId);
        var size  = Limits.AttachmentUltimaMaxBytes;
        var space = purpose == FilePurpose.ChannelAttachment ? spaceId : (Guid?)null;

        Assert.That(
            async () => await grain.RequestUploadAsync(new FileUploadRequest(purpose, "video/mp4", size, space, channelId), ct),
            Throws.InstanceOf<InvalidOperationException>(),
            "an account without Ultima was signed the Ultima limit");

        await GetGrainFactory().GetGrain<IUltimaGrain>(user.UserId)
           .ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var ticket = await grain.RequestUploadAsync(new FileUploadRequest(purpose, "video/mp4", size, space, channelId), ct);

        Assert.That(await BlobLimitAsync(ticket.BlobId, ct), Is.EqualTo(size),
            "the subscriber's attachment was held to the base limit");
    }

    /// <summary>
    /// A channel attachment's limit is raised by the space's boost level, each level to its own
    /// ceiling.
    /// </summary>
    /// <remarks>
    /// The level is set on the space row, where <c>SpaceBoostGrain.RecalculateAsync</c> keeps it and
    /// where the storage grain reads it; reaching it through real boosts would take fourteen of them.
    /// </remarks>
    [TestCase(2)]
    [TestCase(3)]
    [CancelAfter(120_000)]
    public async Task A_channel_attachment_limit_follows_the_spaces_boost_level(int boostLevel, CancellationToken ct = default)
    {
        var boostedOwner = await CreateSessionAsync(ct);
        var boosted      = await CreateBoostedSpaceAsync(boostedOwner, boostLevel, ct);
        var grain        = Files(boostedOwner.UserId);
        var ceiling      = boostLevel >= 3 ? Limits.AttachmentBoostLevel3MaxBytes : Limits.AttachmentBoostLevel2MaxBytes;

        var ticket = await grain.RequestUploadAsync(
            new FileUploadRequest(FilePurpose.ChannelAttachment, "video/mp4", ceiling, boosted, Guid.NewGuid()), ct);

        Assert.That(await BlobLimitAsync(ticket.BlobId, ct), Is.EqualTo(ceiling),
            $"a level-{boostLevel} space's attachment was not given its level's limit");

        Assert.That(
            async () => await grain.RequestUploadAsync(
                new FileUploadRequest(FilePurpose.ChannelAttachment, "video/mp4", ceiling + 1, boosted, Guid.NewGuid()), ct),
            Throws.InstanceOf<InvalidOperationException>(),
            "the boosted limit is a ceiling too");
    }

    // ── content types ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a purpose accepts is decided by what the store received: images for the image purposes,
    /// video for video.
    /// </summary>
    [TestCase(FilePurpose.SpaceAvatar, "image/png", true)]
    [TestCase(FilePurpose.Emoji, "image/png", true)]
    [TestCase(FilePurpose.Sticker, "image/png", true)]
    [TestCase(FilePurpose.Banner, "image/png", true)]
    [TestCase(FilePurpose.Video, "video/mp4", true)]
    [TestCase(FilePurpose.Video, "image/png", false)]
    [TestCase(FilePurpose.Emoji, "text/html", false)]
    [CancelAfter(120_000)]
    public async Task A_purpose_accepts_only_the_content_it_is_for(FilePurpose purpose, string contentType, bool accepted, CancellationToken ct = default)
    {
        var user   = await CreateSessionAsync(ct);
        var grain  = Files(user.UserId);
        var ticket = await grain.RequestUploadAsync(new FileUploadRequest(purpose, contentType, Png.Length, spaceId, channelId), ct);

        await UploadAsync(ticket, Png, contentType);

        if (accepted)
        {
            var info = await grain.FinalizeUploadAsync(ticket.BlobId, ct);

            Assert.Multiple(() =>
            {
                Assert.That(info.ContentType, Is.EqualTo(contentType));
                Assert.That(info.FileSize, Is.EqualTo(Png.Length));
                Assert.That(info.Purpose, Is.EqualTo(purpose));
            });

            return;
        }

        Assert.That(async () => await grain.FinalizeUploadAsync(ticket.BlobId, ct), Throws.InstanceOf<InvalidOperationException>(),
            $"'{contentType}' was accepted as a {purpose}");

        await AssertDiscardedAsync(ticket, ct);
    }

    // ── refusals at finalisation ────────────────────────────────────────────────────────────────

    /// <summary>
    /// An upload finalised after its blob expired is cleaned up — row, blob and bytes — rather than
    /// accepted.
    /// </summary>
    /// <remarks>
    /// <para>The signed URL outlives nothing: a blob past its expiry is one the collector is entitled to
    /// sweep, and accepting it would race the sweep for a file the client was told it had lost.</para>
    ///
    /// <para>The collector's lease is held across the expiry and the finalisation, so the grain is the
    /// one that finds the expired blob: the host's collector, and <c>MediaUploadTests</c>' sweeps, would
    /// otherwise be entitled to take it first, and the test would be asserting on whoever won.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_upload_finalised_after_its_blob_expired_is_cleaned_up(CancellationToken ct = default)
    {
        var user   = await CreateSessionAsync(ct);
        var grain  = Files(user.UserId);
        var ticket = await grain.RequestUploadAsync(new FileUploadRequest(FilePurpose.Sticker, "image/png", Png.Length, spaceId), ct);

        await UploadAsync(ticket, Png, "image/png");

        await using (var holder = await NewDbAsync(ct))
        {
            await holder.Database.OpenConnectionAsync(ct);

            SchemaReconcileLease? lease;
            while ((lease = await SchemaReconcileLease.TryAcquireAsync(holder.Database.GetDbConnection(),
                       NullLogger.Instance, "file-rules-test", TimeSpan.FromMinutes(1), FileGcService.LockTable, ct)) is null)
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);

            await using (lease)
            {
                await using (var db = await NewDbAsync(ct))
                    await db.FileBlobs.Where(b => b.Id == ticket.BlobId)
                       .ExecuteUpdateAsync(s => s.SetProperty(b => b.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), ct);

                Assert.That(async () => await grain.FinalizeUploadAsync(ticket.BlobId, ct),
                    Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("expired"));
            }
        }

        await AssertDiscardedAsync(ticket, ct);
    }

    /// <summary>
    /// A blob is finalised only by the account that asked for it; anybody else, and any id that names
    /// nothing, is told it does not exist.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_blob_is_finalised_only_by_the_account_that_asked_for_it(CancellationToken ct = default)
    {
        var user     = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var ticket   = await Files(user.UserId).RequestUploadAsync(new FileUploadRequest(FilePurpose.Emoji, "image/png", Png.Length, spaceId), ct);

        await UploadAsync(ticket, Png, "image/png");

        Assert.Multiple(() =>
        {
            Assert.That(async () => await Files(user.UserId).FinalizeUploadAsync(Guid.NewGuid(), ct),
                Throws.InstanceOf<KeyNotFoundException>(), "an id that names no blob");
            Assert.That(async () => await Files(stranger.UserId).FinalizeUploadAsync(ticket.BlobId, ct),
                Throws.InstanceOf<KeyNotFoundException>(), "somebody else finalised the blob");
        });

        var info = await Files(user.UserId).FinalizeUploadAsync(ticket.BlobId, ct);

        Assert.That(info.FileId, Is.EqualTo(ticket.FileId), "the stranger's attempt cost the owner their upload");
    }

    /// <summary>A blob whose file has been collected cannot be finalised.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_blob_whose_file_was_collected_cannot_be_finalised(CancellationToken ct = default)
    {
        var user   = await CreateSessionAsync(ct);
        var grain  = Files(user.UserId);
        var ticket = await grain.RequestUploadAsync(new FileUploadRequest(FilePurpose.Banner, "image/png", Png.Length, spaceId), ct);

        await UploadAsync(ticket, Png, "image/png");

        // Soft-deleted, the way FileGcService and an account erasure leave a file row.
        await using (var db = await NewDbAsync(ct))
            await db.Files.Where(f => f.Id == ticket.FileId)
               .ExecuteUpdateAsync(s => s.SetProperty(f => f.IsDeleted, true), ct);

        Assert.That(async () => await grain.FinalizeUploadAsync(ticket.BlobId, ct), Throws.InstanceOf<KeyNotFoundException>(),
            "a file that has been collected came back through its blob");
    }

    /// <summary>
    /// Bytes larger than the blob allows are deleted at finalisation, however small the client said
    /// they would be.
    /// </summary>
    /// <remarks>
    /// The declared size is checked when the URL is signed and nothing holds the client to it
    /// afterwards; the store's own count, at finalisation, is the check that means anything.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Bytes_larger_than_the_blob_allows_are_deleted_at_finalisation(CancellationToken ct = default)
    {
        var user   = await CreateSessionAsync(ct);
        var grain  = Files(user.UserId);
        var ticket = await grain.RequestUploadAsync(new FileUploadRequest(FilePurpose.Emoji, "image/png", Png.Length, spaceId), ct);

        var oversized = new byte[Limits.EmojiMaxBytes + 1];
        Png.CopyTo(oversized, 0);

        await UploadAsync(ticket, oversized, "image/png");

        Assert.That(async () => await grain.FinalizeUploadAsync(ticket.BlobId, ct),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("exceeds"),
            "an emoji over its limit was accepted because the client declared it small");

        await AssertDiscardedAsync(ticket, ct);
    }

    // ── after finalisation ──────────────────────────────────────────────────────────────────────

    /// <summary>The owner can take another reference to their own file, and give it back.</summary>
    [Test, CancelAfter(120_000)]
    public async Task The_owner_can_take_and_release_another_reference(CancellationToken ct = default)
    {
        var user    = await CreateSessionAsync(ct);
        var grain   = Files(user.UserId);
        var counter = FactoryAsp.Services.GetRequiredService<IReferenceCountService>();
        var ticket  = await grain.RequestUploadAsync(new FileUploadRequest(FilePurpose.Sticker, "image/png", Png.Length, spaceId), ct);

        await UploadAsync(ticket, Png, "image/png");
        await grain.FinalizeUploadAsync(ticket.BlobId, ct);

        var initial = await counter.GetRefCountAsync(ticket.FileId, ct);

        await grain.IncrementRefAsync(ticket.FileId, ct);

        var retained = await counter.GetRefCountAsync(ticket.FileId, ct);

        await grain.DecrementRefAsync(ticket.FileId, ct);

        var released = await counter.GetRefCountAsync(ticket.FileId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(initial, Is.EqualTo(1));
            Assert.That(retained, Is.EqualTo(2), "the owner's own retain did not land");
            Assert.That(released, Is.EqualTo(1));
        });
    }

    /// <summary>
    /// A download address is handed out for a finished file, and for nothing else.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_download_address_exists_only_for_a_finished_file(CancellationToken ct = default)
    {
        var user     = await CreateSessionAsync(ct);
        var grain    = Files(user.UserId);
        var finished = await grain.RequestUploadAsync(new FileUploadRequest(FilePurpose.Banner, "image/png", Png.Length, spaceId), ct);
        var pending  = await grain.RequestUploadAsync(new FileUploadRequest(FilePurpose.Banner, "image/png", Png.Length, spaceId), ct);

        await UploadAsync(finished, Png, "image/png");

        var info = await grain.FinalizeUploadAsync(finished.BlobId, ct);

        var forFinished = await grain.GetDownloadUrlAsync(finished.FileId, ct);
        var forPending  = await grain.GetDownloadUrlAsync(pending.FileId, ct);
        var forNothing  = await grain.GetDownloadUrlAsync(Guid.NewGuid(), ct);

        Assert.Multiple(() =>
        {
            Assert.That(forFinished, Is.EqualTo(info.DownloadUrl));
            Assert.That(forFinished, Does.Contain(finished.FileId.ToString()));
            Assert.That(forPending, Is.Null, "an upload that never finished was given an address");
            Assert.That(forNothing, Is.Null);
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private IFileStorageGrain Files(Guid userId)
        => GetGrainFactory().GetGrain<IFileStorageGrain>(userId);

    private static long LimitFor(FilePurpose purpose) => purpose switch
    {
        FilePurpose.Avatar or FilePurpose.SpaceAvatar => Limits.AvatarMaxBytes,
        FilePurpose.Emoji                             => Limits.EmojiMaxBytes,
        FilePurpose.Sticker                           => Limits.StickerMaxBytes,
        FilePurpose.Banner                            => Limits.BannerMaxBytes,
        FilePurpose.Video                             => Limits.VideoMaxBytes,
        _                                             => Limits.AttachmentBaseMaxBytes
    };

    private string ExpectedKey(FilePurpose purpose, Guid fileId, Guid userId) => purpose switch
    {
        FilePurpose.Avatar            => fileId.ToString(),
        FilePurpose.Video             => $"s/{spaceId}/{purpose.S3Prefix()}/{channelId}/{fileId}",
        _ when purpose.IsSpaceScoped() => $"s/{spaceId}/{purpose.S3Prefix()}/{fileId}",
        _                             => $"u/{userId}/{purpose.S3Prefix()}/{fileId}"
    };

    private async Task<long> BlobLimitAsync(Guid blobId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        return await db.FileBlobs.AsNoTracking().Where(b => b.Id == blobId).Select(b => b.SizeLimit).SingleAsync(ct);
    }

    private async Task<Guid> CreateBoostedSpaceAsync(TestUserSession spaceOwner, int boostLevel, CancellationToken ct)
    {
        var created = await spaceOwner.Users.CreateSpace(new CreateServerRequest("Boosted", "FileStorageRulesTests", string.Empty), ct);

        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>(), "setup: could not create the boosted space");

        var boosted = ((SuccessCreateSpace)created).space.spaceId;

        await using var db = await NewDbAsync(ct);

        await db.Spaces.Where(s => s.Id == boosted)
           .ExecuteUpdateAsync(s => s.SetProperty(x => x.BoostLevel, boostLevel), ct);

        return boosted;
    }

    /// <summary>A refused upload leaves no finished file, no live blob and no bytes in the store.</summary>
    private async Task AssertDiscardedAsync(FileUploadResponse ticket, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);

        var file = await db.Files.IgnoreQueryFilters().AsNoTracking().SingleAsync(f => f.Id == ticket.FileId, ct);
        var blob = await db.FileBlobs.IgnoreQueryFilters().AsNoTracking().SingleAsync(b => b.Id == ticket.BlobId, ct);
        var head = await FactoryAsp.Services.GetRequiredService<IS3StorageService>().HeadFileAsync(file.S3Key, ct);

        Assert.Multiple(() =>
        {
            Assert.That(file.IsDeleted, Is.True, "the refused file's row is still live");
            Assert.That(file.Finalized, Is.False);
            Assert.That(blob.IsDeleted, Is.True, "the refused upload's blob is still live");
            Assert.That(head, Is.Null, "the refused bytes are still in the store");
        });
    }

    private async Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

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
    /// <remarks>See <c>MediaUploadTests.DirectToStore</c>: the host is signed into the URL.</remarks>
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
