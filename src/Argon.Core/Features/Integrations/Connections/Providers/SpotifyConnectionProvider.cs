namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static ConnectionDetailKeys;

/// <summary>
/// Spotify. The playback scopes are asked for up front so the activity and listen-along phases
/// need no second consent; <c>user-read-private</c> is what says whether the account is Premium.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parsers are exercised against recorded answers.")]
public sealed class SpotifyConnectionProvider(HttpClient http, IOptions<ConnectionsOptions> options)
    : OAuthConnectionProvider(http, options.Value.Spotify)
{
    private const string Api = "https://api.spotify.com/v1";

    public const string RequiredScopes = "user-read-currently-playing user-read-playback-state user-read-private user-modify-playback-state";

    public override ConnectionProvider   Kind         => ConnectionProvider.SPOTIFY;
    public override ConnectionCapability Capabilities => ConnectionCapability.DETAILS | ConnectionCapability.STATUS | ConnectionCapability.LISTEN_ALONG;

    protected override string AuthorizeEndpoint => "https://accounts.spotify.com/authorize";
    protected override string TokenEndpoint     => "https://accounts.spotify.com/api/token";
    protected override string Scopes            => RequiredScopes;

    protected override async Task<ProviderSnapshot> ReadAsync(ProviderToken token, OAuthTokenResponse? raw, CancellationToken ct)
    {
        using var document = await OAuthCodeFlow.GetJsonAsync(Http, $"{Api}/me", token.AccessToken, ct);

        return Parse(document.RootElement);
    }

    public static ProviderSnapshot Parse(JsonElement me)
    {
        var id = me.String("id") ?? throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/me", "no id");

        string? avatar = null;

        if (me.TryGetProperty("images", out var images) && images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
            avatar = images[0].String("url");

        var identity = new ConnectionIdentity(
            id,
            me.String("display_name") ?? id,
            me.Object("external_urls")?.String("spotify") ?? $"https://open.spotify.com/user/{id}",
            avatar);

        var details = new List<ConnectionDetail>
        {
            Flag(SpotifyPremium, string.Equals(me.String("product"), "premium", StringComparison.OrdinalIgnoreCase))
        };

        if (me.Object("followers")?.Number("total") is { } followers)
            details.Add(Number(SpotifyFollowers, followers));

        return new ProviderSnapshot(identity, details);
    }

    // Spotify has no revocation endpoint; the person removes the app from their account page.
}
