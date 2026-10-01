namespace Argon.Features.Integrations.Connections;

/// <summary>
/// One provider: how to send a person to it, how to read who they are when they come back, and
/// what else it will say about them.
/// </summary>
/// <remarks>
/// Every implementation is a vendor HTTP adapter registered on the silo roles only. What the
/// handshake grain needs from all of them is the same three steps — an authorize URL, a completion
/// that turns the callback into an identity (and, for most, tokens), and a details fetch — and the
/// differences are URLs and parsers. Steam (OpenID 2.0, no token) and Telegram (OIDC, identity in
/// the ID token, no API behind the access token) fit the same shape with <see cref="RefreshAsync"/>
/// answering null.
/// </remarks>
public interface IConnectionProvider
{
    ConnectionProvider   Kind         { get; }
    ConnectionCapability Capabilities { get; }

    /// <summary>A client id (Steam: a Web API key) is present. Otherwise the provider is not offered.</summary>
    bool IsConfigured { get; }

    /// <summary>Whether a PKCE verifier is minted for the handshake.</summary>
    bool UsesPkce { get; }

    /// <summary>Whether a token is stored after linking. False when there is nothing to call with it.</summary>
    bool KeepsTokens { get; }

    string BuildAuthorizationUrl(HandshakeContext ctx);

    /// <summary>Turns the provider's callback query into an identity, tokens and details.</summary>
    Task<HandshakeResult> CompleteAsync(HandshakeContext ctx, IReadOnlyDictionary<string, string> query, CancellationToken ct);

    /// <summary>
    /// A fresh token, or null when the grant is gone (<c>invalid_grant</c>) and the person has to link
    /// again. Throws on a transport failure, which is not the same thing.
    /// </summary>
    Task<ProviderToken?> RefreshAsync(ProviderToken token, CancellationToken ct);

    /// <summary>Best effort; providers without a revoke endpoint do nothing.</summary>
    Task RevokeAsync(ProviderToken token, CancellationToken ct);

    /// <summary>
    /// The account as the provider describes it now: the identity again (handles change) and the
    /// details. Only called for providers with the <c>DETAILS</c> capability.
    /// </summary>
    Task<ProviderSnapshot> FetchAsync(ConnectionIdentity identity, ProviderToken? token, CancellationToken ct);
}

/// <summary>What one handshake was started with.</summary>
public sealed record HandshakeContext(string State, string RedirectUri, string? PkceVerifier);

/// <summary>Who the provider says the account is. The id is the provider's stable one, never the handle.</summary>
public sealed record ConnectionIdentity(string ExternalId, string Name, string? Url, string? AvatarUrl);

/// <summary>One read of the account: who it is and what the provider says about it.</summary>
public sealed record ProviderSnapshot(ConnectionIdentity Identity, IReadOnlyList<ConnectionDetail> Details);

public enum HandshakeOutcome
{
    Linked,

    /// <summary>The person said no at the provider.</summary>
    Denied,

    /// <summary>The provider or the network did not cooperate; nothing about the person is known.</summary>
    Failed
}

public sealed record HandshakeResult(
    HandshakeOutcome Outcome,
    ConnectionIdentity? Identity,
    ProviderToken? Token,
    IReadOnlyList<ConnectionDetail> Details,
    string Scopes,
    string? Error)
{
    public static HandshakeResult Linked(ConnectionIdentity identity, ProviderToken? token, IReadOnlyList<ConnectionDetail> details, string scopes)
        => new(HandshakeOutcome.Linked, identity, token, details, scopes, null);

    public static HandshakeResult Denied()
        => new(HandshakeOutcome.Denied, null, null, [], "", null);

    public static HandshakeResult Failed(string error)
        => new(HandshakeOutcome.Failed, null, null, [], "", error);
}

/// <summary>A provider answered with something other than success.</summary>
public sealed class ProviderCallException(HttpStatusCode status, string endpoint, string? body)
    : Exception($"{endpoint} answered {(int)status}: {Truncate(body)}")
{
    public HttpStatusCode Status   { get; } = status;
    public string         Endpoint { get; } = endpoint;

    public bool IsUnauthorized => Status is HttpStatusCode.Unauthorized;
    public bool IsRateLimited  => Status is HttpStatusCode.TooManyRequests;

    private static string Truncate(string? body)
        => string.IsNullOrEmpty(body) ? "" : body.Length <= 200 ? body : body[..200] + "…";
}
