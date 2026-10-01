namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using static ConnectionDetailKeys;

/// <summary>
/// Twitch. No scope is needed for the public fields of the signed-in user; <c>force_verify</c> makes
/// Twitch ask which account even when one is already signed in, so a second Twitch account is not
/// silently the first one. Helix wants the client id as a header on every call.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parsers are exercised against recorded answers.")]
public sealed class TwitchConnectionProvider(HttpClient http, IOptions<ConnectionsOptions> options)
    : OAuthConnectionProvider(http, options.Value.Twitch)
{
    private const string Api = "https://api.twitch.tv/helix";
    private const string Id  = "https://id.twitch.tv/oauth2";

    private readonly TwitchConnectionOptions twitch = options.Value.Twitch;

    public override ConnectionProvider Kind => ConnectionProvider.TWITCH;

    /// <summary>Streaming status only once EventSub has a secret to sign with; details always.</summary>
    public override ConnectionCapability Capabilities
        => twitch.StreamingStatusEnabled ? ConnectionCapability.DETAILS | ConnectionCapability.STATUS : ConnectionCapability.DETAILS;

    public override bool UsesPkce => false;

    protected override string AuthorizeEndpoint => $"{Id}/authorize";
    protected override string TokenEndpoint     => $"{Id}/token";
    protected override string Scopes            => "";

    protected override TokenClientAuth TokenAuth => TokenClientAuth.Body;

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraAuthorizeParameters
        => [new("force_verify", "true")];

    protected override async Task<ProviderSnapshot> ReadAsync(ProviderToken token, OAuthTokenResponse? raw, CancellationToken ct)
    {
        using var document = await OAuthCodeFlow.GetJsonAsync(Http, $"{Api}/users", token.AccessToken, ct,
            new KeyValuePair<string, string>("Client-Id", App.ClientId));

        return Parse(document.RootElement);
    }

    public static ProviderSnapshot Parse(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/users", "no user");

        var user  = data[0];
        var id    = user.String("id") ?? throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/users", "no id");
        var login = user.String("login") ?? id;

        var identity = new ConnectionIdentity(id, user.String("display_name") ?? login, $"https://twitch.tv/{login}", user.String("profile_image_url"));

        var details = new List<ConnectionDetail>();

        if (user.String("broadcaster_type") is { Length: > 0 } kind)
            details.Add(Text(TwitchBroadcasterType, kind));
        if (user.Date("created_at") is { } since)
            details.Add(Date(Since, since));

        return new ProviderSnapshot(identity, details);
    }

    public override Task RevokeAsync(ProviderToken token, CancellationToken ct)
        => PostFormQuietlyAsync($"{Id}/revoke",
        [
            new("client_id", App.ClientId),
            new("token", token.AccessToken)
        ], null, ct);
}
