namespace Argon.Features.Integrations.Connections;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Signing keys per JWKS address, refreshed on a schedule and on an unknown key id.
/// </summary>
/// <remarks>
/// Telegram publishes a JWKS and no discovery document, so this is the direct form of what
/// <c>AegisTokenValidator</c> gets from <c>ConfigurationManager</c>. Keys rotate rarely and a fetch
/// per validation would put the provider on every link, so a set is held for a day and re-read
/// early only when a token names a key id that is not in it.
/// </remarks>
public sealed class JwksKeyCache(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "connections-jwks";

    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    private readonly ConcurrentDictionary<string, Entry> entries = new();

    private sealed record Entry(DateTimeOffset FetchedAt, IReadOnlyList<SecurityKey> Keys);

    public async Task<IReadOnlyList<SecurityKey>> GetAsync(string jwksUrl, bool refresh, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (!refresh && entries.TryGetValue(jwksUrl, out var entry) && now - entry.FetchedAt < Lifetime)
            return entry.Keys;

        var http = httpClients.CreateClient(HttpClientName);
        var body = await http.GetStringAsync(jwksUrl, ct);
        var set  = new JsonWebKeySet(body);
        var keys = set.GetSigningKeys().ToList();

        entries[jwksUrl] = new Entry(now, keys);

        return keys;
    }
}

/// <summary>Validates an OIDC ID token against a JWKS and hands back its claims.</summary>
public static class IdTokenValidator
{
    private static readonly JsonWebTokenHandler Handler = new();

    /// <summary>The claims, or null with the reason when the token does not verify.</summary>
    public static async Task<(IReadOnlyDictionary<string, string>? Claims, string? Error)> ValidateAsync(
        JwksKeyCache jwks, string jwksUrl, string idToken, IReadOnlyList<string> issuers, string audience, CancellationToken ct)
    {
        var result = await ValidateOnceAsync(jwks, jwksUrl, idToken, issuers, audience, refresh: false, ct);

        // A key the cached set does not know is the one case worth a second look: the provider may
        // have rotated since the set was read.
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            result = await ValidateOnceAsync(jwks, jwksUrl, idToken, issuers, audience, refresh: true, ct);

        if (!result.IsValid)
            return (null, result.Exception?.GetType().Name ?? "invalid id_token");

        var claims = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var claim in result.ClaimsIdentity.Claims)
            claims.TryAdd(claim.Type, claim.Value);

        return (claims, null);
    }

    private static async Task<TokenValidationResult> ValidateOnceAsync(
        JwksKeyCache jwks, string jwksUrl, string idToken, IReadOnlyList<string> issuers, string audience, bool refresh, CancellationToken ct)
    {
        var keys = await jwks.GetAsync(jwksUrl, refresh, ct);

        return await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuers             = issuers,
            ValidAudience            = audience,
            ValidateIssuer           = true,
            ValidateAudience         = true,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys        = keys,
            ClockSkew                = TimeSpan.FromMinutes(2)
        });
    }
}
