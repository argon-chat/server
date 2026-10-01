namespace ArgonSharedLogicTest.Connections;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Argon.Features.Integrations.Connections;
using Microsoft.Extensions.Options;

/// <summary>A handler that answers by method and URL prefix and remembers what it was asked.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<Route> routes = [];

    public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

    public FakeHttpHandler On(HttpMethod method, string urlPrefix, HttpStatusCode status, string body, string mediaType = "application/json")
        => On(method, urlPrefix, _ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });

    public FakeHttpHandler On(HttpMethod method, string urlPrefix, Func<HttpRequestMessage, HttpResponseMessage> answer)
    {
        routes.Add(new Route(method, urlPrefix, answer));
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);

        Calls.Add((request, body));

        var url   = request.RequestUri!.ToString();
        var route = routes.FirstOrDefault(r => r.Method == request.Method && url.StartsWith(r.UrlPrefix, StringComparison.Ordinal));

        return route?.Answer(request)
            ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent($"no route for {request.Method} {url}") };
    }

    public HttpClient Client() => new(this, disposeHandler: false);

    public (HttpRequestMessage Request, string Body) CallTo(string urlPrefix)
        => Calls.First(c => c.Request.RequestUri!.ToString().StartsWith(urlPrefix, StringComparison.Ordinal));

    private sealed record Route(HttpMethod Method, string UrlPrefix, Func<HttpRequestMessage, HttpResponseMessage> Answer);
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal static class ConnectionsTestOptions
{
    public static string Key() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static ConnectionsOptions Default(string? key = null) => new()
    {
        PublicCallbackBase = "https://api.argon.test",
        WebAppUrl          = "https://app.argon.test",
        TokenKey           = key ?? Key(),
        GitHub             = new() { ClientId = "gh-id", ClientSecret = "gh-secret", ServerToken = "ghp_server", ContributorRepos = ["argon-chat/server"] },
        Steam              = new() { WebApiKey = "steam-key" },
        Spotify            = new() { ClientId = "sp-id", ClientSecret = "sp-secret" },
        Twitter            = new() { ClientId = "tw-id", ClientSecret = "tw-secret", DetailsRefreshEvery = TimeSpan.FromDays(30) },
        Twitch             = new() { ClientId = "ttv-id", ClientSecret = "ttv-secret" },
        YouTube            = new() { ClientId = "yt-id", ClientSecret = "yt-secret" },
        Telegram           = new() { ClientId = "123456", ClientSecret = "tg-secret" }
    };

    public static IOptions<ConnectionsOptions> Wrap(ConnectionsOptions options) => Options.Create(options);
}
