namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;

/// <summary>
/// The authorization-code shape every provider but Steam shares: an authorize URL, a code exchange,
/// one identity read, a refresh. A provider class is its three URLs, its scopes and two parsers.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parsers are exercised against recorded answers, the transport is the vendor's.")]
public abstract class OAuthConnectionProvider(HttpClient http, ProviderConnectionOptions app) : IConnectionProvider
{
    protected HttpClient                Http => http;
    protected ProviderConnectionOptions App  => app;

    public abstract ConnectionProvider   Kind         { get; }
    public abstract ConnectionCapability Capabilities { get; }

    public virtual bool IsConfigured => app.IsConfigured;
    public virtual bool UsesPkce     => true;
    public virtual bool KeepsTokens  => true;

    protected abstract string AuthorizeEndpoint { get; }
    protected abstract string TokenEndpoint     { get; }
    protected abstract string Scopes            { get; }

    protected virtual TokenClientAuth TokenAuth => TokenClientAuth.Basic;

    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraAuthorizeParameters => [];

    public virtual string BuildAuthorizationUrl(HandshakeContext ctx)
    {
        var query = new List<KeyValuePair<string, string?>>
        {
            new("client_id", app.ClientId),
            new("redirect_uri", ctx.RedirectUri),
            new("response_type", "code"),
            new("state", ctx.State)
        };

        if (!string.IsNullOrEmpty(Scopes))
            query.Add(new("scope", Scopes));

        if (UsesPkce && ctx.PkceVerifier is not null)
        {
            query.Add(new("code_challenge", OAuthCodeFlow.PkceChallenge(ctx.PkceVerifier)));
            query.Add(new("code_challenge_method", "S256"));
        }

        query.AddRange(ExtraAuthorizeParameters);

        return OAuthCodeFlow.BuildUrl(AuthorizeEndpoint, query);
    }

    public async Task<HandshakeResult> CompleteAsync(HandshakeContext ctx, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        if (query.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
        {
            return error is "access_denied" or "user_denied" or "consent_required" or "interaction_required"
                ? HandshakeResult.Denied()
                : HandshakeResult.Failed(error);
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            return HandshakeResult.Failed("no code");

        var response = await OAuthCodeFlow.ExchangeCodeAsync(Http, TokenEndpoint, App, TokenAuth, code, ctx.RedirectUri,
            UsesPkce ? ctx.PkceVerifier : null, ct);

        var token    = response.ToToken(DateTimeOffset.UtcNow);
        var snapshot = await ReadAsync(token, response, ct);

        return HandshakeResult.Linked(snapshot.Identity, KeepsTokens ? token : null, snapshot.Details, token.Scopes);
    }

    /// <summary>The identity and details behind a token. <paramref name="raw"/> is the token answer on a link, null on a refresh.</summary>
    protected abstract Task<ProviderSnapshot> ReadAsync(ProviderToken token, OAuthTokenResponse? raw, CancellationToken ct);

    public virtual async Task<ProviderToken?> RefreshAsync(ProviderToken token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.RefreshToken))
            return null;

        var response = await OAuthCodeFlow.RefreshAsync(Http, TokenEndpoint, App, TokenAuth, token.RefreshToken, ct);

        return response?.ToToken(DateTimeOffset.UtcNow, token);
    }

    public virtual Task RevokeAsync(ProviderToken token, CancellationToken ct) => Task.CompletedTask;

    public virtual Task<ProviderSnapshot> FetchAsync(ConnectionIdentity identity, ProviderToken? token, CancellationToken ct)
        => token is null
            ? throw new InvalidOperationException($"{Kind} needs a token to read the account")
            : ReadAsync(token, null, ct);

    protected AuthenticationHeaderValue BasicClientCredentials()
        => new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(App.ClientId)}:{Uri.EscapeDataString(App.ClientSecret)}")));

    /// <summary>A POST of a form whose failure is logged by the caller and never rethrown — revocation.</summary>
    protected async Task PostFormQuietlyAsync(string endpoint, IEnumerable<KeyValuePair<string, string>> form,
        AuthenticationHeaderValue? auth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        request.Headers.Authorization = auth;
        request.Content               = new FormUrlEncodedContent(form);

        using var response = await Http.SendAsync(request, ct);

        // 404 and 400 on a revoke mean the token was already gone, which is the state wanted.
        if (!response.IsSuccessStatusCode && response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.BadRequest))
            throw new ProviderCallException(response.StatusCode, endpoint, await response.Content.ReadAsStringAsync(ct));
    }
}
