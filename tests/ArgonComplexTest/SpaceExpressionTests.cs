namespace ArgonComplexTest.Tests;

using System.IO.Compression;
using System.Net;
using System.Text;
using Argon.Features.Expressions;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using static ChannelTestKit;

/// <summary>
/// Stickers and custom emoji of a space: packs, uploads through the object store, the snapshot and
/// its token, the change events, and what the message path makes of the entities.
/// </summary>
[TestFixture]
public class SpaceExpressionTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(15);

    private static readonly IonArray<string> Wave       = new(["👋"]);
    private static readonly IonArray<string> NoKeywords = IonArray<string>.Empty;

    private long randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref randomId);

    // ── files ───────────────────────────────────────────────────────────────────────────────────

    private static Image<Rgba32> Disc(int side)
    {
        var image = new Image<Rgba32>(side, side, new Rgba32(0, 0, 0, 0));
        var r     = side * 0.4;
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
            if (Math.Pow(x + 0.5 - side / 2.0, 2) + Math.Pow(y + 0.5 - side / 2.0, 2) <= r * r)
                image[x, y] = new Rgba32(220, 40, 90, 255);
        return image;
    }

    private static byte[] Webp(int side)
    {
        using var image  = Disc(side);
        using var output = new MemoryStream();
        image.SaveAsWebp(output, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless });
        return output.ToArray();
    }

    private static byte[] Png(int side)
    {
        using var image  = Disc(side);
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private static readonly byte[] StickerWebp = Webp(512);
    private static readonly byte[] EmojiPng    = Png(100);

    /// <summary>A 3 s, 60 fps, 512×512 animation, gzipped the way a .tgs is.</summary>
    private static byte[] Tgs()
    {
        const string json = """
        {"v":"5.7.4","fr":60,"ip":0,"op":180,"w":512,"h":512,"nm":"test","ddd":0,"assets":[],
         "layers":[{"ddd":0,"ind":1,"ty":4,"nm":"rect","sr":1,"ao":0,
           "ks":{"o":{"a":0,"k":100},
                 "r":{"a":1,"k":[{"i":{"x":[0.833],"y":[0.833]},"o":{"x":[0.167],"y":[0.167]},"t":0,"s":[0]},{"t":179,"s":[360]}]},
                 "p":{"a":0,"k":[256,256,0]},"a":{"a":0,"k":[0,0,0]},"s":{"a":0,"k":[100,100,100]}},
           "shapes":[{"ty":"gr","nm":"group","it":[
              {"ty":"rc","d":1,"s":{"a":0,"k":[200,200]},"p":{"a":0,"k":[0,0]},"r":{"a":0,"k":0}},
              {"ty":"fl","c":{"a":0,"k":[1,0,0,1]},"o":{"a":0,"k":100}},
              {"ty":"tr","p":{"a":0,"k":[0,0]},"a":{"a":0,"k":[0,0]},"s":{"a":0,"k":[100,100]},"r":{"a":0,"k":0},"o":{"a":0,"k":100}}]}],
           "ip":0,"op":180,"st":0,"bm":0}]}
        """;

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(json));
        return output.ToArray();
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static ISpaceExpressionInteraction ExpressionsOf(TestUserSession session)
        => session.Client.ForService<ISpaceExpressionInteraction>(Services);

    private async Task<(TestUserSession Owner, Guid SpaceId, Guid ChannelId)> RoomAsync(CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "stickers", ChannelType.Text, ct);
        return (owner, spaceId, channelId);
    }

    private static ExpressionPack Ok(IPackResult result)
    {
        Assert.That(result, Is.InstanceOf<SuccessPack>(), $"refused: {(result as FailedPack)?.error}");
        return ((SuccessPack)result).pack;
    }

    private static ExpressionItem Ok(IItemResult result)
    {
        Assert.That(result, Is.InstanceOf<SuccessItem>(), $"refused: {(result as FailedItem)?.error}");
        return ((SuccessItem)result).item;
    }

    private static ExpressionError? ErrorOf(object result) => result switch
    {
        FailedPack p    => p.error,
        FailedItem i    => i.error,
        FailedReorder r => r.error,
        _               => null
    };

    private static async Task<ExpressionPack> PackAsync(TestUserSession session, Guid spaceId, ExpressionKind kind, string slug, CancellationToken ct)
        => Ok(await ExpressionsOf(session).CreatePack(spaceId, kind, $"Pack {slug}", slug, ct));

    private static async Task<IUploadFileResult> BeginAsync(TestUserSession session, Guid spaceId, ExpressionKind kind, ExpressionFormat format,
        byte[] bytes, string contentType, CancellationToken ct)
        => await ExpressionsOf(session).BeginUploadExpression(spaceId, kind, format, contentType, bytes.Length, ct);

    private static async Task<Guid> PutAsync(IUploadFileResult begun, byte[] bytes, string contentType)
    {
        Assert.That(begun, Is.InstanceOf<SuccessUploadFile>(), $"the upload was not signed: {(begun as FailedUploadFile)?.error}");
        var ticket = (SuccessUploadFile)begun;

        using var response = await TestObjectStore.UploadAsync(ticket.uploadUrl, bytes, contentType,
            ticket.formFields.Values.Select(f => (f.key, f.value)));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());
        return ticket.blobId;
    }

    private static async Task<Guid> UploadAsync(TestUserSession session, Guid spaceId, ExpressionKind kind, ExpressionFormat format,
        byte[] bytes, string contentType, CancellationToken ct)
        => await PutAsync(await BeginAsync(session, spaceId, kind, format, bytes, contentType, ct), bytes, contentType);

    private static async Task<IItemResult> AddStickerAsync(TestUserSession session, Guid spaceId, Guid packId, CancellationToken ct,
        string name = "wave")
    {
        var blob = await UploadAsync(session, spaceId, ExpressionKind.Sticker, ExpressionFormat.Static, StickerWebp, "image/webp", ct);
        return await ExpressionsOf(session).AddItem(spaceId, packId, blob, null, name, Wave, NoKeywords, null, ct);
    }

    private static async Task<IItemResult> AddEmojiAsync(TestUserSession session, Guid spaceId, Guid packId, string name, CancellationToken ct)
    {
        var blob = await UploadAsync(session, spaceId, ExpressionKind.Emoji, ExpressionFormat.Static, EmojiPng, "image/png", ct);
        return await ExpressionsOf(session).AddItem(spaceId, packId, blob, null, name, Wave, NoKeywords, null, ct);
    }

    private static async Task<ExpressionsSnapshot> SnapshotAsync(TestUserSession session, Guid spaceId, string? known, CancellationToken ct)
        => await ExpressionsOf(session).GetExpressions(spaceId, known, ct);

    private static async Task<Archetype> RoleAsync(TestUserSession owner, Guid spaceId, string name, ArgonEntitlement entitlement,
        CancellationToken ct)
    {
        var created = await ArchetypesOf(owner).CreateArchetype(spaceId, name, ct).Ok();
        return await ArchetypesOf(owner).UpdateArchetype(spaceId, created with { entitlement = entitlement }, ct).Ok();
    }

    private async Task<TestUserSession> MemberWithAsync(TestUserSession owner, Guid spaceId, ArgonEntitlement? entitlement, CancellationToken ct)
    {
        var member = await CreateSessionAsync(ct);
        await JoinAsync(owner, member, spaceId, ct);

        if (entitlement is { } granted)
        {
            var role = await RoleAsync(owner, spaceId, $"r{Random.Shared.Next(100000)}", granted, ct);
            Assert.That(await ArchetypesOf(owner).SetArchetypeToMember(spaceId, await MemberIdOfAsync(owner, spaceId, member.UserId, ct), role.id, true, ct),
                Is.True, "setup: the role was not granted");
        }

        return member;
    }

    private static MessageEntitySticker StickerEntity(ExpressionItem item, Guid? spaceId = null)
        => new(EntityType.Sticker, 0, 0, 1, item.itemId, Guid.NewGuid(), spaceId ?? item.spaceId, ExpressionFormat.Video, Guid.NewGuid(),
            null, 1, 1, null, "https://example.invalid/x", null);

    private static MessageEntityCustomEmoji EmojiEntity(ExpressionItem item, int offset, int length)
        => new(EntityType.CustomEmoji, offset, length, 1, item.itemId, item.spaceId, ExpressionFormat.Video, Guid.NewGuid(), "spoof", false,
            "https://example.invalid/x");

    private static async Task<ArgonMessage> ReadAsync(TestUserSession reader, Guid spaceId, Guid channelId, long messageId, CancellationToken ct)
        => (await reader.Channels.QueryMessages(spaceId, channelId, null, 50, ct)).Values.Single(m => m.messageId == messageId);

    private static async Task<string?> StoredContentTypeAsync(Guid fileId, CancellationToken ct)
    {
        await using var db  = await DbAsync(ct);
        var             key = await db.Files.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.S3Key).SingleAsync(ct);
        return (await Services.GetRequiredService<IS3StorageService>().HeadFileAsync(key, ct))?.ContentType;
    }

    // ── packs and uploads ───────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task A_webp_sticker_is_added_with_an_outline_traced_by_the_server(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "waves", ct);

        var item = Ok(await AddStickerAsync(owner, spaceId, pack.packId, ct));

        var snapshot = await SnapshotAsync(owner, spaceId, null, ct);
        var listed   = snapshot.packs!.Value.Single().items.Single();

        Assert.Multiple(() =>
        {
            Assert.That(item.format, Is.EqualTo(ExpressionFormat.Static));
            Assert.That((item.width, item.height), Is.EqualTo((512, 512)));
            Assert.That(item.outline, Is.Not.Null, "a static sticker is outlined from its alpha channel");
            Assert.That(item.outline!.Value.Length, Is.InRange(1, ExpressionLimits.OutlineMaxBytes));
            Assert.That(item.thumbFileId, Is.Null);
            Assert.That(listed.itemId, Is.EqualTo(item.itemId));
            Assert.That(listed.downloadUrl, Does.Contain(item.fileId.ToString()));
        });

        Assert.That(await StoredContentTypeAsync(item.fileId, ct), Is.EqualTo("image/webp"));
    }

    [Test, CancelAfter(180_000)]
    public async Task A_png_sticker_is_stored_as_webp(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "pngs", ct);

        var blob = await UploadAsync(owner, spaceId, ExpressionKind.Sticker, ExpressionFormat.Static, Png(512), "image/png", ct);
        var item = Ok(await ExpressionsOf(owner).AddItem(spaceId, pack.packId, blob, null, "png", Wave, NoKeywords, null, ct));

        await using var db = await DbAsync(ct);
        var file   = await db.Files.AsNoTracking().SingleAsync(f => f.Id == item.fileId, ct);
        var stored = await StoredContentTypeAsync(item.fileId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.EqualTo("image/webp"), "the object in the store is still a PNG");
            Assert.That(file.ContentType, Is.EqualTo("image/webp"));
            Assert.That(file.FileSize, Is.EqualTo(item.fileSize));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_lottie_sticker_takes_its_first_frame_and_the_clients_outline(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack    = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "lottie", ct);
        var tgs     = Tgs();
        var outline = OutlineCodec.Encode("M10,10l200,0l0,200z");

        var unthumbed = await UploadAsync(owner, spaceId, ExpressionKind.Sticker, ExpressionFormat.Lottie, tgs, "application/x-tgsticker", ct);
        var refused   = await ExpressionsOf(owner).AddItem(spaceId, pack.packId, unthumbed, null, "spin", Wave, NoKeywords, null, ct);

        var blob  = await UploadAsync(owner, spaceId, ExpressionKind.Sticker, ExpressionFormat.Lottie, tgs, "application/x-tgsticker", ct);
        var thumb = await UploadAsync(owner, spaceId, ExpressionKind.Sticker, ExpressionFormat.Static, StickerWebp, "image/webp", ct);
        var item  = Ok(await ExpressionsOf(owner).AddItem(spaceId, pack.packId, blob, thumb, "spin", Wave, NoKeywords, new IonBytes(outline), ct));

        var stored = await StoredContentTypeAsync(item.fileId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(refused), Is.EqualTo(ExpressionError.INVALID_FORMAT), "an animated sticker without a first frame was taken");
            Assert.That(item.format, Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That((item.width, item.height), Is.EqualTo((512, 512)));
            Assert.That(item.thumbFileId, Is.Not.Null);
            Assert.That(item.thumbUrl, Is.Not.Null);
            Assert.That(item.outline?.ToArray(), Is.EqualTo(outline));
            Assert.That(stored, Is.EqualTo(ExpressionContentTypes.Tgs));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Emoji_names_are_checked_and_unique_in_the_space(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack  = await PackAsync(owner, spaceId, ExpressionKind.Emoji, "faces", ct);
        var other = await PackAsync(owner, spaceId, ExpressionKind.Emoji, "more", ct);

        var party = Ok(await AddEmojiAsync(owner, spaceId, pack.packId, "party", ct));
        var again = await AddEmojiAsync(owner, spaceId, other.packId, "party", ct);
        var bad   = await AddEmojiAsync(owner, spaceId, pack.packId, "Bad Name", ct);
        var slug  = await ExpressionsOf(owner).CreatePack(spaceId, ExpressionKind.Sticker, "Faces", "faces", ct);

        Assert.Multiple(() =>
        {
            Assert.That((party.width, party.height), Is.EqualTo((100, 100)));
            Assert.That(ErrorOf(again), Is.EqualTo(ExpressionError.NAME_TAKEN));
            Assert.That(ErrorOf(bad), Is.EqualTo(ExpressionError.INVALID_FORMAT));
            Assert.That(ErrorOf(slug), Is.EqualTo(ExpressionError.NAME_TAKEN), "a pack slug is unique in the space");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_space_without_boosts_holds_six_stickers(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack  = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "full", ct);
        var slots = Services.GetRequiredService<IOptions<ExpressionsOptions>>().Value.SlotsFor(ExpressionKind.Sticker, 0);

        // Signed while there was room; added once there is none.
        var late = await UploadAsync(owner, spaceId, ExpressionKind.Sticker, ExpressionFormat.Static, StickerWebp, "image/webp", ct);

        for (var i = 0; i < slots; i++)
            Ok(await AddStickerAsync(owner, spaceId, pack.packId, ct, $"s{i}"));

        var over  = await ExpressionsOf(owner).AddItem(spaceId, pack.packId, late, null, "late", Wave, NoKeywords, null, ct);
        var begin = await BeginAsync(owner, spaceId, ExpressionKind.Sticker, ExpressionFormat.Static, StickerWebp, "image/webp", ct);

        Assert.Multiple(() =>
        {
            Assert.That(slots, Is.EqualTo(6));
            Assert.That(ErrorOf(over), Is.EqualTo(ExpressionError.QUOTA_EXCEEDED));
            Assert.That(begin, Is.InstanceOf<FailedUploadFile>(), "an upload was signed for a space with no room left");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_rejected_upload_is_refused_and_its_file_released(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "denied", ct);
        var blob = await UploadAsync(owner, spaceId, ExpressionKind.Sticker, ExpressionFormat.Static, StickerWebp, "image/webp", ct);

        await using var db = await DbAsync(ct);
        var file = await db.Files.AsNoTracking().Where(f => db.FileBlobs.Any(b => b.Id == blob && b.FileId == f.Id)).SingleAsync(ct);

        Services.GetRequiredService<FakeContentModeration>().Deny(file.S3Key, new Dictionary<string, float> { ["porn"] = 0.99f });

        var result = await ExpressionsOf(owner).AddItem(spaceId, pack.packId, blob, null, "nope", Wave, NoKeywords, null, ct);

        await using var scope = Services.CreateAsyncScope();
        var refs      = await scope.ServiceProvider.GetRequiredService<IReferenceCountService>().GetRefCountAsync(file.Id, ct);
        var violation = await db.ContentViolations.AnyAsync(v => v.FileId == file.Id, ct);
        var listed    = (await SnapshotAsync(owner, spaceId, null, ct)).packs!.Value.Single();

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(result), Is.EqualTo(ExpressionError.CONTENT_REJECTED));
            Assert.That(refs, Is.Zero, "the rejected file still holds its reference");
            Assert.That(violation, Is.True, "the rejection was not recorded");
            Assert.That(listed.items, Is.Empty);
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_patch_changes_only_the_fields_it_carries(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack  = await PackAsync(owner, spaceId, ExpressionKind.Emoji, "first", ct);
        await PackAsync(owner, spaceId, ExpressionKind.Emoji, "second", ct);
        var party = Ok(await AddEmojiAsync(owner, spaceId, pack.packId, "party", ct));

        var expressions = ExpressionsOf(owner);

        var titled  = Ok(await expressions.UpdatePack(spaceId, pack.packId, new IonPartial<ExpressionPack>().Modify(x => x.title, "Renamed"), ct));
        var covered = Ok(await expressions.UpdatePack(spaceId, pack.packId, new IonPartial<ExpressionPack>().Modify(x => x.coverItemId, party.itemId), ct));
        var foreign = await expressions.UpdatePack(spaceId, pack.packId, new IonPartial<ExpressionPack>().Modify(x => x.coverItemId, Guid.NewGuid()), ct);
        var taken   = await expressions.UpdatePack(spaceId, pack.packId, new IonPartial<ExpressionPack>().Modify(x => x.slug, "second"), ct);
        var cleared = await expressions.UpdatePack(spaceId, pack.packId, new IonPartial<ExpressionPack>().Remove(x => x.title), ct);

        var renamed = Ok(await expressions.UpdateItem(spaceId, party.itemId,
            new IonPartial<ExpressionItem>().Modify(x => x.name, "fiesta").Modify(x => x.textColor, true), ct));

        Assert.Multiple(() =>
        {
            Assert.That(titled.title, Is.EqualTo("Renamed"));
            Assert.That(titled.slug, Is.EqualTo("first"), "an untouched field changed");
            Assert.That(covered.coverItemId, Is.EqualTo(party.itemId));
            Assert.That(covered.title, Is.EqualTo("Renamed"));
            Assert.That(covered.version, Is.GreaterThan(titled.version));
            Assert.That(ErrorOf(foreign), Is.EqualTo(ExpressionError.NOT_FOUND));
            Assert.That(ErrorOf(taken), Is.EqualTo(ExpressionError.NAME_TAKEN));
            Assert.That(ErrorOf(cleared), Is.EqualTo(ExpressionError.INVALID_FORMAT));
            Assert.That(renamed.name, Is.EqualTo("fiesta"));
            Assert.That(renamed.textColor, Is.True);
            Assert.That(renamed.emoji.Values, Is.EqualTo(Wave.Values));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Packs_and_items_are_reordered_by_a_manager(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var a = await PackAsync(owner, spaceId, ExpressionKind.Emoji, "a", ct);
        var b = await PackAsync(owner, spaceId, ExpressionKind.Emoji, "b", ct);
        var x = Ok(await AddEmojiAsync(owner, spaceId, a.packId, "xx", ct));
        var y = Ok(await AddEmojiAsync(owner, spaceId, a.packId, "yy", ct));

        var expressions = ExpressionsOf(owner);

        var packs   = await expressions.ReorderPacks(spaceId, ExpressionKind.Emoji, new IonArray<Guid>([b.packId, a.packId]), ct);
        var items   = await expressions.ReorderItems(spaceId, a.packId, new IonArray<Guid>([y.itemId, x.itemId]), ct);
        var partial = await expressions.ReorderPacks(spaceId, ExpressionKind.Emoji, new IonArray<Guid>([a.packId]), ct);

        var listed = (await SnapshotAsync(owner, spaceId, null, ct)).packs!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(packs, Is.InstanceOf<SuccessReorder>());
            Assert.That(items, Is.InstanceOf<SuccessReorder>());
            Assert.That(ErrorOf(partial), Is.EqualTo(ExpressionError.INVALID_FORMAT), "a reorder has to name every pack");
            Assert.That(listed.Select(p => p.packId), Is.EqualTo(new[] { b.packId, a.packId }));
            Assert.That(listed[1].items.Select(i => i.itemId), Is.EqualTo(new[] { y.itemId, x.itemId }));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Changes_past_the_minute_budget_are_refused(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack   = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "busy", ct);
        var budget = Services.GetRequiredService<IOptions<ExpressionsOptions>>().Value.MutationsPerMinute;

        IPackResult last = new SuccessPack(pack);
        var accepted = 0;

        for (var i = 0; i < budget + 5 && last is SuccessPack; i++)
        {
            last = await ExpressionsOf(owner).UpdatePack(spaceId, pack.packId, new IonPartial<ExpressionPack>().Modify(x => x.title, $"Title {i}"), ct);
            if (last is SuccessPack)
                accepted++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(last), Is.EqualTo(ExpressionError.RATE_LIMITED));
            Assert.That(accepted, Is.EqualTo(budget - 1), "creating the pack is one of the minute's changes");
        });
    }

    // ── permissions ─────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task Creating_takes_CreateExpressions_and_changing_anothers_takes_ManageExpressions(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var plain    = await MemberWithAsync(owner, spaceId, null, ct);
        var creator  = await MemberWithAsync(owner, spaceId, ArgonEntitlement.CreateExpressions, ct);
        var manager  = await MemberWithAsync(owner, spaceId, ArgonEntitlement.ManageExpressions, ct);
        var stranger = await CreateSessionAsync(ct);

        var ownersPack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "owners", ct);

        var plainCreate  = await ExpressionsOf(plain).CreatePack(spaceId, ExpressionKind.Sticker, "Mine", "mine", ct);
        var plainUpload  = await BeginAsync(plain, spaceId, ExpressionKind.Sticker, ExpressionFormat.Static, StickerWebp, "image/webp", ct);
        var creatorsPack = Ok(await ExpressionsOf(creator).CreatePack(spaceId, ExpressionKind.Sticker, "Creator's", "creators", ct));
        var foreignByCreator = await ExpressionsOf(creator).DeletePack(spaceId, ownersPack.packId, ct);
        var reorderByCreator = await ExpressionsOf(creator).ReorderPacks(spaceId, ExpressionKind.Sticker,
            new IonArray<Guid>([creatorsPack.packId, ownersPack.packId]), ct);
        var ownByCreator     = await ExpressionsOf(creator).UpdatePack(spaceId, creatorsPack.packId,
            new IonPartial<ExpressionPack>().Modify(x => x.title, "Still mine"), ct);
        var foreignByManager = await ExpressionsOf(manager).DeletePack(spaceId, ownersPack.packId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ErrorOf(plainCreate), Is.EqualTo(ExpressionError.FORBIDDEN));
            Assert.That(plainUpload, Is.InstanceOf<FailedUploadFile>());
            Assert.That(ErrorOf(foreignByCreator), Is.EqualTo(ExpressionError.FORBIDDEN));
            Assert.That(ErrorOf(reorderByCreator), Is.EqualTo(ExpressionError.FORBIDDEN));
            Assert.That(ownByCreator, Is.InstanceOf<SuccessPack>());
            Assert.That(foreignByManager, Is.InstanceOf<SuccessPack>());
            Assert.That(async () => await SnapshotAsync(stranger, spaceId, null, ct), Throws.Exception, "a stranger read the space's packs");
        });

        var left = (await SnapshotAsync(plain, spaceId, null, ct)).packs!.Value;
        Assert.That(left.Select(p => p.packId), Is.EqualTo(new[] { creatorsPack.packId }), "a plain member reads the packs");
    }

    // ── snapshot and events ─────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task A_known_token_is_answered_without_packs_until_something_changes(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);

        var empty = await SnapshotAsync(owner, spaceId, null, ct);
        var same  = await SnapshotAsync(owner, spaceId, empty.version, ct);

        await PackAsync(owner, spaceId, ExpressionKind.Emoji, "new", ct);

        var changed = await SnapshotAsync(owner, spaceId, empty.version, ct);

        Assert.Multiple(() =>
        {
            Assert.That(empty.packs, Is.Not.Null);
            Assert.That(empty.packs!.Value, Is.Empty);
            Assert.That(same.version, Is.EqualTo(empty.version));
            Assert.That(same.packs, Is.Null, "the packs were sent again to a caller that had them");
            Assert.That(changed.version, Is.Not.EqualTo(empty.version));
            Assert.That(changed.packs!.Value, Has.Count.EqualTo(1));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task An_added_item_is_announced_with_the_token_it_follows(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "live", ct);

        await using var hub = await RealtimeClient.ConnectAsync(owner, ct);

        var before = (await SnapshotAsync(owner, spaceId, null, ct)).version;
        var mark   = hub.Mark();
        var item   = Ok(await AddStickerAsync(owner, spaceId, pack.packId, ct));
        var after  = (await SnapshotAsync(owner, spaceId, null, ct)).version;

        var changed = await hub.WaitForAsync<SpaceExpressionsChanged>(e => e.spaceId == spaceId && e.delta is ItemUpserted, EventWait, mark, ct);

        Assert.Multiple(() =>
        {
            Assert.That(changed.baseVersion, Is.EqualTo(before));
            Assert.That(changed.version, Is.EqualTo(after));
            Assert.That(((ItemUpserted)changed.delta!).item.itemId, Is.EqualTo(item.itemId));
            Assert.That(((ItemUpserted)changed.delta!).item.outline, Is.Not.Null);
        });
    }

    // ── messages ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task A_sticker_message_carries_the_servers_copy_of_the_item(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var pack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "send", ct);
        var item = Ok(await AddStickerAsync(owner, spaceId, pack.packId, ct));

        var sent     = await owner.Channels.SendMessage(spaceId, channelId, "", new IonArray<IMessageEntity>([StickerEntity(item)]), NextRandomId(), null, ct).Ok();
        var withText = await owner.Channels.SendMessage(spaceId, channelId, "look", new IonArray<IMessageEntity>([StickerEntity(item)]), NextRandomId(), null, ct).Ok();
        var unknown  = await owner.Channels.SendMessage(spaceId, channelId, "",
            new IonArray<IMessageEntity>([StickerEntity(item with { itemId = Guid.NewGuid() })]), NextRandomId(), null, ct);

        var stored  = await StoredMessageAsync(spaceId, channelId, sent, ct);
        var read    = await ReadAsync(owner, spaceId, channelId, sent, ct);
        var texted  = await ReadAsync(owner, spaceId, channelId, withText, ct);
        var sticker = (MessageEntitySticker)stored!.Entities!.Single();
        var served  = (MessageEntitySticker)read.entities.Values.Single();

        Assert.Multiple(() =>
        {
            Assert.That(sticker.packId, Is.EqualTo(pack.packId));
            Assert.That(sticker.fileId, Is.EqualTo(item.fileId));
            Assert.That(sticker.format, Is.EqualTo(ExpressionFormat.Static));
            Assert.That((sticker.width, sticker.height), Is.EqualTo((512, 512)));
            Assert.That(sticker.outline?.ToArray(), Is.EqualTo(item.outline?.ToArray()));
            Assert.That(sticker.downloadUrl, Is.Null, "a URL was stored");
            Assert.That(served.downloadUrl, Does.Contain(item.fileId.ToString()), "the read did not fill the URL");
            Assert.That(texted.text, Is.EqualTo("look"));
            Assert.That(texted.entities.Values, Is.Empty, "a sticker with text was kept");
            Assert.That(unknown, Is.EqualTo(new FailedSendMessage(SendMessageError.INVALID_DATA)));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_custom_emoji_is_kept_only_over_its_name_and_only_from_this_space(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var pack  = await PackAsync(owner, spaceId, ExpressionKind.Emoji, "emoji", ct);
        var party = Ok(await AddEmojiAsync(owner, spaceId, pack.packId, "party", ct));

        var otherSpace = await CreateSpaceAsync(owner, ct);
        var otherPack  = await PackAsync(owner, otherSpace, ExpressionKind.Emoji, "emoji", ct);
        var foreign    = Ok(await AddEmojiAsync(owner, otherSpace, otherPack.packId, "party", ct));

        const string text = "hi :party: all";

        var kept    = await owner.Channels.SendMessage(spaceId, channelId, text, new IonArray<IMessageEntity>([EmojiEntity(party, 3, 7)]), NextRandomId(), null, ct).Ok();
        var shifted = await owner.Channels.SendMessage(spaceId, channelId, text, new IonArray<IMessageEntity>([EmojiEntity(party, 2, 7)]), NextRandomId(), null, ct).Ok();
        var other   = await owner.Channels.SendMessage(spaceId, channelId, text, new IonArray<IMessageEntity>([EmojiEntity(foreign, 3, 7)]), NextRandomId(), null, ct).Ok();

        var emoji       = (MessageEntityCustomEmoji)(await ReadAsync(owner, spaceId, channelId, kept, ct)).entities.Values.Single();
        var shiftedRead = await ReadAsync(owner, spaceId, channelId, shifted, ct);
        var otherRead   = await ReadAsync(owner, spaceId, channelId, other, ct);

        Assert.Multiple(() =>
        {
            Assert.That(emoji.name, Is.EqualTo("party"));
            Assert.That(emoji.fileId, Is.EqualTo(party.fileId));
            Assert.That(emoji.spaceId, Is.EqualTo(spaceId));
            Assert.That(emoji.format, Is.EqualTo(ExpressionFormat.Static));
            Assert.That(emoji.downloadUrl, Does.Contain(party.fileId.ToString()));
            Assert.That(shiftedRead.entities.Values, Is.Empty);
            Assert.That(otherRead.entities.Values, Is.Empty, "another space's emoji was kept");
            Assert.That(otherRead.text, Is.EqualTo(text));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_custom_emoji_reaction_is_keyed_by_its_item(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var pack  = await PackAsync(owner, spaceId, ExpressionKind.Emoji, "react", ct);
        var party = Ok(await AddEmojiAsync(owner, spaceId, pack.packId, "party", ct));

        var messageId = await owner.Channels.SendMessage(spaceId, channelId, "react", new IonArray<IMessageEntity>([]), NextRandomId(), null, ct).Ok();

        await using var hub = await RealtimeClient.ConnectAsync(owner, ct);
        await hub.SubscribeToChannel(channelId, ct);

        var added   = await owner.Channels.AddCustomReaction(spaceId, channelId, messageId, party.itemId, ct);
        var twice   = await owner.Channels.AddCustomReaction(spaceId, channelId, messageId, party.itemId, ct);
        var unknown = await owner.Channels.AddCustomReaction(spaceId, channelId, messageId, Guid.NewGuid(), ct);
        var plain   = await owner.Channels.RemoveReaction(spaceId, channelId, messageId, ":party:", ct);

        var reactions = (await owner.Channels.BatchGetReactions(spaceId, channelId, new IonArray<long>([messageId]), ct)).Values.Single().reactions.Values;

        var removed = await owner.Channels.RemoveCustomReaction(spaceId, channelId, messageId, party.itemId, ct);
        var gone    = await hub.WaitForAsync<ReactionRemoved>(e => e.messageId == messageId, EventWait, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.InstanceOf<SuccessAddReaction>());
            Assert.That(twice, Is.EqualTo(new FailedAddReaction(AddReactionError.ALREADY_REACTED)));
            Assert.That(unknown, Is.InstanceOf<FailedAddReaction>());
            Assert.That(plain, Is.EqualTo(new FailedRemoveReaction(RemoveReactionError.REACTION_NOT_FOUND)),
                "a unicode removal took away a custom reaction");
            Assert.That(reactions.Single().emoji, Is.EqualTo(":party:"));
            Assert.That(reactions.Single().customEmojiId, Is.EqualTo(party.itemId));
            Assert.That(removed, Is.InstanceOf<SuccessRemoveReaction>());
            Assert.That(gone.customEmojiId, Is.EqualTo(party.itemId));
            Assert.That(gone.emoji, Is.EqualTo(":party:"));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_deleted_item_leaves_the_packs_but_not_the_messages_that_carry_it(CancellationToken ct = default)
    {
        var (owner, spaceId, channelId) = await RoomAsync(ct);
        var pack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "gone", ct);
        var item = Ok(await AddStickerAsync(owner, spaceId, pack.packId, ct));

        var old = await owner.Channels.SendMessage(spaceId, channelId, "", new IonArray<IMessageEntity>([StickerEntity(item)]), NextRandomId(), null, ct).Ok();

        Ok(await ExpressionsOf(owner).DeleteItem(spaceId, item.itemId, ct));

        var after = await owner.Channels.SendMessage(spaceId, channelId, "", new IonArray<IMessageEntity>([StickerEntity(item)]), NextRandomId(), null, ct);
        var listed  = (await SnapshotAsync(owner, spaceId, null, ct)).packs!.Value.Single();
        var oldRead = await ReadAsync(owner, spaceId, channelId, old, ct);

        Assert.Multiple(() =>
        {
            Assert.That(listed.items, Is.Empty);
            Assert.That(oldRead.entities.Values.Single(), Is.InstanceOf<MessageEntitySticker>(), "an old message lost its sticker");
            Assert.That(after, Is.EqualTo(new FailedSendMessage(SendMessageError.INVALID_DATA)), "a deleted sticker was sent");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_direct_message_takes_a_sticker_only_from_a_space_the_sender_is_in(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack     = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "dm", ct);
        var item     = Ok(await AddStickerAsync(owner, spaceId, pack.packId, ct));
        var peer     = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        await owner.Chats.SendDirectMessage(peer.UserId, "", new IonArray<IMessageEntity>([StickerEntity(item)]), NextRandomId(), null, ct);

        var received = (await peer.Chats.QueryDirectMessages(owner.UserId, null, 10, ct)).Values.Single();

        Assert.Multiple(() =>
        {
            Assert.That(((MessageEntitySticker)received.entities.Values.Single()).fileId, Is.EqualTo(item.fileId));
            Assert.That(async () => await stranger.Chats.SendDirectMessage(peer.UserId, "",
                    new IonArray<IMessageEntity>([StickerEntity(item)]), NextRandomId(), null, ct),
                Throws.Exception, "a sticker of a space the sender is not in was sent");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Deleting_the_space_soft_deletes_its_packs_and_releases_their_files(CancellationToken ct = default)
    {
        var (owner, spaceId, _) = await RoomAsync(ct);
        var pack = await PackAsync(owner, spaceId, ExpressionKind.Sticker, "doomed", ct);
        var item = Ok(await AddStickerAsync(owner, spaceId, pack.packId, ct));

        await Grains.GetGrain<ISpaceGrain>(spaceId).DeleteSpace();

        await using var db    = await DbAsync(ct);
        await using var scope = Services.CreateAsyncScope();

        var packs = await db.ExpressionPacks.IgnoreQueryFilters().AsNoTracking().Where(p => p.SpaceId == spaceId).ToListAsync(ct);
        var items = await db.ExpressionItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.SpaceId == spaceId).ToListAsync(ct);
        var refs  = await scope.ServiceProvider.GetRequiredService<IReferenceCountService>().GetRefCountAsync(item.fileId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(packs.Select(p => (p.Id, p.IsDeleted)), Is.EqualTo(new[] { (pack.packId, true) }));
            Assert.That(items.Select(i => (i.Id, i.IsDeleted)), Is.EqualTo(new[] { (item.itemId, true) }));
            Assert.That(refs, Is.Zero, "the space is gone and its sticker still holds its file");
        });
    }
}
