namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Argon.Features.BotApi;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using static BotHttpKit;
using static ChannelTestKit;

/// <summary>
/// Files a bot sends to a channel: uploaded with purpose attachment and named by fileId in <c>IMessages/Send</c>, or
/// carried by a multipart Send itself, read back with their URLs from the history, the Send response and
/// <c>messageCreate</c>, and the limits around them.
/// </summary>
/// <remarks>A sticker upload named as an attachment is refused with 404: the fileId names no attachment upload.</remarks>
[TestFixture]
public class BotAttachmentsTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(30);

    private const ArgonEntitlement Sends = ArgonEntitlement.ViewChannel | ArgonEntitlement.ReadHistory | ArgonEntitlement.SendMessages
                                         | ArgonEntitlement.AttachFiles;

    private const int Side = 600;

    /// <summary>A 600×600 PNG of noise, which does not compress: about 1 MiB.</summary>
    private static readonly byte[] NoisePng = Noise();

    private static readonly byte[] SmallPng = Solid();

    private static byte[] Solid()
    {
        using var image  = new Image<Rgba32>(64, 64, new Rgba32(40, 160, 220, 255));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private static bool IsNull(JsonElement element, string property)
        => !element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null;

    private static byte[] Noise()
    {
        var random = new Random(7);
        using var image = new Image<Rgba32>(Side, Side);
        for (var y = 0; y < Side; y++)
        for (var x = 0; x < Side; x++)
            image[x, y] = new Rgba32((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);

        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private async Task<(Guid SpaceId, Guid ChannelId, Bot Bot)> RoomAsync(CancellationToken ct)
    {
        var owner     = await CreateSessionAsync(ct);
        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "files", ChannelType.Text, ct);
        var bot       = await InstallAsync(owner, spaceId, Sends, ct);
        return (spaceId, channelId, bot);
    }

    private static long NextRandomId() => Random.Shared.NextInt64(1, long.MaxValue);

    private static Task<Answer> UploadAsync(Bot bot, string purpose, byte[] data, string contentType, string fileName, CancellationToken ct)
    {
        var form = Part(new MultipartFormDataContent { { new StringContent(purpose), "purpose" } }, "file", data, contentType, fileName);
        return SendAsync(bot, HttpMethod.Post, "/IFiles/v1/Upload", form, ct);
    }

    private static MultipartFormDataContent SendForm(Guid channelId, string text, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(channelId.ToString()), "channelId" },
            { new StringContent(text), "text" },
            { new StringContent(NextRandomId().ToString()), "randomId" }
        };

        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);

        return form;
    }

    private static Task<Answer> SendJsonAsync(Bot bot, Guid channelId, string text, IEnumerable<string> attachments, CancellationToken ct)
        => PostAsync(bot, "/IMessages/v1/Send", new { channelId, text, randomId = NextRandomId(), attachments }, ct);

    private static JsonElement AttachmentOf(JsonElement message) => message.GetProperty("attachments").EnumerateArray().Single();

    [Test, CancelAfter(180_000)]
    public async Task An_uploaded_file_is_sent_by_its_file_id_and_read_back_with_its_url(CancellationToken ct = default)
    {
        var (_, channelId, bot) = await RoomAsync(ct);

        await using var events = await ChannelTestBot.BotEvents.OpenAsync(bot.Token, ct, (long)BotIntent.Messages);

        var upload = Ok(await UploadAsync(bot, "attachment", NoisePng, "image/png", "noise.png", ct));
        var fileId = upload.GetProperty("fileId").GetGuid();

        var sent      = Ok(await SendJsonAsync(bot, channelId, "a picture", [fileId.ToString()], ct));
        var messageId = sent.GetProperty("messageId").GetInt64();
        var inSend    = AttachmentOf(sent);

        var history = Ok(await GetAsync(bot, $"/IMessages/v1/History?channelId={channelId}&limit=5", ct))
           .GetProperty("messages").EnumerateArray().Single(m => m.GetProperty("messageId").GetInt64() == messageId);
        var entity = history.GetProperty("entities").EnumerateArray().Single();

        var created = await events.WaitForAsync("messageCreate", e => e["message"]?["messageId"]?.ToObject<long>() == messageId, EventWait, ct);
        var inEvent = created["message"]!["attachments"]!.Single()!;

        await using var db = await DbAsync(ct);
        var counted = await PollAsync(() => db.FileCounters.AsNoTracking().Where(c => c.Id == fileId).Select(c => c.RefCount).SingleAsync(ct),
            count => count == 1, EventWait, ct);

        Assert.Multiple(() =>
        {
            Assert.That(NoisePng.Length, Is.GreaterThan(1024 * 1024 - 64 * 1024), "setup: about a MiB");
            Assert.That(upload.GetProperty("contentType").GetString(), Is.EqualTo("image/png"));
            Assert.That(upload.GetProperty("fileName").GetString(), Is.EqualTo("noise.png"));
            Assert.That(upload.GetProperty("size").GetInt64(), Is.EqualTo(NoisePng.Length));

            Assert.That(inSend.GetProperty("fileId").GetGuid(), Is.EqualTo(fileId));
            Assert.That(inSend.GetProperty("url").GetString(), Does.EndWith(fileId.ToString()));
            Assert.That(inSend.GetProperty("fileName").GetString(), Is.EqualTo("noise.png"));
            Assert.That(inSend.GetProperty("size").GetInt64(), Is.EqualTo(NoisePng.Length));
            Assert.That(inSend.GetProperty("contentType").GetString(), Is.EqualTo("image/png"));
            Assert.That((inSend.GetProperty("width").GetInt32(), inSend.GetProperty("height").GetInt32()), Is.EqualTo((Side, Side)));

            Assert.That(entity.GetProperty("type").GetInt32(), Is.EqualTo((int)EntityType.Attachment));
            Assert.That(entity.GetProperty("fileId").GetGuid(), Is.EqualTo(fileId));
            Assert.That(entity.GetProperty("downloadUrl").GetString(), Does.EndWith(fileId.ToString()));
            Assert.That(IsNull(entity, "thumbHash"), Is.True, "a bot attachment has no thumbHash");
            Assert.That(AttachmentOf(history).GetProperty("url").GetString(), Does.EndWith(fileId.ToString()));

            Assert.That(inEvent["url"]?.ToString(), Does.EndWith(fileId.ToString()));
            Assert.That(inEvent["fileName"]?.ToString(), Is.EqualTo("noise.png"));
            Assert.That(inEvent["size"]?.ToObject<long>(), Is.EqualTo(NoisePng.Length));
            Assert.That(inEvent["width"]?.ToObject<int>(), Is.EqualTo(Side));
            Assert.That(created["message"]!["entities"]!.Single()!["url"]?.ToString(), Does.EndWith(fileId.ToString()),
                "the attachment entity carries its URL too");

            Assert.That(counted, Is.EqualTo(1), "the message holds a reference, so the upload is no longer collectable");
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_multipart_send_carries_its_files_as_parts(CancellationToken ct = default)
    {
        var (_, channelId, bot) = await RoomAsync(ct);

        var uploaded = Ok(await UploadAsync(bot, "attachment", NoisePng, "image/png", "earlier.png", ct)).GetProperty("fileId").GetGuid();

        using var form = SendForm(channelId, "three files", ("attachments", "attach://notes"), ("attachments", uploaded.ToString()));
        Part(form, "notes", Encoding.UTF8.GetBytes("hello"), "text/plain", "notes.txt");
        Part(form, "attachments", Encoding.UTF8.GetBytes("{\"a\":1}"), "application/json", "data.json");

        var sent  = Ok(await SendAsync(bot, HttpMethod.Post, "/IMessages/v1/Send", form, ct));
        var files = sent.GetProperty("attachments").EnumerateArray().ToList();

        Assert.Multiple(() =>
        {
            Assert.That(files.Select(f => f.GetProperty("fileName").GetString()), Is.EqualTo(new[] { "notes.txt", "earlier.png", "data.json" }));
            Assert.That(files.Select(f => f.GetProperty("contentType").GetString()),
                Is.EqualTo(new[] { "text/plain", "image/png", "application/json" }));
            Assert.That(files[0].GetProperty("size").GetInt64(), Is.EqualTo(5));
            Assert.That(IsNull(files[0], "width"), Is.True, "no size for a file that is no image");
            Assert.That(files[1].GetProperty("fileId").GetGuid(), Is.EqualTo(uploaded));
            Assert.That(files.Select(f => f.GetProperty("url").GetString()), Is.All.Not.Empty);
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task Oversized_surplus_unknown_and_foreign_files_are_refused(CancellationToken ct = default)
    {
        var (_, channelId, bot) = await RoomAsync(ct);

        var eleven = new byte[11 * 1024 * 1024];
        var tooBig = await UploadAsync(bot, "attachment", eleven, "application/octet-stream", "big.bin", ct);

        using var bigForm = SendForm(channelId, "too big", ("attachments", "attach://big"));
        Part(bigForm, "big", eleven, "application/octet-stream", "big.bin");
        var tooBigInline = await SendAsync(bot, HttpMethod.Post, "/IMessages/v1/Send", bigForm, ct);

        var surplus = await SendJsonAsync(bot, channelId, "eleven", Enumerable.Range(0, 11).Select(_ => Guid.NewGuid().ToString()), ct);
        var unknown = await SendJsonAsync(bot, channelId, "unknown", [Guid.NewGuid().ToString()], ct);

        var sticker = Ok(await UploadAsync(bot, "sticker", SmallPng, "image/png", "sticker.png", ct)).GetProperty("fileId").GetGuid();
        var wrongPurpose = await SendJsonAsync(bot, channelId, "a sticker", [sticker.ToString()], ct);

        var partInJson = await SendJsonAsync(bot, channelId, "a part", ["attach://nowhere"], ct);

        using var emptyForm = SendForm(channelId, "empty", ("attachments", "attach://empty"));
        Part(emptyForm, "empty", [], "text/plain", "empty.txt");
        var empty = await SendAsync(bot, HttpMethod.Post, "/IMessages/v1/Send", emptyForm, ct);

        var history = Ok(await GetAsync(bot, $"/IMessages/v1/History?channelId={channelId}&limit=20", ct)).GetProperty("messages");

        Assert.Multiple(() =>
        {
            Refused(tooBig, HttpStatusCode.RequestEntityTooLarge, "too_large", "an 11 MiB upload");
            Refused(tooBigInline, HttpStatusCode.RequestEntityTooLarge, "too_large", "an 11 MiB part");
            Refused(surplus, HttpStatusCode.BadRequest, "validation_error", "eleven attachments");
            Refused(unknown, HttpStatusCode.NotFound, "not_found", "a fileId that names no upload");
            Refused(wrongPurpose, HttpStatusCode.NotFound, "not_found", "a sticker upload is no attachment");
            Refused(partInJson, HttpStatusCode.BadRequest, "invalid_request", "attach:// in a JSON body");
            Refused(empty, HttpStatusCode.BadRequest, "invalid_format", "an empty part");
            Assert.That(history.GetArrayLength(), Is.Zero, "nothing was sent");
        });
    }
}
