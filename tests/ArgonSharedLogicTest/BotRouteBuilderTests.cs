namespace ArgonSharedLogicTest;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Argon.Features.BotApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// The typed route builder is what every bot interface will be mapped through, so the two things it
/// changes about a route — how the request is bound and how a declared error comes back — are pinned
/// here against a real host rather than assumed.
/// <para>
/// Query binding matters specifically: routes that used to take loose <c>channelId</c>/<c>limit</c>
/// parameters now take a record whose properties are PascalCase, and bots in the wild send the
/// lowercase spelling.
/// </para>
/// </summary>
[TestFixture]
public class BotRouteBuilderTests
{
    private WebApplication app    = null!;
    private HttpClient     client = null!;

    public sealed record EchoQuery(Guid ChannelId, long? From = null, int? Limit = null);

    public sealed record EchoResponse(Guid ChannelId, long? From, int? Limit);

    public sealed record FailRequest(string Reason);

    public sealed record FormRequest(
        Guid          SpaceId,
        string        Name,
        BotInputFile  File,
        List<string>? Emoji     = null,
        bool?         TextColor = null,
        int?          Count     = null,
        BotInputFile? Thumb     = null);

    public sealed record FormEcho(
        Guid          SpaceId,
        string        Name,
        List<string>? Emoji,
        bool?         TextColor,
        int?          Count,
        string?       File,
        string?       FileName,
        Guid?         FileId,
        string?       Thumb);

    public sealed record FilesRequest(
        string              Text,
        List<BotInputFile>? Files = null);

    public sealed record FilesEcho(
        string        Text,
        List<string?> Files,
        List<Guid?>   FileIds,
        List<string?> Names);

    private const int FormLimit = 2048;

    private static readonly BotError Refused = new(403, "refused", "The route refused this request.");

    [OneTimeSetUp]
    public async Task StartHost()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        app = builder.Build();

        var group = app.MapGroup("/test");

        group.Get<EchoQuery, EchoResponse>("/Echo")
           .Summary("Echoes the bound query back.")
           .Handle((_, query) => Task.FromResult(new EchoResponse(query.ChannelId, query.From, query.Limit)));

        group.Post<FailRequest, EchoResponse>("/Fail")
           .Throws(Refused)
           .Handle((_, request) => throw Refused.Raise(request.Reason));

        group.Post<FormRequest, FormEcho>("/Form")
           .FromForm(FormLimit)
           .Handle((_, r) => Task.FromResult(new FormEcho(r.SpaceId, r.Name, r.Emoji, r.TextColor, r.Count,
                Text(r.File.Data), r.File.FileName, r.File.FileId, Text(r.Thumb?.Data))));

        group.Post<FilesRequest, FilesEcho>("/Files")
           .FromBodyOrForm(FormLimit)
           .Handle((_, r) => Task.FromResult(new FilesEcho(r.Text,
                (r.Files ?? []).Select(f => Text(f.Data)).ToList(),
                (r.Files ?? []).Select(f => f.FileId).ToList(),
                (r.Files ?? []).Select(f => f.FileName).ToList())));

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
           .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        client = new HttpClient { BaseAddress = new Uri(address) };
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    [Test]
    public async Task QueryBinding_AcceptsTheLowercaseSpellingBotsAlreadySend()
    {
        var channelId = Guid.NewGuid();

        var response = await client.GetAsync($"/test/Echo?channelId={channelId}&from=42&limit=7");
        var body     = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("channelId").GetGuid(), Is.EqualTo(channelId));
            Assert.That(body.GetProperty("from").GetInt64(), Is.EqualTo(42));
            Assert.That(body.GetProperty("limit").GetInt32(), Is.EqualTo(7));
        });
    }

    [Test]
    public async Task QueryBinding_LeavesOmittedOptionalsNull()
    {
        var response = await client.GetAsync($"/test/Echo?channelId={Guid.NewGuid()}");
        var body     = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(body.GetProperty("from").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(body.GetProperty("limit").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    private static string? Text(byte[]? data) => data is null ? null : Encoding.UTF8.GetString(data);

    private static MultipartFormDataContent Form(Guid spaceId, params (string Name, string Value)[] fields)
    {
        var form = new MultipartFormDataContent { { new StringContent(spaceId.ToString()), "spaceId" } };
        foreach (var (name, value) in fields)
            form.Add(new StringContent(value), name);
        return form;
    }

    private static void AddPart(MultipartFormDataContent form, string name, string content, string fileName = "part.bin")
        => form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), name, fileName);

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostFormAsync(HttpContent content)
    {
        var response = await client.PostAsync("/test/Form", content);
        var text     = await response.Content.ReadAsStringAsync();

        Assert.That(text, Is.Not.Empty, $"{(int)response.StatusCode} with no body");
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Test]
    public async Task FormBinding_TakesAFilePartNamedLikeTheField()
    {
        var spaceId = Guid.NewGuid();
        using var form = Form(spaceId, ("name", "wave"), ("count", "5"), ("textColor", "true"));
        AddPart(form, "file", "the sticker", "wave.webp");

        var (status, body) = await PostFormAsync(form);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
            Assert.That(body.GetProperty("spaceId").GetGuid(), Is.EqualTo(spaceId));
            Assert.That(body.GetProperty("name").GetString(), Is.EqualTo("wave"));
            Assert.That(body.GetProperty("count").GetInt32(), Is.EqualTo(5));
            Assert.That(body.GetProperty("textColor").GetBoolean(), Is.True);
            Assert.That(body.GetProperty("file").GetString(), Is.EqualTo("the sticker"));
            Assert.That(body.GetProperty("fileName").GetString(), Is.EqualTo("wave.webp"));
            Assert.That(body.GetProperty("thumb").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [Test]
    public async Task FormBinding_ResolvesAttachReferencesToTheirParts()
    {
        // Telegram's convention: the field names a part, and the part carries the bytes.
        using var form = Form(Guid.NewGuid(), ("name", "animated"), ("file", "attach://sticker"), ("thumb", "attach://first_frame"));
        AddPart(form, "sticker", "lottie bytes");
        AddPart(form, "first_frame", "webp bytes");

        var (status, body) = await PostFormAsync(form);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
            Assert.That(body.GetProperty("file").GetString(), Is.EqualTo("lottie bytes"));
            Assert.That(body.GetProperty("thumb").GetString(), Is.EqualTo("webp bytes"));
        });
    }

    [Test]
    public async Task FormBinding_TakesAFileIdInPlaceOfAPart()
    {
        var fileId = Guid.NewGuid();
        using var form = Form(Guid.NewGuid(), ("name", "reused"), ("file", fileId.ToString()));

        var (status, body) = await PostFormAsync(form);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
            Assert.That(body.GetProperty("fileId").GetGuid(), Is.EqualTo(fileId));
            Assert.That(body.GetProperty("file").ValueKind, Is.EqualTo(JsonValueKind.Null));
        });
    }

    [TestCase("repeated")]
    [TestCase("json")]
    public async Task FormBinding_FillsAListFromRepeatedFieldsOrAJsonArray(string shape)
    {
        using var form = shape == "json"
            ? Form(Guid.NewGuid(), ("name", "listed"), ("emoji", """["👋","🙂"]"""))
            : Form(Guid.NewGuid(), ("name", "listed"), ("emoji", "👋"), ("emoji", "🙂"));
        AddPart(form, "file", "x");

        var (status, body) = await PostFormAsync(form);

        Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
        Assert.That(body.GetProperty("emoji").EnumerateArray().Select(e => e.GetString()), Is.EqualTo(new[] { "👋", "🙂" }));
    }

    [Test]
    public async Task FormBinding_RefusesWhatItCannotBind()
    {
        using var dangling = Form(Guid.NewGuid(), ("name", "x"), ("file", "attach://nowhere"));
        using var noFile   = Form(Guid.NewGuid(), ("name", "x"));
        using var noName   = Form(Guid.NewGuid());
        AddPart(noName, "file", "x");
        using var badCount = Form(Guid.NewGuid(), ("name", "x"), ("count", "many"));
        AddPart(badCount, "file", "x");
        using var json = new StringContent("""{"spaceId":"00000000-0000-0000-0000-000000000000","name":"x"}""", Encoding.UTF8, "application/json");

        foreach (var (content, why) in new (HttpContent, string)[]
                 {
                     (dangling, "an attach:// reference to no part"), (noFile, "no file"), (noName, "no name"),
                     (badCount, "a number that is not one"), (json, "a JSON body")
                 })
        {
            var (status, body) = await PostFormAsync(content);

            Assert.That(status, Is.EqualTo(HttpStatusCode.BadRequest), why);
            Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("invalid_request"), why);
        }
    }

    [Test]
    public async Task FormBinding_RefusesABodyOverTheRoutesLimit()
    {
        using var form = Form(Guid.NewGuid(), ("name", "big"));
        AddPart(form, "file", new string('x', FormLimit + 1));

        var (status, body) = await PostFormAsync(form);

        Assert.That(status, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
        Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("too_large"));
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostFilesAsync(HttpContent content)
    {
        var response = await client.PostAsync("/test/Files", content);
        var text     = await response.Content.ReadAsStringAsync();

        Assert.That(text, Is.Not.Empty, $"{(int)response.StatusCode} with no body");
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Test]
    public async Task BodyOrForm_TakesJsonWithFileIds()
    {
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        using var json = new StringContent($$"""{"text":"hi","files":["{{first}}","{{second}}"]}""", Encoding.UTF8, "application/json");

        var (status, body) = await PostFilesAsync(json);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
            Assert.That(body.GetProperty("text").GetString(), Is.EqualTo("hi"));
            Assert.That(body.GetProperty("fileIds").EnumerateArray().Select(e => e.GetGuid()), Is.EqualTo(new[] { first, second }));
        });
    }

    [Test]
    public async Task BodyOrForm_FillsAListOfFilesFromReferencesFileIdsAndPartsOfItsName()
    {
        var fileId = Guid.NewGuid();
        using var form = new MultipartFormDataContent
        {
            { new StringContent("with files"), "text" },
            { new StringContent("attach://photo"), "files" },
            { new StringContent(fileId.ToString()), "files" }
        };
        AddPart(form, "photo", "photo bytes", "photo.png");
        AddPart(form, "files", "first own part", "a.txt");
        AddPart(form, "files", "second own part", "b.txt");

        var (status, body) = await PostFilesAsync(form);

        Assert.Multiple(() =>
        {
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), body.ToString());
            Assert.That(body.GetProperty("files").EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()),
                Is.EqualTo(new[] { "photo bytes", null, "first own part", "second own part" }));
            Assert.That(body.GetProperty("fileIds")[1].GetGuid(), Is.EqualTo(fileId));
            Assert.That(body.GetProperty("names").EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString()),
                Is.EqualTo(new[] { "photo.png", null, "a.txt", "b.txt" }));
        });
    }

    [Test]
    public async Task BodyOrForm_RefusesMalformedJsonAndAnOversizedForm()
    {
        using var json = new StringContent("""{"text":"hi","files":["not a file"]}""", Encoding.UTF8, "application/json");
        using var big  = new MultipartFormDataContent { { new StringContent("big"), "text" } };
        AddPart(big, "files", new string('x', FormLimit + 1));

        var (malformed, malformedBody) = await PostFilesAsync(json);
        var (tooLarge, tooLargeBody)   = await PostFilesAsync(big);

        Assert.Multiple(() =>
        {
            Assert.That(malformed, Is.EqualTo(HttpStatusCode.BadRequest));
            Assert.That(malformedBody.GetProperty("error").GetString(), Is.EqualTo("invalid_request"));
            Assert.That(tooLarge, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
            Assert.That(tooLargeBody.GetProperty("error").GetString(), Is.EqualTo("too_large"));
        });
    }

    [Test]
    public async Task ADeclaredError_ComesBackWithItsStatusAndCode()
    {
        var response = await client.PostAsJsonAsync("/test/Fail", new FailRequest("no reason at all"));
        var body     = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
            Assert.That(body.GetProperty("error").GetString(), Is.EqualTo("refused"));
            Assert.That(body.GetProperty("message").GetString(), Is.EqualTo("no reason at all"));
        });
    }
}
