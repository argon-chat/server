namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static ConnectionDetailKeys;

/// <summary>
/// Twitter/X, OAuth 2.0 with PKCE. <c>users.read</c> needs <c>tweet.read</c> beside it, and
/// <c>offline.access</c> is what yields a refresh token; the refresh rotates, so both halves are
/// rewritten on every renewal. Every <c>users/me</c> is a billed read, which is why this provider's
/// details refresh is monthly.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parsers are exercised against recorded answers.")]
public sealed class TwitterConnectionProvider(HttpClient http, IOptions<ConnectionsOptions> options)
    : OAuthConnectionProvider(http, options.Value.Twitter)
{
    private const string Api = "https://api.x.com/2";

    public override ConnectionProvider   Kind         => ConnectionProvider.TWITTER;
    public override ConnectionCapability Capabilities => ConnectionCapability.DETAILS;

    protected override string AuthorizeEndpoint => "https://x.com/i/oauth2/authorize";
    protected override string TokenEndpoint     => $"{Api}/oauth2/token";
    protected override string Scopes            => "users.read tweet.read offline.access";

    protected override async Task<ProviderSnapshot> ReadAsync(ProviderToken token, OAuthTokenResponse? raw, CancellationToken ct)
    {
        using var document = await OAuthCodeFlow.GetJsonAsync(Http,
            $"{Api}/users/me?user.fields=created_at,public_metrics,verified,profile_image_url", token.AccessToken, ct);

        return Parse(document.RootElement);
    }

    public static ProviderSnapshot Parse(JsonElement root)
    {
        var data     = root.Object("data") ?? throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/users/me", "no data");
        var id       = data.String("id") ?? throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/users/me", "no id");
        var username = data.String("username") ?? id;

        var identity = new ConnectionIdentity(id, username, $"https://x.com/{username}", data.String("profile_image_url"));

        var details = new List<ConnectionDetail>();

        if (data.Object("public_metrics")?.Number("followers_count") is { } followers)
            details.Add(Number(TwitterFollowers, followers));
        if (data.Boolean("verified") is { } verified)
            details.Add(Flag(TwitterVerified, verified));
        if (data.Date("created_at") is { } since)
            details.Add(Date(Since, since));

        return new ProviderSnapshot(identity, details);
    }

    public override Task RevokeAsync(ProviderToken token, CancellationToken ct)
        => PostFormQuietlyAsync($"{Api}/oauth2/revoke",
        [
            new("token", token.AccessToken),
            new("token_type_hint", "access_token"),
            new("client_id", App.ClientId)
        ], BasicClientCredentials(), ct);
}
