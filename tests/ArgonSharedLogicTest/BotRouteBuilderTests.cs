namespace ArgonSharedLogicTest;

using System.Net;
using System.Net.Http.Json;
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
