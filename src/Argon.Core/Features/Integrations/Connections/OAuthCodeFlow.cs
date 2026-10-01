namespace Argon.Features.Integrations.Connections;

using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

/// <summary>How the token endpoint wants the client credentials.</summary>
public enum TokenClientAuth
{
    /// <summary><c>client_id</c> and <c>client_secret</c> in the form body.</summary>
    Body,

    /// <summary>HTTP Basic <c>client_id:client_secret</c>; the body carries <c>client_id</c> only where the provider wants it twice.</summary>
    Basic
}

/// <summary>What a token endpoint answered.</summary>
public sealed record OAuthTokenResponse(string AccessToken, string? RefreshToken, int? ExpiresIn, string Scope, string? IdToken)
{
    /// <param name="previous">The token being refreshed, whose refresh token and scopes stand in when the answer omits them.</param>
    public ProviderToken ToToken(DateTimeOffset now, ProviderToken? previous = null)
        => new(AccessToken,
            RefreshToken ?? previous?.RefreshToken,
            ExpiresIn is { } s ? now.AddSeconds(s) : previous?.ExpiresAt,
            string.IsNullOrWhiteSpace(Scope) ? previous?.Scopes ?? "" : Scope);
}

/// <summary>
/// The plumbing every authorization-code provider shares: state and PKCE, the authorize URL, the
/// code exchange, the refresh, and the one answer that means "the grant is gone".
/// </summary>
public static class OAuthCodeFlow
{
    public static string NewState()        => Base64Url(RandomNumberGenerator.GetBytes(32));
    public static string NewPkceVerifier() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string PkceChallenge(string verifier)
        => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string BuildUrl(string endpoint, IEnumerable<KeyValuePair<string, string?>> query)
        => QueryHelpers.AddQueryString(endpoint, query);

    public static async Task<OAuthTokenResponse> ExchangeCodeAsync(
        HttpClient http, string endpoint, ProviderConnectionOptions app, TokenClientAuth auth,
        string code, string redirectUri, string? pkceVerifier, CancellationToken ct,
        params KeyValuePair<string, string>[] extra)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri)
        };

        if (pkceVerifier is not null)
            form.Add(new("code_verifier", pkceVerifier));

        form.AddRange(extra);

        return await RequestTokenAsync(http, endpoint, app, auth, form, ct)
            ?? throw new ProviderCallException(HttpStatusCode.BadRequest, endpoint, "invalid_grant on a code exchange");
    }

    /// <summary>Null when the provider says the grant is gone; throws on anything else that is not a token.</summary>
    public static Task<OAuthTokenResponse?> RefreshAsync(
        HttpClient http, string endpoint, ProviderConnectionOptions app, TokenClientAuth auth,
        string refreshToken, CancellationToken ct)
        => RequestTokenAsync(http, endpoint, app, auth,
        [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken)
        ], ct);

    private static async Task<OAuthTokenResponse?> RequestTokenAsync(
        HttpClient http, string endpoint, ProviderConnectionOptions app, TokenClientAuth auth,
        List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (auth == TokenClientAuth.Basic)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(app.ClientId)}:{Uri.EscapeDataString(app.ClientSecret)}")));
            form.Add(new("client_id", app.ClientId));
        }
        else
        {
            form.Add(new("client_id", app.ClientId));
            form.Add(new("client_secret", app.ClientSecret));
        }

        request.Content = new FormUrlEncodedContent(form);

        using var response = await http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            if (IsGrantGone(response.StatusCode, body))
                return null;

            throw new ProviderCallException(response.StatusCode, endpoint, body);
        }

        return ParseToken(body, endpoint);
    }

    /// <summary>
    /// <c>invalid_grant</c> is the one refusal that is about the person rather than about us: the
    /// refresh token was revoked, the password changed, the app was removed from the account. A 400
    /// or 401 that says so is a state, not an outage.
    /// </summary>
    private static bool IsGrantGone(HttpStatusCode status, string body)
        => status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
        && (body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
         || body.Contains("Invalid refresh token", StringComparison.OrdinalIgnoreCase));

    public static OAuthTokenResponse ParseToken(string body, string endpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            var root = document.RootElement;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                throw new ProviderCallException(HttpStatusCode.BadRequest, endpoint, body);

            var access = root.TryGetProperty("access_token", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;

            if (string.IsNullOrEmpty(access))
                throw new ProviderCallException(HttpStatusCode.BadGateway, endpoint, "no access_token in the answer");

            var refresh = root.TryGetProperty("refresh_token", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            var idToken = root.TryGetProperty("id_token", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;

            int? expiresIn = root.TryGetProperty("expires_in", out var e) switch
            {
                true when e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n)       => n,
                true when e.ValueKind == JsonValueKind.String && int.TryParse(e.GetString(), out var n) => n,
                _                                                                                 => null
            };

            // Twitch answers with an array, everyone else with a space-separated string.
            var scope = root.TryGetProperty("scope", out var s) switch
            {
                true when s.ValueKind == JsonValueKind.String => s.GetString() ?? "",
                true when s.ValueKind == JsonValueKind.Array  => string.Join(' ', s.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x))),
                _                                             => ""
            };

            return new OAuthTokenResponse(access, refresh, expiresIn, scope, idToken);
        }
        catch (JsonException)
        {
            throw new ProviderCallException(HttpStatusCode.BadGateway, endpoint, "the token answer is not JSON");
        }
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url, string? bearer, CancellationToken ct,
        params KeyValuePair<string, string>[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);

        using var response = await http.SendAsync(request, ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new ProviderCallException(response.StatusCode, url, body);

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new ProviderCallException(HttpStatusCode.BadGateway, url, "the answer is not JSON");
        }
    }

    public static string? String(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    public static long? Number(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var p))
            return null;

        return p.ValueKind switch
        {
            JsonValueKind.Number when p.TryGetInt64(out var n)                 => n,
            JsonValueKind.String when long.TryParse(p.GetString(), out var n) => n,
            _                                                                  => null
        };
    }

    public static bool? Boolean(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p)
            ? p.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                _                   => null
            }
            : null;

    public static JsonElement? Object(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object
            ? p
            : null;

    public static DateTimeOffset? Date(this JsonElement element, string name)
        => DateTimeOffset.TryParse(element.String(name), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? d
            : null;
}
