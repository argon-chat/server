namespace Argon.Features.WebSession;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Who a caller is, according to a token the identity server signed.
/// </summary>
/// <param name="Audience">
/// Which of the trusted audiences the token was issued for — the application, in practice, and what
/// decides the id the session is recorded under.
/// </param>
public sealed record AegisIdentity(Guid UserId, string Audience);

/// <summary>
/// Checks an Aegis access token before it is traded for an Argon session.
/// </summary>
/// <remarks>
/// The key set is fetched and refreshed by <see cref="ConfigurationManager{T}"/> — the same machinery
/// the developer console's interceptor uses — so a rotated signing key is picked up rather than
/// failing every exchange until the process restarts.
/// </remarks>
public sealed class AegisTokenValidator(IOptions<WebSessionOptions> options, ILogger<AegisTokenValidator> logger)
{
    private readonly Lazy<ConfigurationManager<OpenIdConnectConfiguration>> configuration = new(() =>
        new ConfigurationManager<OpenIdConnectConfiguration>(
            options.Value.MetadataAddress,
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever()));

    private static readonly JsonWebTokenHandler Handler = new();

    public async Task<AegisIdentity?> ValidateAsync(string token, CancellationToken ct = default)
    {
        var settings = options.Value;

        if (settings.TrustedAudiences.Count == 0)
            return null;

        var keys = await configuration.Value.GetConfigurationAsync(ct);

        var result = await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer              = settings.ValidIssuer,
            ValidAudiences           = settings.TrustedAudiences.Keys,
            ValidateAudience         = true,
            IssuerSigningKeys        = keys.SigningKeys,
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true
        });

        if (!result.IsValid)
        {
            logger.LogWarning("Rejected a web session exchange: {Error}", result.Exception?.Message);
            return null;
        }

        if (result.ClaimsIdentity.FindFirst("sub")?.Value is not { } subject || !Guid.TryParse(subject, out var userId))
        {
            logger.LogWarning("A token passed validation but carries no usable subject");
            return null;
        }

        // Validation proved the token names one of the trusted audiences; this finds out which,
        // because the application id the session is filed under hangs off it. A token naming several
        // is answered by the first that is trusted rather than refused: the audience is a pin on who
        // the token was minted for, and one extra resource on it does not make it a different token.
        if (result.SecurityToken is not JsonWebToken jwt)
            return null;

        var audience = jwt.Audiences.FirstOrDefault(settings.TrustedAudiences.ContainsKey);

        return audience is null ? null : new AegisIdentity(userId, audience);
    }
}
