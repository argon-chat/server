namespace Argon.Features.Aegis;

using Argon.Features.Clustering;
using Argon.Features.Jwt;

/// <summary>
/// The OpenID Connect provider: which endpoints it answers on, and what it will put in a token.
/// </summary>
public sealed class OpenIdProviderOptions : IValidatableFeatureOptions
{
    public const string SectionName = "OpenId";

    /// <summary>
    /// Where the authorization request lands. The widget is served from the site root and posts the
    /// completed flow back to it, which is why this is <c>/</c> rather than a path of its own.
    /// </summary>
    public string AuthorizationEndpoint { get; set; } = "/";

    public string TokenEndpoint    { get; set; } = "/connect/token";
    public string UserInfoEndpoint { get; set; } = "/connect/userinfo";

    /// <summary>
    /// Scopes an application is allowed to ask for. What any given application may actually have is
    /// narrower — that is its own registration's business — but nothing outside this list is a scope
    /// at all.
    /// </summary>
    public List<string> Scopes { get; set; } = [.. ArgonScopes.All];

    /// <summary>
    /// Whether access tokens are encrypted as well as signed.
    /// </summary>
    /// <remarks>
    /// Off, and the reason is not laziness: an access token here is read by resource servers that
    /// verify it as an ordinary signed JWT. Encrypting it would make the claims unreadable to
    /// everyone without this server's decryption key, which is every one of them. Identity tokens
    /// and authorization codes stay encrypted regardless — those are ours to read.
    /// </remarks>
    public bool EncryptAccessTokens { get; set; }

    /// <summary>
    /// Requires PKCE on the authorization code flow.
    /// </summary>
    /// <remarks>
    /// On, and it should stay on. The widget is a public client: it cannot keep a secret, so the
    /// code it receives is the only thing standing between an intercepted redirect and a token.
    /// </remarks>
    public bool RequireProofKeyForCodeExchange { get; set; } = true;

    public void Validate(IFeatureConfigurationReport report)
    {
        if (!report.SectionExists)
            return;

        foreach (var (path, name) in new[]
                 {
                     (AuthorizationEndpoint, nameof(AuthorizationEndpoint)),
                     (TokenEndpoint, nameof(TokenEndpoint)),
                     (UserInfoEndpoint, nameof(UserInfoEndpoint))
                 })
        {
            report.Required(path, name);
            report.Require(path.StartsWith('/'), name, $"'{path}' is not a rooted path");
        }

        // Note that configuration adds to this list rather than replacing it, so it can be extended
        // by a deployment but never emptied — the count check is a guard for code that constructs
        // these options directly, not something a settings file can trip.
        report.Require(Scopes.Count > 0, nameof(Scopes),
            "is empty, so every authorization request would be rejected for asking for a scope that " +
            "does not exist");

        foreach (var scope in Scopes)
            report.Require(!string.IsNullOrWhiteSpace(scope) && !scope.Contains(' '), nameof(Scopes),
                $"'{scope}' is not a scope: scopes are space-separated in a request, so one that is " +
                "blank or contains a space could never be asked for");

        report.Require(Scopes.Distinct(StringComparer.Ordinal).Count() == Scopes.Count, nameof(Scopes),
            "lists the same scope twice, which usually means a deployment meant to replace the list " +
            "and appended to it instead");

        // Authorization codes, refresh tokens and identity tokens are all encrypted with the RSA key
        // in the JWT section, and the wrapper that loads it is built lazily — without this rule a
        // deployment missing the key starts clean and then answers every single request with a 500,
        // including the ones that only asked whether there is a session. Read from another feature's
        // section on purpose: this is the rule that says the provider depends on it.
        report.Require(report.Read<JwtOptions>("Jwt").EncryptionBase64 is
                { PrivateKeyBase64.Length: > 0, PublicKeyBase64.Length: > 0 },
            "Jwt:EncryptionBase64",
            "is missing, and the provider cannot issue an authorization code without a key to " +
            "encrypt it with");

        report.Prefer(RequireProofKeyForCodeExchange, nameof(RequireProofKeyForCodeExchange),
            "is off; the sign-in widget is a public client and an intercepted authorization code " +
            "would be redeemable by whoever intercepted it");
    }
}
