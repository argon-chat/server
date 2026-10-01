namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static ConnectionDetailKeys;

/// <summary>
/// YouTube through Google OAuth. <c>access_type=offline</c> and <c>prompt=consent</c> are what make
/// Google hand out a refresh token every time rather than only on the first consent. A Google
/// account without a channel is refused: there is nothing to show.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parsers are exercised against recorded answers.")]
public sealed class YouTubeConnectionProvider(HttpClient http, IOptions<ConnectionsOptions> options)
    : OAuthConnectionProvider(http, options.Value.YouTube)
{
    private const string Api = "https://www.googleapis.com/youtube/v3";

    public override ConnectionProvider   Kind         => ConnectionProvider.YOUTUBE;
    public override ConnectionCapability Capabilities => ConnectionCapability.DETAILS;

    protected override string AuthorizeEndpoint => "https://accounts.google.com/o/oauth2/v2/auth";
    protected override string TokenEndpoint     => "https://oauth2.googleapis.com/token";
    protected override string Scopes            => "https://www.googleapis.com/auth/youtube.readonly";

    protected override TokenClientAuth TokenAuth => TokenClientAuth.Body;

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraAuthorizeParameters =>
    [
        new("access_type", "offline"),
        new("prompt", "consent"),
        new("include_granted_scopes", "true")
    ];

    protected override async Task<ProviderSnapshot> ReadAsync(ProviderToken token, OAuthTokenResponse? raw, CancellationToken ct)
    {
        using var document = await OAuthCodeFlow.GetJsonAsync(Http, $"{Api}/channels?part=snippet,statistics&mine=true", token.AccessToken, ct);

        return Parse(document.RootElement);
    }

    public static ProviderSnapshot Parse(JsonElement root)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            throw new ProviderCallException(HttpStatusCode.NotFound, $"{Api}/channels", "this Google account has no YouTube channel");

        var channel = items[0];
        var id      = channel.String("id") ?? throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/channels", "no id");
        var snippet = channel.Object("snippet");
        var stats   = channel.Object("statistics");
        var handle  = snippet?.String("customUrl");

        var identity = new ConnectionIdentity(
            id,
            snippet?.String("title") ?? handle ?? id,
            handle is { Length: > 0 } ? $"https://www.youtube.com/{handle}" : $"https://www.youtube.com/channel/{id}",
            snippet?.Object("thumbnails")?.Object("default")?.String("url"));

        var details = new List<ConnectionDetail>();

        if (stats is { } s)
        {
            if (s.Boolean("hiddenSubscriberCount") != true && s.Number("subscriberCount") is { } subscribers)
                details.Add(Number(YouTubeSubscribers, subscribers));
            if (s.Number("videoCount") is { } videos)
                details.Add(Number(YouTubeVideos, videos));
        }

        if (snippet?.Date("publishedAt") is { } since)
            details.Add(Date(Since, since));

        return new ProviderSnapshot(identity, details);
    }

    public override Task RevokeAsync(ProviderToken token, CancellationToken ct)
        => PostFormQuietlyAsync("https://oauth2.googleapis.com/revoke",
            [new("token", token.RefreshToken ?? token.AccessToken)], null, ct);
}
