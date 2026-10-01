namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

/// <summary>
/// Telegram, through its OpenID Connect endpoints: a textbook code flow whose identity arrives as an
/// ID token rather than from a userinfo endpoint (there is none). The client id is the bot id, the
/// credentials come from @BotFather's Login Widget section, and there is no API behind the access
/// token, so nothing is kept once the ID token has been read.
/// </summary>
/// <remarks>
/// Only <c>openid profile</c> is asked for. <c>phone</c> (a verified number) and
/// <c>telegram:bot_access</c> (the bot may message the person) are real features of their own and
/// are not slipped in under a "show it on my profile" button.
/// </remarks>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the ID-token path is exercised with a fake JWKS.")]
public sealed class TelegramConnectionProvider(HttpClient http, JwksKeyCache jwks, IOptions<ConnectionsOptions> options)
    : OAuthConnectionProvider(http, options.Value.Telegram)
{
    public const string Issuer  = "https://oauth.telegram.org";
    public const string JwksUrl = "https://oauth.telegram.org/.well-known/jwks.json";

    private static readonly string[] Issuers = [Issuer, Issuer + "/"];

    public override ConnectionProvider   Kind         => ConnectionProvider.TELEGRAM;
    public override ConnectionCapability Capabilities => ConnectionCapability.NONE;

    public override bool KeepsTokens => false;

    protected override string AuthorizeEndpoint => "https://oauth.telegram.org/auth";
    protected override string TokenEndpoint     => "https://oauth.telegram.org/token";
    protected override string Scopes            => "openid profile";

    protected override async Task<ProviderSnapshot> ReadAsync(ProviderToken token, OAuthTokenResponse? raw, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(raw?.IdToken))
            throw new ProviderCallException(HttpStatusCode.BadGateway, TokenEndpoint, "no id_token in the answer");

        var (claims, error) = await IdTokenValidator.ValidateAsync(jwks, JwksUrl, raw.IdToken, Issuers, App.ClientId, ct);

        if (claims is null)
            throw new ProviderCallException(HttpStatusCode.Unauthorized, TokenEndpoint, $"id_token rejected: {error}");

        return new ProviderSnapshot(IdentityFrom(claims), []);
    }

    public static ConnectionIdentity IdentityFrom(IReadOnlyDictionary<string, string> claims)
    {
        var id = claims.GetValueOrDefault("sub")
              ?? claims.GetValueOrDefault(ClaimTypes.NameIdentifier)
              ?? claims.GetValueOrDefault("id")
              ?? throw new ProviderCallException(HttpStatusCode.BadGateway, Issuer, "id_token carries no subject");

        var username = claims.GetValueOrDefault("preferred_username");
        var name     = claims.GetValueOrDefault("name") ?? claims.GetValueOrDefault(ClaimTypes.Name);

        return new ConnectionIdentity(
            id,
            username ?? name ?? id,
            username is { Length: > 0 } ? $"https://t.me/{username}" : null,
            claims.GetValueOrDefault("picture"));
    }

    /// <summary>Nothing is kept, so there is nothing to renew.</summary>
    public override Task<ProviderToken?> RefreshAsync(ProviderToken token, CancellationToken ct)
        => Task.FromResult<ProviderToken?>(null);

    /// <summary>No API to ask; the identity read at link time is what there is.</summary>
    public override Task<ProviderSnapshot> FetchAsync(ConnectionIdentity identity, ProviderToken? token, CancellationToken ct)
        => Task.FromResult(new ProviderSnapshot(identity, []));
}
