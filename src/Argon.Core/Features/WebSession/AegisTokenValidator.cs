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
        {
            // Said out loud, because the silent version of this is indistinguishable from a bad
            // token: every exchange answers 401 and nothing anywhere names the reason. It is also
            // the state a deployment that registered the feature and configured nothing is in.
            logger.LogWarning("Refused a web session exchange: WebSession:TrustedAudiences is empty, "
                            + "so no token can ever be exchanged and the web client cannot sign in");
            return null;
        }

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
            // IDX10214 on its own names neither the audience the token carried nor the ones this
            // deployment accepts, and that pair is what the answer nearly always turns out to be: a
            // web client served from an origin nobody added to the list, because Aegis sets the
            // audience from the origin of the redirect_uri the flow came through. Without them the
            // only way to find out is to read the identity server's log beside this one.
            //
            // Read back unvalidated on purpose. The token has already been refused by this point and
            // nothing below trusts a word of it; this is a diagnostic, and a token whose signature
            // failed still has an audience worth printing.
            logger.LogWarning(
                "Rejected a web session exchange: {Error}. The token named [{Presented}]; this deployment trusts [{Trusted}]",
                result.Exception?.Message,
                string.Join(", ", DescribeAudiences(token)),
                string.Join(", ", settings.TrustedAudiences.Keys));

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

        if (audience is null)
            return null;

        var appId = settings.TrustedAudiences[audience];

        // AND THE APPLICATION ITSELF, not only where the token was going.
        //
        // The two come apart on any origin more than one application can claim. The audience is the
        // origin of a redirect_uri, and a redirect_uri is checked against the registration of
        // whichever client asked — so `https://localhost:5005`, the origin every developer registers
        // for their own build, is an audience that any registered application can be issued a token
        // for. Trusting one so that our own web client can be worked on would otherwise hand a
        // third-party application a full Argon session with the scopes an installed client gets.
        //
        // `azp` is the one claim that names the application rather than the destination, so pinning
        // it is what keeps this door ours — and what makes a development origin something that can
        // be added to the list at all.
        if (AuthorizedParty(jwt) is not { } party)
            // Absent is not refused: an identity server that does not put the claim on an access
            // token would lose web sign-in entirely, which is a worse failure than the one this
            // guards against. It is reported instead, because a pin that is quietly not applied is
            // no pin at all, and this line is how a deployment finds out it is running without one.
            logger.LogWarning("A web session exchange for {Audience} carried no azp or client_id claim, so it "
                            + "was accepted on its audience alone and the application pin did not apply", audience);
        else if (!string.Equals(party, appId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Rejected a web session exchange: the token was issued to application {Party}, "
                            + "but audience {Audience} is registered to {AppId}", party, audience, appId);

            return null;
        }

        return new AegisIdentity(userId, audience);
    }

    /// <summary>
    /// The application a token was issued to, by either of the two names it can go under.
    /// </summary>
    /// <remarks>
    /// <c>azp</c> is what OpenIddict writes onto an access token. <c>client_id</c> is read as well
    /// because it is the spelling another authorization server is free to use instead, and a
    /// deployment running one should not fall through to the unpinned path without saying so.
    /// </remarks>
    private static string? AuthorizedParty(JsonWebToken jwt)
    {
        if (jwt.TryGetClaim("azp", out var azp) && !string.IsNullOrWhiteSpace(azp.Value))
            return azp.Value;

        return jwt.TryGetClaim("client_id", out var clientId) && !string.IsNullOrWhiteSpace(clientId.Value)
            ? clientId.Value
            : null;
    }

    /// <summary>
    /// The audiences a refused token claimed, for the log line above and nothing else.
    /// </summary>
    /// <remarks>
    /// Never throws: this runs on the failure path, where the input is by definition something the
    /// handler has already declined, and a token too malformed to read is itself the answer.
    /// </remarks>
    private static IEnumerable<string> DescribeAudiences(string token)
    {
        try
        {
            var audiences = new JsonWebToken(token).Audiences.ToArray();

            return audiences.Length > 0 ? audiences : ["<none>"];
        }
        catch (Exception)
        {
            return ["<unreadable>"];
        }
    }
}
