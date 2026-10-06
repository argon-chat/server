namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Web;
using Argon.Api.Grains.Interfaces;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using ArgonSharedLogicTest.Storage;
using ion.runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

/// <summary>
/// Video attachments: the client declares the MP4 it made, uploads it in parts, and the server checks
/// the declaration against the container header and keeps the media record.
/// </summary>
/// <remarks>
/// Against the real store, like <c>PrepareUploadTests</c>: what arrived is only known from what the
/// store reports, and a multipart upload only exists there. Every video is a multipart upload; the host
/// makes it one part up to 1 MiB and 5 MiB parts above (<c>ArgonServerTargetHost</c>), so a two-part
/// file is a few megabytes. Every file carries random mdat bytes, so no test meets another's object
/// through dedup. A test that leaves a ticket open aborts it: an account holds only a few at a time.
/// </remarks>
[TestFixture]
public class VideoUploadTests : TestBase
{
    private TestUserSession owner  = null!;
    private TestUserSession member = null!;
    private Guid            spaceId;
    private Guid            channelA;
    private Guid            channelB;

    /// <summary>Two parts in the test host.</summary>
    private const int TwoPartMdat = 5 * 1024 * 1024 + 64 * 1024;

    private static FileLimitsOptions Limits
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<IOptions<FileLimitsOptions>>().Value;

    private IS3StorageService Store => FactoryAsp.Services.GetRequiredService<IS3StorageService>();

    private FileGcService Gc => FactoryAsp.Services.GetServices<IHostedService>().OfType<FileGcService>().Single();

    [OneTimeSetUp]
    public async Task CreateSpaceAsync()
    {
        owner  = await CreateSessionAsync();
        member = await CreateSessionAsync();

        spaceId  = await SpaceGroupSupport.CreateSpaceAsync(owner, "Videos");
        channelA = await ChannelTestKit.CreateChannelAsync(owner, spaceId, "a", ChannelType.Text, CancellationToken.None);
        channelB = await ChannelTestKit.CreateChannelAsync(owner, spaceId, "b", ChannelType.Text, CancellationToken.None);

        await SpaceGroupSupport.JoinAsync(owner, member, spaceId);
    }

    // ── the happy paths ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A small faststart MP4 goes up as one signed part and comes back as a media record of what its
    /// header says plus the poster, storyboard and preload hint declared.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_small_video_goes_up_as_one_part_and_is_kept_with_its_header(CancellationToken ct = default)
    {
        var poster     = await UploadImageAsync(owner, channelA, ct);
        var storyboard = await UploadImageAsync(owner, channelA, ct);
        var mp4        = new Mp4Builder { MdatSize = 4096 }.Build();
        var board      = new VideoStoryboard(160, 90, 10, 3, 1000);

        var prepared = await owner.Channels.PrepareVideoUpload(spaceId, channelA,
            Declare(mp4, poster: poster.fileId, storyboardId: storyboard.fileId, storyboard: board, preload: 10 * 1024 * 1024), ct);

        var ticket = Ticket(prepared);

        Assert.Multiple(() =>
        {
            Assert.That(ticket.uploadUrl, Is.Null, "a single PUT outlives the completion it was signed for");
            Assert.That(ticket.partUrls.Values, Has.Count.EqualTo(1));
            Assert.That(ticket.partSize, Is.EqualTo(mp4.Length));
            Assert.That(SignedHeaders(ticket.partUrls.Values[0]), Does.Contain("content-length"), "the part's length is not signed");
        });

        var parts = await UploadAsync(ticket, mp4, ct);
        var info  = Stored(await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct));

        await using var db = await ChannelTestKit.DbAsync(ct);

        var media = await db.FileMedia.AsNoTracking().SingleAsync(m => m.FileId == info.fileId, ct);
        var file  = await db.Files.AsNoTracking().SingleAsync(f => f.Id == info.fileId, ct);
        var left  = await db.FileBlobs.AsNoTracking().CountAsync(b => b.Id == ticket.ticketId, ct);
        var head  = await Store.HeadFileAsync(await db.KeyOfAsync(info.fileId, ct), ct);

        Assert.Multiple(() =>
        {
            Assert.That(info.fileId, Is.EqualTo(ticket.fileId));
            Assert.That((info.width, info.height, info.durationMs, info.hasAudio), Is.EqualTo((640, 360, 3000, true)));
            Assert.That(info.codec, Is.EqualTo("avc1.64001f"));
            Assert.That(info.fileSize, Is.EqualTo(mp4.Length));
            Assert.That(info.contentType, Is.EqualTo(VideoMedia.ContentType));
            Assert.That(info.preloadPrefixSize, Is.EqualTo(mp4.Length), "the preload hint was not clamped to the file");
            Assert.That(info.storyboard, Is.EqualTo(board));
            Assert.That(info.posterUrl, Does.Contain(poster.fileId.ToString()));
            Assert.That(info.storyboardUrl, Does.Contain(storyboard.fileId.ToString()));
            Assert.That(info.downloadUrl, Does.Contain(info.fileId.ToString()));

            Assert.That(media.AudioCodec, Is.EqualTo("mp4a.40.2"));
            Assert.That(media.PosterFileId, Is.EqualTo(poster.fileId));
            Assert.That(file.Finalized, Is.True);
            Assert.That(file.Purpose, Is.EqualTo(FilePurpose.Video));
            Assert.That(file.ChannelId, Is.EqualTo(channelA));
            Assert.That(left, Is.Zero, "the ticket outlived its upload");
            // No Cache-Control: SeaweedFS drops the one given at CreateMultipartUpload.
            Assert.That(head?.ContentType, Is.EqualTo(VideoMedia.ContentType));
        });
    }

    /// <summary>A video over the threshold goes up in parts, each answered with an ETag that completes it.</summary>
    [Test, CancelAfter(180_000)]
    public async Task A_large_video_goes_up_in_parts_and_is_completed_from_their_etags(CancellationToken ct = default)
    {
        var mp4 = new Mp4Builder { MdatSize = TwoPartMdat }.Build();

        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));

        Assert.Multiple(() =>
        {
            Assert.That(ticket.uploadUrl, Is.Null);
            Assert.That(ticket.partSize, Is.EqualTo(Limits.VideoPartSizeBytes));
            Assert.That(ticket.partUrls.Values, Has.Count.EqualTo(2));
        });

        var parts = await UploadAsync(ticket, mp4, ct);
        var info  = Stored(await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct));

        await using var db = await ChannelTestKit.DbAsync(ct);

        var head = await Store.HeadFileAsync(await db.KeyOfAsync(info.fileId, ct), ct);

        Assert.Multiple(() =>
        {
            Assert.That(info.fileSize, Is.EqualTo(mp4.Length));
            Assert.That((info.width, info.height), Is.EqualTo((640, 360)));
            Assert.That(head?.ContentLength, Is.EqualTo(mp4.Length));
            Assert.That(head?.ContentType, Is.EqualTo(VideoMedia.ContentType), "the multipart upload was not created with the video type");
        });
    }

    /// <summary>A retried completion answers what the first one did, without a second file.</summary>
    [Test, CancelAfter(120_000)]
    public async Task Completing_twice_answers_the_same_video_both_times(CancellationToken ct = default)
    {
        var mp4    = new Mp4Builder().Build();
        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        var first  = Stored(await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct));
        var second = Stored(await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct));

        await using var db = await ChannelTestKit.DbAsync(ct);

        Assert.Multiple(async () =>
        {
            Assert.That(second.fileId, Is.EqualTo(first.fileId));
            Assert.That((second.width, second.height, second.fileSize), Is.EqualTo((first.width, first.height, first.fileSize)));
            Assert.That(await db.FileCounters.CountAsync(c => c.Id == first.fileId, ct), Is.EqualTo(1));
        });
    }

    // ── refusals before anything is signed ─────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task An_over_limit_size_is_refused_before_anything_is_signed(CancellationToken ct = default)
    {
        var uploader = await CreateSessionAsync(ct);
        await SpaceGroupSupport.JoinAsync(owner, uploader, spaceId, ct);

        var declared = Declare(new Mp4Builder().Build()) with { size = Limits.VideoMaxBytes + 1 };
        var result   = await uploader.Channels.PrepareVideoUpload(spaceId, channelA, declared, ct);

        await using var db = await ChannelTestKit.DbAsync(ct);

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.EqualTo(new FailedVideoUpload(VideoUploadError.TOO_LARGE)));
            Assert.That(await db.FileBlobs.CountAsync(b => b.OwnerId == uploader.UserId, ct), Is.Zero, "a ticket was made for a refused size");
        });
    }

    /// <summary>
    /// The limits a client shows before it uploads are the ones the uploads are held to: base, then
    /// Ultima's, in a channel and in a direct chat, which has no space to be boosted.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Upload_limits_follow_the_callers_tier(CancellationToken ct = default)
    {
        var uploader = await CreateSessionAsync(ct);
        await SpaceGroupSupport.JoinAsync(owner, uploader, spaceId, ct);

        var channelBase = await uploader.Channels.GetUploadLimits(spaceId, channelA, ct);
        var directBase  = await uploader.Chats.GetUploadLimits(owner.UserId, ct);
        var outsider    = await (await CreateSessionAsync(ct)).Channels.GetUploadLimits(spaceId, channelA, ct);

        await GetGrainFactory().GetGrain<IUltimaGrain>(uploader.UserId).ActivateSubscriptionAsync(UltimaTier.Monthly, 30, null, null, ct);

        var channelUltima = await uploader.Channels.GetUploadLimits(spaceId, channelA, ct);
        var directUltima  = await uploader.Chats.GetUploadLimits(owner.UserId, ct);

        var baseLimits   = new UploadLimits(Limits.AttachmentBaseMaxBytes, Limits.VideoMaxBytes, Limits.VideoMaxDurationMs);
        var ultimaLimits = new UploadLimits(Limits.AttachmentUltimaMaxBytes, Limits.VideoUltimaMaxBytes, Limits.VideoMaxDurationMs);

        Assert.Multiple(() =>
        {
            Assert.That(baseLimits.attachmentMaxBytes, Is.EqualTo(15 * 1024 * 1024), "premise: the shipped base attachment limit");
            Assert.That(baseLimits.videoMaxBytes, Is.EqualTo(100 * 1024 * 1024), "premise: the shipped base video limit");

            Assert.That(channelBase, Is.EqualTo(baseLimits));
            Assert.That(directBase, Is.EqualTo(baseLimits));
            Assert.That(channelUltima, Is.EqualTo(ultimaLimits));
            Assert.That(directUltima, Is.EqualTo(ultimaLimits));
            Assert.That(outsider, Is.EqualTo(new UploadLimits(0, 0, 0)), "somebody who cannot read the channel was told its limits");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Too_long_non_mp4_and_non_h264_declarations_are_refused(CancellationToken ct = default)
    {
        var declared = Declare(new Mp4Builder().Build());

        var webm    = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { contentType = "video/webm" }, ct);
        var hevc    = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { codec = "hvc1.1.6.L93.B0" }, ct);
        var tooLong = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { durationMs = Limits.VideoMaxDurationMs + 1 }, ct);
        var name    = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { fileName = new string('a', 256) }, ct);

        Assert.Multiple(() =>
        {
            Assert.That(webm, Is.EqualTo(new FailedVideoUpload(VideoUploadError.CONTENT_TYPE_REJECTED)));
            Assert.That(hevc, Is.EqualTo(new FailedVideoUpload(VideoUploadError.CONTENT_TYPE_REJECTED)));
            Assert.That(tooLong, Is.EqualTo(new FailedVideoUpload(VideoUploadError.TOO_LONG)));
            Assert.That(name, Is.EqualTo(new FailedVideoUpload(VideoUploadError.DECLARATION_MISMATCH)));
        });
    }

    /// <summary>The poster and the storyboard are the caller's own finalized images in the same channel, and a storyboard comes with its layout.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_poster_that_is_not_the_callers_image_here_is_refused(CancellationToken ct = default)
    {
        var declared      = Declare(new Mp4Builder().Build());
        var membersPoster = await UploadImageAsync(member, channelA, ct);
        var elsewhere     = await UploadImageAsync(owner, channelB, ct);
        var own           = await UploadImageAsync(owner, channelA, ct);

        var foreign  = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { posterFileId = membersPoster.fileId }, ct);
        var moved    = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { posterFileId = elsewhere.fileId }, ct);
        var unknown  = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { posterFileId = Guid.NewGuid() }, ct);
        var noLayout = await owner.Channels.PrepareVideoUpload(spaceId, channelA, declared with { storyboardFileId = own.fileId }, ct);
        var tooBig   = await owner.Channels.PrepareVideoUpload(spaceId, channelA,
            declared with { storyboardFileId = own.fileId, storyboard = new VideoStoryboard(201, 90, 10, 3, 1000) }, ct);

        var refused = new FailedVideoUpload(VideoUploadError.POSTER_REJECTED);

        Assert.Multiple(() =>
        {
            Assert.That(foreign, Is.EqualTo(refused), "somebody else's image was taken as the poster");
            Assert.That(moved, Is.EqualTo(refused), "an image of another channel was taken as the poster");
            Assert.That(unknown, Is.EqualTo(refused));
            Assert.That(noLayout, Is.EqualTo(refused), "a storyboard without its layout was taken");
            Assert.That(tooBig, Is.EqualTo(refused), "a storyboard frame over 200 px was taken");
        });
    }

    /// <summary>An account holds a bounded number of open tickets; an abort frees one.</summary>
    [Test, CancelAfter(180_000)]
    public async Task Open_tickets_are_capped_per_account(CancellationToken ct = default)
    {
        var uploader = await CreateSessionAsync(ct);
        await SpaceGroupSupport.JoinAsync(owner, uploader, spaceId, ct);

        var declared = Declare(new Mp4Builder().Build());
        var tickets  = new List<VideoUploadTicket>();

        for (var i = 0; i < Limits.VideoMaxOpenTickets; i++)
            tickets.Add(Ticket(await uploader.Channels.PrepareVideoUpload(spaceId, channelA, declared, ct)));

        var over = await uploader.Channels.PrepareVideoUpload(spaceId, channelA, declared, ct);

        await uploader.Channels.AbortVideoUpload(spaceId, channelA, tickets[0].ticketId, ct);
        var freed = await uploader.Channels.PrepareVideoUpload(spaceId, channelA, declared, ct);

        Assert.Multiple(() =>
        {
            Assert.That(over, Is.EqualTo(new FailedVideoUpload(VideoUploadError.NOT_AUTHORIZED)));
            Assert.That(freed, Is.InstanceOf<VideoUploadRequired>(), "an aborted ticket still counted");
        });

        foreach (var ticket in tickets.Skip(1).Append(((VideoUploadRequired)freed).ticket))
            await uploader.Channels.AbortVideoUpload(spaceId, channelA, ticket.ticketId, ct);
    }

    // ── refusals at completion ──────────────────────────────────────────────────────────────────

    /// <summary>A header that disagrees with the declaration is refused after the parts are joined, and the object goes; the ticket stays to be aborted.</summary>
    [Test, CancelAfter(180_000)]
    public async Task A_declaration_the_header_disagrees_with_is_refused_and_the_object_dropped(CancellationToken ct = default)
    {
        var mp4    = new Mp4Builder { MdatSize = TwoPartMdat }.Build();
        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4) with { height = 480 }, ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        var result = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct);

        await using var db = await ChannelTestKit.DbAsync(ct);

        var key  = await db.KeyOfAsync(ticket.fileId, ct);
        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == ticket.fileId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(ticket.partUrls.Values, Has.Count.EqualTo(2), "premise: a multipart upload");
            Assert.That(result, Is.EqualTo(new FailedVideoUpload(VideoUploadError.DECLARATION_MISMATCH)));
            Assert.That(await Store.HeadFileAsync(key, ct), Is.Null, "the refused object is still in the store");
            Assert.That(file.Finalized, Is.False);
            Assert.That(await db.FileMedia.AnyAsync(m => m.FileId == ticket.fileId, ct), Is.False);
            Assert.That(await db.FileBlobs.AsNoTracking().AnyAsync(b => b.Id == ticket.ticketId, ct), Is.True, "the refusal took the ticket");
        });

        await owner.Channels.AbortVideoUpload(spaceId, channelA, ticket.ticketId, ct);

        Assert.That(await db.FileBlobs.AsNoTracking().AnyAsync(b => b.Id == ticket.ticketId, ct), Is.False, "the abort left the ticket");
    }

    /// <summary>A wrong or missing ETag is refused before anything is joined; the ticket and its parts stay, and the right list completes it.</summary>
    [Test, CancelAfter(180_000)]
    public async Task A_bad_etag_is_refused_and_the_ticket_survives_it(CancellationToken ct = default)
    {
        var mp4    = new Mp4Builder { MdatSize = TwoPartMdat }.Build();
        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        var wrong   = new IonArray<UploadedPart>([parts.Values[0], parts.Values[1] with { etag = "\"0123456789abcdef0123456789abcdef\"" }]);
        var missing = new IonArray<UploadedPart>([parts.Values[0]]);
        var empty   = new IonArray<UploadedPart>([parts.Values[0], parts.Values[1] with { etag = "" }]);

        var byWrong   = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, wrong, ct);
        var byMissing = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, missing, ct);
        var byEmpty   = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, empty, ct);
        var right     = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct);

        var mismatch = new FailedVideoUpload(VideoUploadError.DECLARATION_MISMATCH);

        Assert.Multiple(() =>
        {
            Assert.That(byWrong, Is.EqualTo(mismatch));
            Assert.That(byMissing, Is.EqualTo(mismatch));
            Assert.That(byEmpty, Is.EqualTo(mismatch));
            Assert.That(right, Is.InstanceOf<VideoStored>(), "the ticket did not survive a bad parts list");
        });
    }

    /// <summary>moov after mdat cannot start playing before it is downloaded, and that is the client's to fix.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_file_with_its_header_after_the_samples_is_not_streamable(CancellationToken ct = default)
    {
        var mp4    = new Mp4Builder { FastStart = false }.Build();
        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        var result = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct);

        Assert.That(result, Is.EqualTo(new FailedVideoUpload(VideoUploadError.NOT_STREAMABLE)));

        await owner.Channels.AbortVideoUpload(spaceId, channelA, ticket.ticketId, ct);
    }

    /// <summary>Only H.264 is kept, whatever the declaration left out.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_codec_other_than_h264_is_refused_even_undeclared(CancellationToken ct = default)
    {
        var mp4    = new Mp4Builder { VideoFourcc = "hvc1", VideoConfig = Mp4Builder.HvcC(0, false, 1, 0x60000000, [0xB0, 0, 0, 0, 0, 0], 93) }.Build();
        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4) with { codec = null }, ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        var result = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct);

        Assert.That(result, Is.EqualTo(new FailedVideoUpload(VideoUploadError.CONTENT_TYPE_REJECTED)));

        await owner.Channels.AbortVideoUpload(spaceId, channelA, ticket.ticketId, ct);
    }

    /// <summary>A part's signed length is its slot: a bigger body does not go up on that URL.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_part_larger_than_its_slot_is_refused_by_the_signature(CancellationToken ct = default)
    {
        var mp4    = new Mp4Builder().Build();
        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));

        using var larger = await PutAsync(ticket.partUrls.Values[0], new byte[mp4.Length + 1], [], ct);

        Assert.Multiple(() =>
        {
            Assert.That(SignedHeaders(ticket.partUrls.Values[0]), Does.Contain("content-length"));
            Assert.That(larger.IsSuccessStatusCode, Is.False, "the store took a part longer than the length signed for it");
        });

        await owner.Channels.AbortVideoUpload(spaceId, channelA, ticket.ticketId, ct);
    }

    /// <summary>A ticket past its time is refused, and its multipart upload is aborted with it.</summary>
    [Test, CancelAfter(180_000)]
    public async Task An_expired_ticket_is_refused_and_its_parts_dropped(CancellationToken ct = default)
    {
        var mp4      = new Mp4Builder().Build();
        var ticket   = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));
        var parts    = await UploadAsync(ticket, mp4, ct);
        var uploadId = await UploadIdAsync(ticket.ticketId, ct);

        await ExpireAsync(ticket.ticketId, ct);

        var result = await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct);

        await using var db = await ChannelTestKit.DbAsync(ct);

        Assert.Multiple(async () =>
        {
            Assert.That(result, Is.EqualTo(new FailedVideoUpload(VideoUploadError.TICKET_EXPIRED)));
            Assert.That(await MultipartIsOpenAsync(ticket.fileId, uploadId, ct), Is.False, "the multipart upload is still open");
            Assert.That(await db.FileBlobs.AsNoTracking().AnyAsync(b => b.Id == ticket.ticketId, ct), Is.False);
        });
    }

    /// <summary>The sweep does for an abandoned multipart ticket what an abort would have.</summary>
    [Test, CancelAfter(180_000)]
    public async Task The_sweep_aborts_an_abandoned_multipart_upload(CancellationToken ct = default)
    {
        var mp4      = new Mp4Builder().Build();
        var ticket   = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));
        var uploadId = await UploadIdAsync(ticket.ticketId, ct);

        Assert.That(await MultipartIsOpenAsync(ticket.fileId, uploadId, ct), Is.True, "premise: the store knows the upload");

        await ExpireAsync(ticket.ticketId, ct);

        var swept = false;
        for (var attempt = 0; attempt < 40 && !swept; attempt++)
        {
            await Gc.SweepExpiredBlobsAsync(ct);

            await using var db = await ChannelTestKit.DbAsync(ct);
            swept = !await db.FileBlobs.AsNoTracking().AnyAsync(b => b.Id == ticket.ticketId, ct);

            if (!swept)
                await Task.Delay(250, ct);
        }

        Assert.Multiple(async () =>
        {
            Assert.That(swept, Is.True, "the sweep never took the expired ticket");
            Assert.That(await MultipartIsOpenAsync(ticket.fileId, uploadId, ct), Is.False, "the sweep left the multipart upload open");
        });
    }

    /// <summary>An abort drops the parts and the rows at once; somebody else's abort drops nothing.</summary>
    [Test, CancelAfter(180_000)]
    public async Task Abort_drops_the_ticket_and_its_parts(CancellationToken ct = default)
    {
        var mp4      = new Mp4Builder().Build();
        var ticket   = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));
        var uploadId = await UploadIdAsync(ticket.ticketId, ct);

        await member.Channels.AbortVideoUpload(spaceId, channelA, ticket.ticketId, ct);
        var survivedStranger = await MultipartIsOpenAsync(ticket.fileId, uploadId, ct);

        await owner.Channels.AbortVideoUpload(spaceId, channelA, ticket.ticketId, ct);

        await using var db = await ChannelTestKit.DbAsync(ct);

        Assert.Multiple(async () =>
        {
            Assert.That(survivedStranger, Is.True, "another account's abort dropped the upload");
            Assert.That(await MultipartIsOpenAsync(ticket.fileId, uploadId, ct), Is.False, "the abort left the multipart upload open");
            Assert.That(await db.FileBlobs.AsNoTracking().AnyAsync(b => b.Id == ticket.ticketId, ct), Is.False);
            Assert.That(await db.Files.AsNoTracking().AnyAsync(f => f.Id == ticket.fileId, ct), Is.False);
        });
    }

    // ── the generic upload paths ────────────────────────────────────────────────────────────────

    /// <summary>A video ticket is closed only by CompleteVideoUpload: the attachment finalize does not take it.</summary>
    [Test, CancelAfter(120_000)]
    public async Task The_attachment_finalize_refuses_a_video_ticket(CancellationToken ct = default)
    {
        var mp4    = new Mp4Builder().Build();
        var ticket = Ticket(await owner.Channels.PrepareVideoUpload(spaceId, channelA, Declare(mp4), ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        Assert.That(async () => await owner.Channels.CompleteUploadAttachment(spaceId, channelA, ticket.ticketId, ct), Throws.Exception,
            "the attachment finalize closed a video ticket");

        await using var db = await ChannelTestKit.DbAsync(ct);

        Assert.Multiple(async () =>
        {
            Assert.That((await db.Files.AsNoTracking().SingleAsync(f => f.Id == ticket.fileId, ct)).Finalized, Is.False);
            Assert.That(await owner.Channels.CompleteVideoUpload(spaceId, channelA, ticket.ticketId, parts, ct), Is.InstanceOf<VideoStored>());
        });
    }

    /// <summary>
    /// Nothing but PrepareVideoUpload makes a video file: <c>/api/files/upload</c> and the grain behind it
    /// refuse the purpose. The controller is driven directly; the test host authenticates no REST caller.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_generic_upload_of_purpose_video_is_refused(CancellationToken ct = default)
    {
        var controller = new FileStorageController(FactoryAsp.Services.GetRequiredService<IClusterClient>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("uid", owner.UserId.ToString())])) }
            }
        };

        var http = await controller.RequestUpload(new FileUploadHttpRequest
        {
            Purpose = FilePurpose.Video, ContentType = VideoMedia.ContentType, FileSize = 1024, SpaceId = spaceId, ChannelId = channelA
        }, ct);

        Assert.Multiple(() =>
        {
            Assert.That(http, Is.InstanceOf<BadRequestObjectResult>());
            Assert.That(async () => await GetGrainFactory().GetGrain<IFileStorageGrain>(owner.UserId)
                   .RequestUploadAsync(new FileUploadRequest(FilePurpose.Video, VideoMedia.ContentType, 1024, spaceId, channelA), ct),
                Throws.InstanceOf<InvalidOperationException>(), "the grain signed a video upload without its header check");
        });
    }

    // ── copies ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Known bytes come back as a copy, media record included, without an upload.</summary>
    [Test, CancelAfter(120_000)]
    public async Task Known_bytes_are_handed_back_as_a_copy_with_their_media_record(CancellationToken ct = default)
    {
        var poster = await UploadImageAsync(owner, channelA, ct);
        var mp4    = new Mp4Builder().Build();
        var first  = await StoreVideoAsync(owner, channelA, mp4, Declare(mp4, sha: true, poster: poster.fileId), ct);

        var again = await owner.Channels.PrepareVideoUpload(spaceId, channelB, Declare(mp4, sha: true) with { fileName = "again.mp4" }, ct);

        var copy = Stored(again);

        await using var db = await ChannelTestKit.DbAsync(ct);

        var rows = await db.Files.AsNoTracking().Where(f => f.Id == first.fileId || f.Id == copy.fileId).ToDictionaryAsync(f => f.Id, ct);

        Assert.Multiple(() =>
        {
            Assert.That(copy.fileId, Is.Not.EqualTo(first.fileId));
            Assert.That(copy.fileName, Is.EqualTo("again.mp4"));
            Assert.That((copy.width, copy.height, copy.durationMs, copy.codec), Is.EqualTo((first.width, first.height, first.durationMs, first.codec)));
            Assert.That(copy.posterFileId, Is.EqualTo(poster.fileId), "the copy lost the source's poster");
            Assert.That(rows[copy.fileId].BlobId, Is.EqualTo(rows[first.fileId].BlobId), "the copy has an object of its own");
            Assert.That(rows[copy.fileId].ChannelId, Is.EqualTo(channelB));
        });
    }

    /// <summary>Bytes the account posted as a plain file are read once by their header and made a video; ones that fail it are uploaded instead.</summary>
    [Test, CancelAfter(120_000)]
    public async Task Bytes_posted_as_a_plain_file_are_made_a_video_from_their_header(CancellationToken ct = default)
    {
        var mp4  = new Mp4Builder { Width = 320, Height = 240, Audio = false }.Build();
        var late = new Mp4Builder { FastStart = false }.Build();

        await PostPlainAsync(mp4, ct);
        await PostPlainAsync(late, ct);

        var video    = Stored(await owner.Channels.PrepareVideoUpload(spaceId, channelB, Declare(mp4, sha: true, width: 320, height: 240, audio: false), ct));
        var fallback = await owner.Channels.PrepareVideoUpload(spaceId, channelB, Declare(late, sha: true), ct);

        Assert.Multiple(() =>
        {
            Assert.That((video.width, video.height, video.hasAudio), Is.EqualTo((320, 240, false)));
            Assert.That(video.codec, Is.EqualTo("avc1.64001f"));
            Assert.That(fallback, Is.InstanceOf<VideoUploadRequired>(), "bytes whose header fails were refused instead of uploaded");
        });

        if (fallback is VideoUploadRequired { ticket: var open })
            await owner.Channels.AbortVideoUpload(spaceId, channelB, open.ticketId, ct);

        async Task PostPlainAsync(byte[] bytes, CancellationToken token)
        {
            var plain = await owner.Channels.PrepareUploadAttachment(spaceId, channelA, new IonBytes(SHA256.HashData(bytes)), bytes.Length,
                "video/mp4", "plain.mp4", token);
            Assert.That(plain, Is.InstanceOf<UploadRequired>(), "setup: the plain upload was not given a ticket");

            var up = (UploadRequired)plain;
            await SocialHarness.UploadAsync(new SuccessUploadFile(up.blobId, up.uploadUrl, up.formFields, up.ttlSeconds), bytes, "video/mp4");
            await owner.Channels.CompleteUploadAttachment(spaceId, channelA, up.blobId, token);
        }
    }

    /// <summary>A video copied into another channel stays a video: same media record, video limits.</summary>
    [Test, CancelAfter(120_000)]
    public async Task Copying_a_video_into_another_channel_keeps_its_media_record(CancellationToken ct = default)
    {
        var poster = await UploadImageAsync(owner, channelA, ct);
        var mp4    = new Mp4Builder().Build();
        var source = await StoreVideoAsync(owner, channelA, mp4, Declare(mp4, poster: poster.fileId), ct);

        var result = await member.Channels.AttachExistingFile(spaceId, channelB, source.fileId, null, ct);

        Assert.That(result, Is.InstanceOf<SuccessAttachExistingFile>(), $"refused: {(result as FailedAttachExistingFile)?.error}");

        var copy = ((SuccessAttachExistingFile)result).info;

        await using var db = await ChannelTestKit.DbAsync(ct);

        var media = await db.FileMedia.AsNoTracking().SingleOrDefaultAsync(m => m.FileId == copy.fileId, ct);
        var file  = await db.Files.AsNoTracking().SingleAsync(f => f.Id == copy.fileId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(media, Is.Not.Null, "the copy has no media record");
            Assert.That(media?.PosterFileId, Is.EqualTo(poster.fileId));
            Assert.That((media?.Width, media?.Height), Is.EqualTo(((int?)640, (int?)360)));
            Assert.That(file.Purpose, Is.EqualTo(FilePurpose.Video));
            Assert.That(file.OwnerId, Is.EqualTo(member.UserId));
        });
    }

    // ── messages ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A sent video entity is rewritten from the file's record and served with its URLs; a video that is
    /// not the sender's, or not uploaded into this channel, refuses the send; videos count as files.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_sent_video_is_rewritten_from_its_record_and_a_foreign_one_refused(CancellationToken ct = default)
    {
        var poster = await UploadImageAsync(owner, channelA, ct);
        var mp4    = new Mp4Builder().Build();
        var video  = await StoreVideoAsync(owner, channelA, mp4, Declare(mp4, poster: poster.fileId), ct);

        var forged = VideoMedia.Placeholder(video.fileId) with
        {
            fileName = "forged.mp4", width = 1, height = 1, durationMs = 1, codec = "hvc1", downloadUrl = "https://evil.example/steal"
        };

        var messageId = await owner.Channels.SendMessage(spaceId, channelA, "", Entities(forged), Random.Shared.NextInt64(), null, ct).Ok();

        var stored = (MessageEntityVideo)(await ChannelTestKit.StoredMessageAsync(spaceId, channelA, messageId, ct))!.Entities.Single();
        var served = (MessageEntityVideo)(await owner.Channels.QueryMessages(spaceId, channelA, null, 20, ct)).Values
           .Single(m => m.messageId == messageId).entities.Values.Single();

        var byMember  = await member.Channels.SendMessage(spaceId, channelA, "", Entities(forged), Random.Shared.NextInt64(), null, ct);
        var elsewhere = await owner.Channels.SendMessage(spaceId, channelB, "", Entities(forged), Random.Shared.NextInt64(), null, ct);
        var eleven    = await owner.Channels.SendMessage(spaceId, channelA, "",
            Entities(Enumerable.Range(0, 11).Select(_ => (IMessageEntity)VideoMedia.Placeholder(video.fileId)).ToArray()), Random.Shared.NextInt64(), null, ct);

        Assert.Multiple(() =>
        {
            Assert.That((stored.width, stored.height, stored.durationMs, stored.codec), Is.EqualTo((640, 360, 3000, "avc1.64001f")));
            Assert.That(stored.fileName, Is.EqualTo("clip.mp4"));
            Assert.That(stored.fileSize, Is.EqualTo(mp4.Length));
            Assert.That(stored.posterFileId, Is.EqualTo(poster.fileId));
            Assert.That(stored.downloadUrl, Is.Null, "the client's URL was stored");

            Assert.That(served.downloadUrl, Is.EqualTo(Store.GetFileDownloadUrl(video.fileId)));
            Assert.That(served.posterUrl, Is.EqualTo(Store.GetFileDownloadUrl(poster.fileId)));

            Assert.That(byMember, Is.EqualTo(new FailedSendMessage(SendMessageError.INVALID_DATA)), "somebody else's video was sent");
            Assert.That(elsewhere, Is.EqualTo(new FailedSendMessage(SendMessageError.INVALID_DATA)), "a video was sent outside its channel");
            Assert.That(eleven, Is.EqualTo(new FailedSendMessage(SendMessageError.TOO_MANY_ATTACHMENTS)));
        });
    }

    /// <summary>A scheduled post keeps the record's fields of its author's own video, and loses a video that is not theirs.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_scheduled_post_keeps_only_its_authors_videos_from_their_records(CancellationToken ct = default)
    {
        var mp4   = new Mp4Builder().Build();
        var video = await StoreVideoAsync(owner, channelA, mp4, Declare(mp4), ct);

        var forged = VideoMedia.Placeholder(video.fileId) with
        {
            width    = 1, height = 1, codec = "hvc1",
            variants = new IonArray<VideoVariant>([new VideoVariant(Guid.NewGuid(), 1, 1, 1, null, 1, "https://evil.example")])
        };

        var later = DateTimeOffset.UtcNow.AddHours(1);

        var own        = await ComposerOf(owner).SchedulePost(spaceId, channelA, "", Entities(forged), later, ct);
        var foreign    = await ComposerOf(member).SchedulePost(spaceId, channelA, "", Entities(forged), later, ct);
        var withText   = await ComposerOf(member).SchedulePost(spaceId, channelA, "look", Entities(forged), later, ct);

        Assert.That(own, Is.InstanceOf<SuccessSchedulePost>(), $"refused: {(own as FailedSchedulePost)?.error}");
        Assert.That(withText, Is.InstanceOf<SuccessSchedulePost>(), $"refused: {(withText as FailedSchedulePost)?.error}");

        var kept = (MessageEntityVideo)((SuccessSchedulePost)own).post.entities.Values.Single();

        Assert.Multiple(() =>
        {
            Assert.That((kept.width, kept.height, kept.codec), Is.EqualTo((640, 360, "avc1.64001f")), "the post kept the client's fields");
            Assert.That(kept.variants.Values ?? [], Is.Empty, "the post kept the client's variants");
            Assert.That(foreign, Is.EqualTo(new FailedSchedulePost(SchedulePostError.EMPTY_MESSAGE)), "somebody else's video was scheduled");
            Assert.That(((SuccessSchedulePost)withText).post.entities.Values ?? [], Is.Empty, "somebody else's video stayed on a post");
        });
    }

    /// <summary>The direct-chat twins: scoped to the conversation, filed under the sender, refused when the peer blocked the sender.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_direct_chat_video_belongs_to_its_conversation(CancellationToken ct = default)
    {
        var alice   = await CreateSessionAsync(ct);
        var bob     = await CreateSessionAsync(ct);
        var charlie = await CreateSessionAsync(ct);

        var mp4    = new Mp4Builder().Build();
        var ticket = Ticket(await alice.Chats.PrepareVideoUpload(bob.UserId, Declare(mp4), ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        var video = Stored(await alice.Chats.CompleteVideoUpload(bob.UserId, ticket.ticketId, parts, ct));

        var forged = VideoMedia.Placeholder(video.fileId) with { width = 1, height = 1 };

        await alice.Chats.SendDirectMessage(bob.UserId, "", new IonArray<IMessageEntity>([forged]), Random.Shared.NextInt64(), null, ct);
        await charlie.Chats.SendDirectMessage(bob.UserId, "not mine", new IonArray<IMessageEntity>([forged]), Random.Shared.NextInt64(), null, ct);

        var received  = (await bob.Chats.QueryDirectMessages(alice.UserId, null, 5, ct)).Values.Single();
        var fromThird = (await bob.Chats.QueryDirectMessages(charlie.UserId, null, 5, ct)).Values.Single();

        await bob.Friends.BlockUser(alice.UserId, ct);
        var blocked = await alice.Chats.PrepareVideoUpload(bob.UserId, Declare(mp4), ct);

        await using var db = await ChannelTestKit.DbAsync(ct);
        var file = await db.Files.AsNoTracking().SingleAsync(f => f.Id == video.fileId, ct);
        var key  = await db.KeyOfAsync(video.fileId, ct);
        var chat = ConversationEntity.GenerateConversationId(alice.UserId, bob.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(file.SpaceId, Is.Null);
            Assert.That(file.ChannelId, Is.EqualTo(chat));
            Assert.That(key, Is.EqualTo($"u/{alice.UserId}/video/{video.fileId}"));
            Assert.That(key, Does.Not.Contain(chat.ToString()), "the key carries the conversation id");

            var entity = (MessageEntityVideo)received.entities.Values.Single();
            Assert.That((entity.width, entity.height), Is.EqualTo((640, 360)), "the direct message kept the client's fields");

            Assert.That(fromThird.entities.Values ?? [], Is.Empty, "a video of somebody else's chat went through");
            Assert.That(blocked, Is.EqualTo(new FailedVideoUpload(VideoUploadError.NOT_AUTHORIZED)));
        });
    }

    /// <summary>
    /// A bot's faststart H.264 MP4 is read off its header at upload and goes out as a video, shown back to
    /// bots as the attachment it was; any other MP4 stays a plain file.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task A_bot_mp4_goes_out_as_a_video_and_any_other_as_a_file(CancellationToken ct = default)
    {
        const ArgonEntitlement sends = ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory | ArgonEntitlement.SendMessages
                                     | ArgonEntitlement.AttachFiles;

        var bot  = await BotHttpKit.InstallAsync(owner, spaceId, sends, ct);
        var h264 = new Mp4Builder { Width = 480, Height = 270 }.Build();
        var hevc = new Mp4Builder { VideoFourcc = "hvc1", VideoConfig = Mp4Builder.HvcC(0, false, 1, 0x60000000, [0xB0, 0, 0, 0, 0, 0], 93) }.Build();

        var (videoFile, videoMessage) = await SendAsync(h264);
        var (plainFile, plainMessage) = await SendAsync(hevc);

        var asVideo = (await ChannelTestKit.StoredMessageAsync(spaceId, channelA, videoMessage, ct))?.Entities.Single();
        var asFile  = (await ChannelTestKit.StoredMessageAsync(spaceId, channelA, plainMessage, ct))?.Entities.Single();

        var history = BotHttpKit.Ok(await BotHttpKit.GetAsync(bot, $"/IMessages/v1/History?channelId={channelA}&limit=10", ct))
           .GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("messageId").GetInt64() == videoMessage);

        Assert.Multiple(() =>
        {
            Assert.That(asVideo, Is.InstanceOf<MessageEntityVideo>(), "the bot's MP4 went out as a plain file");
            Assert.That((asVideo as MessageEntityVideo)?.width, Is.EqualTo(480));
            Assert.That(asFile, Is.InstanceOf<MessageEntityAttachment>(), "an HEVC file was made a video");
            Assert.That(history.GetProperty("attachments").EnumerateArray().Single().GetProperty("fileId").GetGuid(), Is.EqualTo(videoFile),
                "bots no longer see the file a video message carries");
            Assert.That(plainFile, Is.Not.EqualTo(videoFile));
        });

        async Task<(Guid FileId, long MessageId)> SendAsync(byte[] mp4)
        {
            var form   = BotHttpKit.Part(new MultipartFormDataContent { { new StringContent("attachment"), "purpose" } }, "file", mp4, "video/mp4", "bot.mp4");
            var fileId = BotHttpKit.Ok(await BotHttpKit.SendAsync(bot, HttpMethod.Post, "/IFiles/v1/Upload", form, ct)).GetProperty("fileId").GetGuid();

            var sent = BotHttpKit.Ok(await BotHttpKit.PostAsync(bot, "/IMessages/v1/Send",
                new { channelId = channelA, text = "", randomId = Random.Shared.NextInt64(1, long.MaxValue), attachments = new[] { fileId.ToString() } }, ct));

            Assert.That(sent.GetProperty("attachments").EnumerateArray().Single().GetProperty("fileId").GetGuid(), Is.EqualTo(fileId));
            return (fileId, sent.GetProperty("messageId").GetInt64());
        }
    }

    // ── the URL endpoint ────────────────────────────────────────────────────────────────────────

    /// <summary>The player's one lookup: the regional URL the redirect would send it to, as JSON, privately cacheable.</summary>
    [Test, CancelAfter(120_000)]
    public async Task The_url_endpoint_answers_with_the_address_the_redirect_points_at(CancellationToken ct = default)
    {
        var mp4   = new Mp4Builder().Build();
        var video = await StoreVideoAsync(owner, channelA, mp4, Declare(mp4), ct);

        using var client = FactoryAsp.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using var redirect = await client.GetAsync($"{CdnOptions.FilePath}/{video.fileId}", ct);
        using var resolved = await client.GetAsync($"{CdnOptions.FilePath}/{video.fileId}/url", ct);

        var body = await resolved.Content.ReadFromJsonAsync<Resolved>(ct);

        Assert.Multiple(() =>
        {
            Assert.That(resolved.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body?.url, Is.EqualTo(redirect.Headers.Location?.ToString()));
            Assert.That(body?.ttlSeconds, Is.EqualTo(300));
            Assert.That(resolved.Headers.CacheControl?.Private, Is.True, "a shared cache may keep one region's address for another");
            Assert.That(resolved.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromSeconds(300)));
        });
    }

    private sealed record Resolved(string url, int ttlSeconds);

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static VideoUploadDeclaration Declare(byte[] mp4, bool sha = false, Guid? poster = null, Guid? storyboardId = null,
        VideoStoryboard? storyboard = null, long? preload = null, int width = 640, int height = 360, bool audio = true)
        => new("clip.mp4", VideoMedia.ContentType, mp4.Length, sha ? new IonBytes(SHA256.HashData(mp4)) : (IonBytes?)null,
            width, height, 3000, audio, "avc1.64001f", "1QcSHQRnh493V4dIh4eXh1h4kJUI", poster, storyboardId, storyboard, preload);

    private static VideoUploadTicket Ticket(IVideoUploadResult result)
    {
        Assert.That(result, Is.InstanceOf<VideoUploadRequired>(), $"expected a ticket, got {result}");
        return ((VideoUploadRequired)result).ticket;
    }

    private static VideoInfo Stored(IVideoUploadResult result)
    {
        Assert.That(result, Is.InstanceOf<VideoStored>(), $"expected the video stored, got {result}");
        return ((VideoStored)result).info;
    }

    private static IonArray<IMessageEntity> Entities(params IMessageEntity[] entities)
        => new(entities);

    private static IChannelComposerInteraction ComposerOf(TestUserSession session)
        => session.Client.ForService<IChannelComposerInteraction>(ChannelTestKit.Services);

    private static string SignedHeaders(string url)
        => HttpUtility.ParseQueryString(new Uri(url).Query)["X-Amz-SignedHeaders"] ?? "";

    private async Task<VideoInfo> StoreVideoAsync(TestUserSession who, Guid channelId, byte[] mp4, VideoUploadDeclaration declared, CancellationToken ct)
    {
        var ticket = Ticket(await who.Channels.PrepareVideoUpload(spaceId, channelId, declared, ct));
        var parts  = await UploadAsync(ticket, mp4, ct);

        return Stored(await who.Channels.CompleteVideoUpload(spaceId, channelId, ticket.ticketId, parts, ct));
    }

    private async Task<AttachmentInfo> UploadImageAsync(TestUserSession who, Guid channelId, CancellationToken ct)
    {
        var begin = await who.Channels.BeginUploadAttachment(spaceId, channelId, ct);

        Assert.That(begin, Is.InstanceOf<SuccessUploadFile>(), $"setup: the image upload was refused: {(begin as FailedUploadFile)?.error}");

        var ticket = (SuccessUploadFile)begin;
        await SocialHarness.UploadAsync(ticket, [..SocialHarness.Png, ..RandomNumberGenerator.GetBytes(8)], "image/png");

        return await who.Channels.CompleteUploadAttachment(spaceId, channelId, ticket.blobId, ct);
    }

    /// <summary>One PUT per part, as the ticket lays them out; the parts to complete with.</summary>
    private static async Task<IonArray<UploadedPart>> UploadAsync(VideoUploadTicket ticket, byte[] mp4, CancellationToken ct)
    {
        Assert.That(ticket.uploadUrl, Is.Null, "a video ticket handed out a single PUT");

        var parts = new List<UploadedPart>();
        var urls  = ticket.partUrls.Values;

        for (var i = 0; i < urls.Count; i++)
        {
            var from = (int)(i * ticket.partSize);
            var to   = (int)Math.Min(mp4.Length, from + ticket.partSize);

            using var response = await PutAsync(urls[i], mp4.AsMemory(from, to - from), ticket.formFields.Values?.Select(f => (f.key, f.value)) ?? [], ct);

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                $"the store refused part {i + 1}: {await response.Content.ReadAsStringAsync(ct)}");

            parts.Add(new UploadedPart(i + 1, response.Headers.ETag?.Tag ?? ""));
        }

        return new IonArray<UploadedPart>(parts);
    }

    /// <summary>
    /// Whether the store still lists an open upload under the file's key. Not a part PUT or ListParts:
    /// SeaweedFS answers both for any upload id it minted, aborted or not.
    /// </summary>
    /// <remarks>
    /// SimpleS3 cannot parse the upload entries SeaweedFS lists (no initiator), and throws on the first
    /// one; the key is the ticket's own, so an entry under it is this upload.
    /// </remarks>
    private async Task<bool> MultipartIsOpenAsync(Guid fileId, string uploadId, CancellationToken ct)
    {
        await using var db = await ChannelTestKit.DbAsync(ct);

        var key    = await db.KeyOfAsync(fileId, ct);
        var bucket = FactoryAsp.Services.GetRequiredService<IOptions<StorageOptions>>().Value.BucketName;

        try
        {
            var uploads = await FactoryAsp.Services.GetRequiredService<IS3ClientPool>().GetMultipartClient()
               .ListMultipartUploadsAsync(bucket, r =>
                {
                    r.Prefix       = key;
                    r.EncodingType = Genbox.SimpleS3.Core.Enums.EncodingType.Url; // the parser refuses an empty one
                }, ct);

            Assert.That(uploads.IsSuccess, Is.True, $"the store did not list its multipart uploads: {uploads.Error?.Message}");
            return uploads.Uploads.Any(u => u.UploadId == uploadId);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("Missing required values"))
        {
            return true;
        }
    }

    private static async Task<string> UploadIdAsync(Guid ticketId, CancellationToken ct)
    {
        await using var db = await ChannelTestKit.DbAsync(ct);

        var uploadId = await db.FileBlobs.AsNoTracking().Where(b => b.Id == ticketId).Select(b => b.UploadId).SingleAsync(ct);

        Assert.That(uploadId, Is.Not.Null, "premise: the ticket is a multipart one");
        return uploadId!;
    }

    private static async Task ExpireAsync(Guid ticketId, CancellationToken ct)
    {
        await using var db = await ChannelTestKit.DbAsync(ct);
        await db.FileBlobs.Where(b => b.Id == ticketId)
           .ExecuteUpdateAsync(s => s.SetProperty(b => b.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), ct);
    }

    /// <summary>A PUT to the store's mapped port, whatever host the signed URL names; headers go where HTTP puts them.</summary>
    private static async Task<HttpResponseMessage> PutAsync(string url, ReadOnlyMemory<byte> body, IEnumerable<(string Key, string Value)> headers,
        CancellationToken ct)
    {
        var port = int.Parse(ArgonTestEnvironment.Instance.S3Endpoint.Split(':')[1]);

        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                await socket.ConnectAsync(IPAddress.Loopback, port, token);
                return new NetworkStream(socket, ownsSocket: true);
            }
        });

        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ReadOnlyMemoryContent(body) };

        foreach (var (key, value) in headers)
            if (!request.Content.Headers.TryAddWithoutValidation(key, value))
                request.Headers.TryAddWithoutValidation(key, value);

        return await client.SendAsync(request, ct);
    }
}
