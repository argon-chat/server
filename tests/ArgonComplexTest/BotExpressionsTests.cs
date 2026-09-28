namespace ArgonComplexTest.Tests;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.BotApi;
using Argon.Features.Expressions;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using static ChannelTestKit;

/// <summary>
/// The Bot API for stickers and custom emoji: IFiles uploads taken by fileId, inline attach:// parts,
/// idempotent creates and adds, the bots' own budget, the change events, and what a bot may never do.
/// </summary>
/// <remarks>
/// Every test seeds and installs its own bot, the way <c>SpaceBotTests</c> does: the install is what grants
/// the bot its required entitlements, through the locked <c>Bot: {name}</c> role.
/// </remarks>
[TestFixture]
public class BotExpressionsTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(30);

    private const ArgonEntitlement Creates = ArgonEntitlement.ViewChannel | ArgonEntitlement.CreateExpressions;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private sealed record Bot(Guid AppId, Guid UserId, string Token);

    private sealed record Answer(HttpStatusCode Status, JsonElement Body, TimeSpan? RetryAfter)
    {
        public string? Error => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("error", out var e) ? e.GetString() : null;

        public override string ToString() => $"{(int)Status} {Body}";
    }

    // ── files ───────────────────────────────────────────────────────────────────────────────────

    private static Image<Rgba32> Disc(int side)
    {
        var image = new Image<Rgba32>(side, side, new Rgba32(0, 0, 0, 0));
        var r     = side * 0.4;
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
            if (Math.Pow(x + 0.5 - side / 2.0, 2) + Math.Pow(y + 0.5 - side / 2.0, 2) <= r * r)
                image[x, y] = new Rgba32(40, 160, 220, 255);
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

    /// <summary>A 3 s, 60 fps, 512×512 animation, gzipped the way a .tgs is, with Telegram's root marker.</summary>
    private static byte[] Tgs()
    {
        const string json = """
        {"tgs":1,"v":"5.7.4","fr":60,"ip":0,"op":180,"w":512,"h":512,"nm":"test","ddd":0,"assets":[],
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

    private static readonly byte[] StickerWebp = Webp(512);
    private static readonly byte[] EmojiPng    = Png(100);

    /// <summary>The 512×512 VP9 red square with alpha that <c>SpaceExpressionTests</c> uses.</summary>
    private static readonly byte[] SquareWebm = Convert.FromBase64String(
        "GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQJChYECGFOAZwEAAAAAAAMBEU2bdLpNu4tTq4QVSalmU6yBoU27i1OrhBZUrmtTrIHGTbuMU6uEElTD" +
        "Z1OsggEZTbuMU6uEHFO7a1OsggLr7AEAAAAAAABZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmoCrXsYMPQkBNgIRMYXZmV0GETGF2ZkSJiEBeAAAAAAAAFlSua86uAQAAAAAAAEXXgQFzxYgAAAAA" +
        "AAAAAZyBACK1nIN1bmSIgQCGhVZfVlA5g4EBI+ODhAJiWgDglrCCAgC6ggIAmoECU8CBAVWwhFW5gQESVMNn1XNz0mPAi2PFiAAAAAAAAAABZ8icRaOHRU5D" +
        "T0RFUkSHj0xhdmMgbGlidnB4LXZwOWfIokWjiERVUkFUSU9ORIeUMDA6MDA6MDAuMTIwMDAwMDAwAAAfQ7Z1QXLngQCgQNih3IEAAACCSYNCAB/wH/YAOCQc" +
        "GD4QAFBh9jr2gFzR7gAAAAAAHGb/1d////Xnz/////2oBDqBWFJHkR4cePHhwABm/9Xf///158/////9qAQ6gVhSR5EeHHjx4cAAdaH3pvXugQGl8IJJg0IA" +
        "H/Af9gA4JBwYPhAAcG+x17QAulX/gB779AAAAAAAIWcb9MRKvnbmWM6oFDKlz+hj8Jn5i6azeHeam9PgCSC4AGcb9MRKvnb7s/t7tK0xHQ+GqCkISQYizj6M" +
        "n+CDVhS2zN9Liujm3hS/HACgy6GhgQAoAIYAQJKcAE8BAAMAYNWFGwAAAAAABGcRzABnEcwAdaGipqDugQGlm4YAQJKcAE8BAAMAYNWFGwAAAAAAA2mlWGml" +
        "WPuB2KDFoZ6BAFAAhgBAkpwATaEAAYBgAAAAAAAEZxHMAGcRzAB1oZ+mne6BAaWYhgBAkpwATaEAAYBgAAAAAAADaaVYaaVY+4HYHFO7a5G7j7OBALeK94EB" +
        "8YIBc/CBAw==");

    // ── bots ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<Bot> SeedBotAsync(TestUserSession developer, ArgonEntitlement required, CancellationToken ct)
    {
        var botUserId = Guid.NewGuid();
        var botAppId  = Guid.NewGuid();
        var teamId    = Guid.NewGuid();
        var token     = BotToken(botAppId);
        var name      = SpaceGroupSupport.Unique("Stickers");

        await using var db = await DbAsync(ct);

        db.Users.Add(new UserEntity
        {
            Id          = botUserId,
            Username    = $"xbot_{botUserId:N}"[..32],
            DisplayName = name,
            Email       = $"xbot_{botUserId:N}@test.local",
            AgreeTOS    = true,
            DateOfBirth = new DateOnly(2000, 1, 1),
            BotEntityId = botAppId
        });
        db.TeamEntities.Add(new DevTeamEntity { TeamId = teamId, OwnerId = developer.UserId, Name = "Sticker bot team" });
        db.MemberTeamEntities.Add(new DevTeamMemberEntity
        {
            TeamId = teamId, UserId = developer.UserId, JoinedAt = DateTime.UtcNow, IsOwner = true
        });
        db.BotEntities.Add(new BotEntity
        {
            AppId                = botAppId,
            TeamId               = teamId,
            Name                 = name,
            ClientId             = Guid.NewGuid().ToString(),
            ClientSecret         = Guid.NewGuid().ToString(),
            AppType              = DevAppType.Bot,
            BotToken             = token,
            BotAsUserId          = botUserId,
            LifecycleState       = BotLifecycleState.Published,
            MaxSpaces            = 100,
            RequiredEntitlements = required,
            RequiredScopes       = [],
            AllowedRedirects     = []
        });

        await db.SaveChangesAsync(ct);
        return new Bot(botAppId, botUserId, token);
    }

    private static string BotToken(Guid botAppId)
    {
        Span<byte> app = stackalloc byte[16];
        botAppId.TryWriteBytes(app);
        app.Reverse();

        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"{Convert.ToHexString(app)}:{secret}";
    }

    private async Task<(TestUserSession Owner, Guid SpaceId, Bot Bot)> RoomAsync(CancellationToken ct, ArgonEntitlement required = Creates)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var bot     = await SeedBotAsync(owner, required, ct);

        var installed = await SpaceGroupSupport.Bots(owner).InstallBot(spaceId, bot.AppId, ct);
        Assert.That(installed, Is.InstanceOf<SuccessInstallBot>(), $"could not install the bot: {(installed as FailedInstallBot)?.error}");

        return (owner, spaceId, bot);
    }

    // ── http ────────────────────────────────────────────────────────────────────────────────────

    private static async Task<Answer> SendAsync(Bot bot, HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"/api/bot{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", bot.Token);

        using var response = await ArgonTestEnvironment.Instance.HttpClient.SendAsync(request, ct);
        var       text     = await response.Content.ReadAsStringAsync(ct);

        return new Answer(response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone(),
            response.Headers.RetryAfter?.Delta);
    }

    private static Task<Answer> GetAsync(Bot bot, string path, CancellationToken ct)
        => SendAsync(bot, HttpMethod.Get, path, null, ct);

    private static Task<Answer> JsonAsync(Bot bot, HttpMethod method, string path, object body, CancellationToken ct)
        => SendAsync(bot, method, path, new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"), ct);

    private static JsonElement Ok(Answer answer)
    {
        Assert.That(answer.Status, Is.EqualTo(HttpStatusCode.OK), answer.ToString());
        return answer.Body;
    }

    private static void Refused(Answer answer, HttpStatusCode status, string error, string? because = null)
    {
        Assert.That(answer.Status, Is.EqualTo(status), $"{because} {answer}");
        Assert.That(answer.Error, Is.EqualTo(error), $"{because} {answer}");
    }

    private static Task<Answer> CreatePackAsync(Bot bot, Guid spaceId, string kind, string slug, CancellationToken ct)
        => JsonAsync(bot, HttpMethod.Post, "/IExpressions/v1/CreatePack", new { spaceId, kind, title = $"Pack {slug}", slug }, ct);

    private static MultipartFormDataContent ItemForm(Guid spaceId, Guid packId, string name, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(spaceId.ToString()), "spaceId" },
            { new StringContent(packId.ToString()), "packId" },
            { new StringContent(name), "name" }
        };

        foreach (var (field, value) in fields)
            form.Add(new StringContent(value), field);

        return form;
    }

    private static MultipartFormDataContent Part(MultipartFormDataContent form, string name, byte[] data, string contentType, string fileName)
    {
        var part = new ByteArrayContent(data);
        part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(part, name, fileName);
        return form;
    }

    private static Task<Answer> AddItemAsync(Bot bot, MultipartFormDataContent form, CancellationToken ct)
        => SendAsync(bot, HttpMethod.Post, "/IExpressions/v1/AddItem", form, ct);

    private static Task<Answer> AddStickerAsync(Bot bot, Guid spaceId, Guid packId, string name, CancellationToken ct, params (string, string)[] fields)
        => AddItemAsync(bot, Part(ItemForm(spaceId, packId, name, fields), "file", StickerWebp, "image/webp", "sticker.webp"), ct);

    private static Guid PackIdOf(JsonElement pack) => pack.GetProperty("packId").GetGuid();

    /// <summary>A file URL is <c>{api}/files/{fileId}</c>.</summary>
    private static Guid FileIdOf(string? url) => Guid.Parse(url![(url.LastIndexOf('/') + 1)..]);

    private static ISpaceExpressionInteraction ExpressionsOf(TestUserSession session)
        => session.Client.ForService<ISpaceExpressionInteraction>(Services);

    private static async Task<ExpressionItem> OwnersEmojiAsync(TestUserSession owner, Guid spaceId, Guid packId, string name, CancellationToken ct)
    {
        var begun = await ExpressionsOf(owner).BeginUploadExpression(spaceId, ExpressionKind.Emoji, ExpressionFormat.Static, "image/png",
            EmojiPng.Length, ct);
        Assert.That(begun, Is.InstanceOf<SuccessUploadFile>(), $"the upload was not signed: {(begun as FailedUploadFile)?.error}");

        var ticket = (SuccessUploadFile)begun;
        using (var put = await TestObjectStore.UploadAsync(ticket.uploadUrl, EmojiPng, "image/png", ticket.formFields.Values.Select(f => (f.key, f.value))))
            Assert.That(put.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var added = await ExpressionsOf(owner).AddItem(spaceId, packId, ticket.blobId, null, name, new IonArray<string>(["🙂"]),
            IonArray<string>.Empty, null, ct);
        Assert.That(added, Is.InstanceOf<SuccessItem>(), $"refused: {(added as FailedItem)?.error}");
        return ((SuccessItem)added).item;
    }

    private static async Task<ExpressionPack> OwnersPackAsync(TestUserSession owner, Guid spaceId, ExpressionKind kind, string slug, CancellationToken ct)
    {
        var created = await ExpressionsOf(owner).CreatePack(spaceId, kind, $"Pack {slug}", slug, ct);
        Assert.That(created, Is.InstanceOf<SuccessPack>(), $"refused: {(created as FailedPack)?.error}");
        return ((SuccessPack)created).pack;
    }

    // ── files ───────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task An_upload_is_taken_by_its_file_id_and_can_be_taken_again(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct);

        using var upload = Part(new MultipartFormDataContent { { new StringContent("sticker"), "purpose" } }, "file", StickerWebp, "image/webp",
            "wave.webp");
        var file   = Ok(await SendAsync(bot, HttpMethod.Post, "/IFiles/v1/Upload", upload, ct));
        var fileId = file.GetProperty("fileId").GetGuid();
        var got    = Ok(await GetAsync(bot, $"/IFiles/v1/Get?fileId={fileId}", ct));

        var pack   = Ok(await CreatePackAsync(bot, spaceId, "sticker", "uploads", ct));
        var item   = Ok(await AddItemAsync(bot, ItemForm(spaceId, PackIdOf(pack), "first", ("file", fileId.ToString()), ("emoji", "👋")), ct));
        var second = Ok(await AddItemAsync(bot, ItemForm(spaceId, PackIdOf(pack), "second", ("file", fileId.ToString())), ct));

        using var thumbWebp = Part(new MultipartFormDataContent { { new StringContent("thumb"), "purpose" } }, "file", StickerWebp,
            "image/webp", "t.webp");
        var noThumbs = await SendAsync(bot, HttpMethod.Post, "/IFiles/v1/Upload", thumbWebp, ct);
        var unknown  = await GetAsync(bot, $"/IFiles/v1/Get?fileId={Guid.NewGuid()}", ct);
        var missing = await AddItemAsync(bot, ItemForm(spaceId, PackIdOf(pack), "ghost", ("file", Guid.NewGuid().ToString())), ct);

        Assert.Multiple(() =>
        {
            Assert.That(file.GetProperty("contentType").GetString(), Is.EqualTo("image/webp"));
            Assert.That(file.GetProperty("size").GetInt64(), Is.EqualTo(StickerWebp.Length));
            Assert.That(got.GetProperty("url").GetString(), Does.EndWith(fileId.ToString()));
            Assert.That(item.GetProperty("kind").GetString(), Is.EqualTo("sticker"));
            Assert.That(item.GetProperty("format").GetString(), Is.EqualTo("static"));
            Assert.That(item.GetProperty("emoji").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "👋" }));
            Assert.That(item.GetProperty("creatorId").GetGuid(), Is.EqualTo(bot.UserId));
            Assert.That(item.GetProperty("createdByBot").GetBoolean(), Is.True);
            Assert.That(FileIdOf(item.GetProperty("url").GetString()), Is.Not.EqualTo(fileId), "the item has a copy of its own");
            Assert.That(second.GetProperty("itemId").GetGuid(), Is.Not.EqualTo(item.GetProperty("itemId").GetGuid()),
                "an upload can be taken more than once");
            Assert.That(item.TryGetProperty("thumbUrl", out _), Is.False, "bots are shown no thumbnails");
            Refused(noThumbs, HttpStatusCode.BadRequest, "invalid_request", "thumb is no purpose any more");
            Refused(unknown, HttpStatusCode.NotFound, "not_found");
            Refused(missing, HttpStatusCode.NotFound, "not_found", "a fileId that names no upload");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task An_animated_sticker_from_a_bot_gets_the_servers_first_frame(CancellationToken ct = default)
    {
        Assume.That(Renders(ExpressionFormat.Lottie), "rlottie is not installed");

        var (_, spaceId, bot) = await RoomAsync(ct);
        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "drawn", ct)));

        using var form = ItemForm(spaceId, pack, "drawn", ("file", "attach://animation"));
        Part(form, "animation", Tgs(), "application/x-tgsticker", "drawn.tgs");

        var item = Ok(await AddItemAsync(bot, form, ct));
        var key  = await ThumbKeyAsync(item, ct);

        await using var stream = await Services.GetRequiredService<IS3StorageService>().GetObjectStreamAsync(key, ct);
        using var       thumb  = await Image.LoadAsync<Rgba32>(stream!, ct);

        using var dangling = ItemForm(spaceId, pack, "dangling", ("file", "attach://nowhere"));
        var unbound = await AddItemAsync(bot, dangling, ct);

        Assert.Multiple(() =>
        {
            Assert.That(item.GetProperty("format").GetString(), Is.EqualTo("lottie"));
            Assert.That((item.GetProperty("width").GetInt32(), item.GetProperty("height").GetInt32()), Is.EqualTo((512, 512)));
            Assert.That(item.TryGetProperty("thumbUrl", out _), Is.False, "bots are shown no thumbnails");
            Assert.That((thumb.Width, thumb.Height), Is.EqualTo((512, 512)));
            Assert.That(thumb[256, 256] is { R: > 240, G: < 16, B: < 16, A: 255 }, Is.True, $"the square: {thumb[256, 256]}");
            Assert.That(thumb[100, 100].A, Is.Zero, "around the square");
            Assert.That(Services.GetRequiredService<FakeContentModeration>().WasEvaluated(key), Is.True,
                "moderation did not see the first frame");
            Refused(unbound, HttpStatusCode.BadRequest, "invalid_request", "attach:// naming no part");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_video_sticker_uploaded_by_a_bot_gets_the_servers_first_frame(CancellationToken ct = default)
    {
        Assume.That(Renders(ExpressionFormat.Video), "libvpx is not installed");

        var (_, spaceId, bot) = await RoomAsync(ct);
        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "decoded", ct)));

        using var upload = Part(new MultipartFormDataContent { { new StringContent("sticker"), "purpose" } }, "file", SquareWebm, "video/webm",
            "square.webm");
        var fileId = Ok(await SendAsync(bot, HttpMethod.Post, "/IFiles/v1/Upload", upload, ct)).GetProperty("fileId").GetGuid();

        var item = Ok(await AddItemAsync(bot, ItemForm(spaceId, pack, "decoded", ("file", fileId.ToString())), ct));
        var key  = await ThumbKeyAsync(item, ct);

        await using var stream = await Services.GetRequiredService<IS3StorageService>().GetObjectStreamAsync(key, ct);
        using var       thumb  = await Image.LoadAsync<Rgba32>(stream!, ct);

        Assert.Multiple(() =>
        {
            Assert.That(item.GetProperty("format").GetString(), Is.EqualTo("video"));
            Assert.That((thumb.Width, thumb.Height), Is.EqualTo((512, 512)));
            Assert.That(thumb[256, 256] is { R: > 240, G: < 16, B: < 16, A: > 250 }, Is.True, $"the square: {thumb[256, 256]}");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Without_the_renderer_an_animated_item_is_refused_with_503(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct);
        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "undrawn", ct)));

        using var upload = Part(new MultipartFormDataContent { { new StringContent("sticker"), "purpose" } }, "file", SquareWebm, "video/webm",
            "square.webm");
        var webm = Ok(await SendAsync(bot, HttpMethod.Post, "/IFiles/v1/Upload", upload, ct)).GetProperty("fileId").GetGuid();

        Answer lottie, video, still;
        using (Services.GetRequiredService<TestFirstFrameRenderer>().Blind(bot.UserId))
        {
            using var form = ItemForm(spaceId, pack, "spin", ("file", "attach://animation"));
            Part(form, "animation", Tgs(), "application/x-tgsticker", "spin.tgs");

            lottie = await AddItemAsync(bot, form, ct);
            video  = await AddItemAsync(bot, ItemForm(spaceId, pack, "square", ("file", webm.ToString())), ct);
            still  = await AddStickerAsync(bot, spaceId, pack, "still", ct);
        }

        var listed = Ok(await GetAsync(bot, $"/IExpressions/v1/GetPack?spaceId={spaceId}&packId={pack}", ct));

        Assert.Multiple(() =>
        {
            Refused(lottie, HttpStatusCode.ServiceUnavailable, "render_unavailable", "an inline TGS");
            Refused(video, HttpStatusCode.ServiceUnavailable, "render_unavailable", "an uploaded WEBM");
            Assert.That(still.Status, Is.EqualTo(HttpStatusCode.OK), $"a static item needs no renderer: {still}");
            Assert.That(listed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()),
                Is.EqualTo(new[] { "still" }));
        });
    }

    private static bool Renders(ExpressionFormat format) => Services.GetRequiredService<IFirstFrameRenderer>().IsAvailable(format);

    /// <summary>Where the item's first frame is stored; bots are not told, so it is read from the row.</summary>
    private static async Task<string> ThumbKeyAsync(JsonElement item, CancellationToken ct)
    {
        var itemId = item.GetProperty("itemId").GetGuid();

        await using var db = await DbAsync(ct);
        var thumbId = await db.ExpressionItems.AsNoTracking().Where(i => i.Id == itemId).Select(i => i.ThumbFileId).SingleAsync(ct);

        Assert.That(thumbId, Is.Not.Null, "no first frame was stored");
        return await db.Files.AsNoTracking().Where(f => f.Id == thumbId).Select(f => f.S3Key).SingleAsync(ct);
    }

    [Test, CancelAfter(180_000)]
    public async Task A_png_is_stored_as_webp(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct);
        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "pngs", ct)));

        using var form = Part(ItemForm(spaceId, pack, "flat"), "file", Png(512), "image/png", "flat.png");
        var item = Ok(await AddItemAsync(bot, form, ct));

        await using var db = await DbAsync(ct);
        var fileId = FileIdOf(item.GetProperty("url").GetString());
        var key    = await db.Files.AsNoTracking().Where(f => f.Id == fileId).Select(f => f.S3Key).SingleAsync(ct);
        var stored = await Services.GetRequiredService<IS3StorageService>().HeadFileAsync(key, ct);

        Assert.Multiple(() =>
        {
            Assert.That(item.GetProperty("format").GetString(), Is.EqualTo("static"));
            Assert.That(stored?.ContentType, Is.EqualTo("image/webp"));
            Assert.That(stored?.CacheControl, Is.EqualTo(ExpressionUploads.CacheControl));
        });
    }

    // ── limits ──────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task A_full_space_is_refused_with_422(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct);
        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "full", ct)));

        var before = Ok(await GetAsync(bot, $"/IExpressions/v1/GetQuota?spaceId={spaceId}", ct));
        var slots  = before.GetProperty("stickers").GetProperty("max").GetInt32();

        for (var i = 0; i < slots; i++)
            Ok(await AddStickerAsync(bot, spaceId, pack, $"s{i}", ct));

        var over  = await AddStickerAsync(bot, spaceId, pack, "over", ct);
        var after = Ok(await GetAsync(bot, $"/IExpressions/v1/GetQuota?spaceId={spaceId}", ct));

        Assert.Multiple(() =>
        {
            Assert.That(slots, Is.EqualTo(6), "a space without boosts");
            Refused(over, (HttpStatusCode)422, "quota_exceeded");
            Assert.That(after.GetProperty("stickers").GetProperty("used").GetInt32(), Is.EqualTo(slots));
            Assert.That(after.GetProperty("packs").GetProperty("used").GetInt32(), Is.EqualTo(1));
            Assert.That(after.GetProperty("packs").GetProperty("max").GetInt32(), Is.EqualTo(10));
            Assert.That(after.GetProperty("emoji").GetProperty("used").GetInt32(), Is.Zero);
            Assert.That(after.GetProperty("itemsPerPack").GetProperty("sticker").GetInt32(), Is.EqualTo(120));
            Assert.That(after.GetProperty("boostLevel").GetInt32(), Is.Zero);
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_taken_name_is_refused_with_409(CancellationToken ct = default)
    {
        var (owner, spaceId, bot) = await RoomAsync(ct);
        await OwnersPackAsync(owner, spaceId, ExpressionKind.Emoji, "theirs", ct);

        var pack  = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "emoji", "party", ct)));
        var taken = await CreatePackAsync(bot, spaceId, "emoji", "theirs", ct);

        using var first = Part(ItemForm(spaceId, pack, "party"), "file", EmojiPng, "image/png", "party.png");
        Ok(await AddItemAsync(bot, first, ct));

        using var second = Part(ItemForm(spaceId, pack, "party"), "file", EmojiPng, "image/png", "party.png");
        var again = await AddItemAsync(bot, second, ct);

        Assert.Multiple(() =>
        {
            Refused(taken, HttpStatusCode.Conflict, "name_taken", "a slug a person's pack holds");
            Refused(again, HttpStatusCode.Conflict, "name_taken", "an emoji name the space holds");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Creating_a_pack_again_returns_the_one_the_bot_made(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct);

        var first  = Ok(await CreatePackAsync(bot, spaceId, "sticker", "mine", ct));
        var second = Ok(await CreatePackAsync(bot, spaceId, "sticker", "mine", ct));
        var kind   = await CreatePackAsync(bot, spaceId, "emoji", "mine", ct);

        Assert.Multiple(() =>
        {
            Assert.That(PackIdOf(second), Is.EqualTo(PackIdOf(first)));
            Assert.That(second.GetProperty("createdByBot").GetBoolean(), Is.True);
            Refused(kind, HttpStatusCode.Conflict, "name_taken", "the same slug for the other kind");
        });
    }

    // ── what a bot may do ───────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task A_bot_without_CreateExpressions_only_reads(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct, ArgonEntitlement.ViewChannel | ArgonEntitlement.SendMessages);

        var create = await CreatePackAsync(bot, spaceId, "sticker", "nope", ct);
        var list   = await GetAsync(bot, $"/IExpressions/v1/List?spaceId={spaceId}", ct);

        Assert.Multiple(() =>
        {
            Refused(create, HttpStatusCode.Forbidden, "insufficient_permissions");
            Assert.That(list.Status, Is.EqualTo(HttpStatusCode.OK), list.ToString());
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_bot_changes_only_what_it_made(CancellationToken ct = default)
    {
        var (owner, spaceId, bot) = await RoomAsync(ct);
        var theirs     = await OwnersPackAsync(owner, spaceId, ExpressionKind.Emoji, "theirs", ct);
        var theirsItem = await OwnersEmojiAsync(owner, spaceId, theirs.packId, "owners", ct);

        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "emoji", "ours", ct)));
        using var form = Part(ItemForm(spaceId, pack, "mine"), "file", EmojiPng, "image/png", "mine.png");
        var mine = Ok(await AddItemAsync(bot, form, ct)).GetProperty("itemId").GetGuid();

        var renamed = Ok(await JsonAsync(bot, HttpMethod.Patch, "/IExpressions/v1/UpdateItem",
            new { spaceId, itemId = mine, name = "still_mine", keywords = new[] { "wave" }, textColor = true }, ct));
        var retitled = Ok(await JsonAsync(bot, HttpMethod.Patch, "/IExpressions/v1/UpdatePack", new { spaceId, packId = pack, title = "Ours" }, ct));

        var foreignItem = await JsonAsync(bot, HttpMethod.Patch, "/IExpressions/v1/UpdateItem",
            new { spaceId, itemId = theirsItem.itemId, name = "taken_over" }, ct);
        var foreignPack = await JsonAsync(bot, HttpMethod.Patch, "/IExpressions/v1/UpdatePack",
            new { spaceId, packId = theirs.packId, title = "Taken over" }, ct);

        Assert.Multiple(() =>
        {
            Assert.That(renamed.GetProperty("name").GetString(), Is.EqualTo("still_mine"));
            Assert.That(renamed.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()), Is.EqualTo(new[] { "wave" }));
            Assert.That(renamed.GetProperty("textColor").GetBoolean(), Is.True);
            Assert.That(retitled.GetProperty("title").GetString(), Is.EqualTo("Ours"));
            Assert.That(retitled.GetProperty("items").GetArrayLength(), Is.EqualTo(1));
            Refused(foreignItem, HttpStatusCode.Forbidden, "insufficient_permissions", "a person's item");
            Refused(foreignPack, HttpStatusCode.Forbidden, "insufficient_permissions", "a person's pack");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task The_grain_refuses_a_bot_deleting_or_reordering_even_with_ManageExpressions(CancellationToken ct = default)
    {
        var (owner, spaceId, bot) = await RoomAsync(ct, Creates | ArgonEntitlement.ManageExpressions);
        var theirs = await OwnersPackAsync(owner, spaceId, ExpressionKind.Sticker, "theirs", ct);

        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "kept", ct)));
        var item = Ok(await AddStickerAsync(bot, spaceId, pack, "kept", ct)).GetProperty("itemId").GetGuid();

        var grain        = Grains.GetGrain<ISpaceExpressionsGrain>(spaceId);
        var deleteItem   = await AsUserAsync(bot.UserId, () => grain.DeleteItem(item));
        var deletePack   = await AsUserAsync(bot.UserId, () => grain.DeletePack(pack));
        var reorderPacks = await AsUserAsync(bot.UserId, () => grain.ReorderPacks(ExpressionKind.Sticker, [theirs.packId, pack]));
        var reorderItems = await AsUserAsync(bot.UserId, () => grain.ReorderItems(pack, [item]));
        var foreign      = await AsUserAsync(bot.UserId, () => grain.UpdatePack(theirs.packId,
            new IonPartial<ExpressionPack>().Modify(x => x.title, "Managed")));

        var listed = Ok(await GetAsync(bot, $"/IExpressions/v1/GetPack?spaceId={spaceId}&slug=kept", ct));

        await using var db = await DbAsync(ct);
        var marked = await db.Users.AsNoTracking().Where(u => u.Id == bot.UserId).Select(u => u.BotEntityId).SingleAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(marked, Is.EqualTo(bot.AppId), "setup: the bot's user carries its BotEntityId");
            Assert.That((deleteItem as FailedItem)?.error, Is.EqualTo(ExpressionError.FORBIDDEN), "DeleteItem");
            Assert.That((deletePack as FailedPack)?.error, Is.EqualTo(ExpressionError.FORBIDDEN), "DeletePack");
            Assert.That((reorderPacks as FailedReorder)?.error, Is.EqualTo(ExpressionError.FORBIDDEN), "ReorderPacks");
            Assert.That((reorderItems as FailedReorder)?.error, Is.EqualTo(ExpressionError.FORBIDDEN), "ReorderItems");
            Assert.That((foreign as FailedPack)?.error, Is.EqualTo(ExpressionError.FORBIDDEN), "ManageExpressions does not open a person's pack");
            Assert.That(listed.GetProperty("items").GetArrayLength(), Is.EqualTo(1), "nothing was deleted");
        });
    }

    // ── reading and events ──────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task Listing_with_the_current_version_leaves_the_packs_out(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct);
        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "listed", ct)));

        var full    = Ok(await GetAsync(bot, $"/IExpressions/v1/List?spaceId={spaceId}", ct));
        var version = full.GetProperty("version").GetString();
        var same    = Ok(await GetAsync(bot, $"/IExpressions/v1/List?spaceId={spaceId}&known={Uri.EscapeDataString(version!)}", ct));
        var bySlug  = Ok(await GetAsync(bot, $"/IExpressions/v1/GetPack?spaceId={spaceId}&slug=listed", ct));
        var unknown = await GetAsync(bot, $"/IExpressions/v1/GetPack?spaceId={spaceId}&slug=none", ct);

        Assert.Multiple(() =>
        {
            Assert.That(full.GetProperty("packs").EnumerateArray().Select(PackIdOf), Is.EqualTo(new[] { pack }));
            Assert.That(same.GetProperty("version").GetString(), Is.EqualTo(version));
            Assert.That(same.GetProperty("packs").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(PackIdOf(bySlug), Is.EqualTo(pack));
            Refused(unknown, HttpStatusCode.NotFound, "not_found");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task The_event_stream_carries_expression_changes_under_their_intent(CancellationToken ct = default)
    {
        var (_, spaceId, bot) = await RoomAsync(ct);

        await using var events = await ChannelTestBot.BotEvents.OpenAsync(bot.Token, ct, (long)BotIntent.Expressions);

        var pack = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "streamed", ct)));
        Ok(await AddStickerAsync(bot, spaceId, pack, "streamed", ct));

        var created = await events.WaitForAsync("expressionsUpdate",
            e => e["delta"]?["type"]?.ToString() == "packUpserted" && e["delta"]?["pack"]?["slug"]?.ToString() == "streamed", EventWait, ct);
        var added = await events.WaitForAsync("expressionsUpdate",
            e => e["delta"]?["type"]?.ToString() == "itemUpserted", EventWait, ct);

        Assert.Multiple(() =>
        {
            Assert.That(created["spaceId"]?.ToString(), Is.EqualTo(spaceId.ToString()));
            Assert.That(created["version"]?.ToString(), Is.Not.Empty);
            Assert.That(created["delta"]!["pack"]!["kind"]?.ToString(), Is.EqualTo("sticker"));
            Assert.That(added["baseVersion"]?.ToString(), Is.EqualTo(created["version"]?.ToString()));
            Assert.That(added["delta"]!["item"]!["name"]?.ToString(), Is.EqualTo("streamed"));
            Assert.That(added["delta"]!["item"]!["url"]?.ToString(), Is.Not.Empty);
            Assert.That(added["delta"]!["item"]!["createdByBot"]?.ToObject<bool>(), Is.True);
            Assert.That(added["delta"]!["item"]!["fileId"], Is.Null, "files travel as URLs");
            Assert.That(added["delta"]!["item"]!["thumbUrl"], Is.Null, "bots are shown no thumbnails");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_spent_bot_budget_answers_space_rate_limited_and_leaves_people_theirs(CancellationToken ct = default)
    {
        var (owner, spaceId, bot) = await RoomAsync(ct);
        var pack   = PackIdOf(Ok(await CreatePackAsync(bot, spaceId, "sticker", "busy", ct)));
        var budget = TestServerConfiguration.BotExpressionMutationsPerMinute;

        Answer last     = default!;
        var    accepted = 0;

        for (var i = 0; i < budget + 3; i++)
        {
            last = await JsonAsync(bot, HttpMethod.Patch, "/IExpressions/v1/UpdatePack", new { spaceId, packId = pack, title = $"Title {i}" }, ct);
            if (last.Status != HttpStatusCode.OK)
                break;
            accepted++;
        }

        var person = await OwnersPackAsync(owner, spaceId, ExpressionKind.Emoji, "people", ct);

        Assert.Multiple(() =>
        {
            Refused(last, HttpStatusCode.TooManyRequests, "space_rate_limited");
            Assert.That(last.RetryAfter, Is.Not.Null.And.GreaterThan(TimeSpan.Zero), "Retry-After");
            Assert.That(accepted, Is.EqualTo(budget - 1), "creating the pack is one of the minute's changes");
            Assert.That(person.slug, Is.EqualTo("people"), "a person's change draws on a budget of its own");
        });
    }
}
