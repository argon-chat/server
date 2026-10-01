namespace ArgonComplexTest;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

/// <summary>
/// GitHub and Spotify, answered in process for the connections fixtures.
/// </summary>
/// <remarks>
/// <para>Only the socket is replaced: the adapters, <c>OAuthCodeFlow</c>, the resilience handlers
/// and <c>SpotifyPlayerClient</c> are the shipped ones, as with <c>FakeKlipyApi</c>. A request to any
/// host this does not know is answered 404, so nothing in the suite reaches a real provider.</para>
///
/// <para>The contributor listing is fixed for the life of the process because the server caches it
/// for hours: a pool of contributor ids is drawn up front and tests take ids from it
/// (<see cref="NextContributorId"/>) or from a disjoint range (<see cref="NextOutsiderId"/>). Every
/// id is fresh per test, since an external identity can vouch for one Argon account at a time and
/// a trophy grant outlives the test that made it.</para>
/// </remarks>
public sealed class FakeConnectionsApi
{
    public const string ContributorRepo = "argon-chat/server";

    private readonly long[] contributors;
    private readonly long   outsiderBase;
    private int             nextContributor = -1;
    private long            nextOutsider;
    private long            nextToken;

    private readonly ConcurrentDictionary<string, (long Id, string Login)>          gitHubCodes   = new();
    private readonly ConcurrentDictionary<string, (long Id, string Login)>          gitHubTokens  = new();
    private readonly ConcurrentDictionary<string, SpotifyAccount>                    spotifyCodes  = new();
    private readonly ConcurrentDictionary<string, SpotifyAccount>                    spotifyTokens = new();
    private readonly ConcurrentDictionary<string, SpotifyAccount>                    spotifyRefresh = new();
    private readonly ConcurrentDictionary<string, string>                            playing       = new();
    private readonly ConcurrentQueue<string>                                         revoked       = new();
    private readonly ConcurrentQueue<(string Token, string Command, string Body)>    commands      = new();

    public sealed record SpotifyAccount(string Id, string DisplayName, bool Premium, int ExpiresIn)
    {
        public bool RefreshRevoked { get; set; }
    }

    public FakeConnectionsApi()
    {
        var start = Random.Shared.NextInt64(1_000_000_000, 4_000_000_000);

        contributors = Enumerable.Range(0, 250).Select(i => start + i).ToArray();
        outsiderBase = start + 1_000_000;
        nextOutsider = outsiderBase;
    }

    public long NextContributorId()
        => contributors[Interlocked.Increment(ref nextContributor) % contributors.Length];

    public long NextOutsiderId() => Interlocked.Increment(ref nextOutsider);

    /// <summary>An authorization code that GitHub will exchange for a token of this account.</summary>
    public string GitHubCode(long id, string login)
    {
        var code = $"gh-code-{Guid.NewGuid():N}";
        gitHubCodes[code] = (id, login);
        return code;
    }

    /// <summary>An authorization code for a Spotify account; <paramref name="expiresIn"/> is the access token's lifetime.</summary>
    public string SpotifyCode(SpotifyAccount account)
    {
        var code = $"sp-code-{Guid.NewGuid():N}";
        spotifyCodes[code] = account;
        return code;
    }

    /// <summary>What Spotify's currently-playing answers for this account from now on; null is a 204.</summary>
    public void SetPlaying(string spotifyUserId, string? json)
    {
        if (json is null)
            playing.TryRemove(spotifyUserId, out _);
        else
            playing[spotifyUserId] = json;
    }

    public IReadOnlyList<string> RevokedGitHubTokens => revoked.ToArray();

    public IReadOnlyList<(string Token, string Command, string Body)> SpotifyCommands => commands.ToArray();

    public static string Track(string trackId, string title, string artist, int progressMs = 30_000, int durationMs = 200_000, bool isPlaying = true)
        => JsonSerializer.Serialize(new
        {
            progress_ms            = progressMs,
            is_playing             = isPlaying,
            currently_playing_type = "track",
            item = new
            {
                id          = trackId,
                name        = title,
                duration_ms = durationMs,
                is_local    = false,
                artists     = new[] { new { name = artist } },
                album       = new { name = "Album", images = new[] { new { url = "https://i.scdn.co/300", width = 300, height = 300 } } },
                external_urls = new { spotify = $"https://open.spotify.com/track/{trackId}" }
            }
        });

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(FakeConnectionsApi api) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri    = request.RequestUri!;
            var body   = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var bearer = request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter ?? "" : "";

            return (uri.Host, request.Method.Method, uri.AbsolutePath) switch
            {
                ("github.com", "POST", "/login/oauth/access_token")             => api.GitHubToken(Form(body)),
                ("api.github.com", "GET", "/user")                              => api.GitHubUser(bearer),
                ("api.github.com", "GET", var p) when p.EndsWith("/contributors", StringComparison.Ordinal)
                                                                                 => api.Contributors(p, uri.Query),
                ("api.github.com", "DELETE", var p) when p.EndsWith("/grant", StringComparison.Ordinal)
                                                                                 => api.RevokeGitHub(body),
                ("accounts.spotify.com", "POST", "/api/token")                  => api.SpotifyToken(Form(body)),
                ("api.spotify.com", "GET", "/v1/me")                            => api.SpotifyMe(bearer),
                ("api.spotify.com", "GET", "/v1/me/player/currently-playing")   => api.CurrentlyPlaying(bearer),
                ("api.spotify.com", "PUT", var p) when p.StartsWith("/v1/me/player/", StringComparison.Ordinal)
                                                                                 => api.Command(bearer, p, body),
                _                                                                => Answer(HttpStatusCode.NotFound, $"no fake for {request.Method} {uri}")
            };
        }
    }

    // ── GitHub ───────────────────────────────────────────────────────────────────────────────

    private HttpResponseMessage GitHubToken(Dictionary<string, string> form)
    {
        if (!form.TryGetValue("code", out var code) || !gitHubCodes.TryRemove(code, out var account))
            return Json(HttpStatusCode.OK, new { error = "bad_verification_code" });

        var token = $"gho_{Interlocked.Increment(ref nextToken)}_{Guid.NewGuid():N}";
        gitHubTokens[token] = account;

        return Json(HttpStatusCode.OK, new { access_token = token, token_type = "bearer", scope = "" });
    }

    private HttpResponseMessage GitHubUser(string bearer)
        => gitHubTokens.TryGetValue(bearer, out var account)
            ? Json(HttpStatusCode.OK, new
            {
                id           = account.Id,
                login        = account.Login,
                html_url     = $"https://github.com/{account.Login}",
                avatar_url   = $"https://avatars.githubusercontent.com/u/{account.Id}",
                public_repos = 12,
                followers    = 34,
                created_at   = "2015-03-04T05:06:07Z"
            })
            : Answer(HttpStatusCode.Unauthorized, """{"message":"Bad credentials"}""");

    private HttpResponseMessage Contributors(string path, string query)
    {
        if (!path.Contains(ContributorRepo, StringComparison.Ordinal))
            return Answer(HttpStatusCode.NotFound, """{"message":"Not Found"}""");

        // Two pages, so the Link header is followed the way the real listing needs.
        var page  = query.Contains("page=2", StringComparison.Ordinal) ? 2 : 1;
        var half  = contributors.Length / 2;
        var slice = page == 1 ? contributors[..half] : contributors[half..];
        var json  = JsonSerializer.Serialize(slice.Select(id => new { id, login = $"c{id}", type = "User" })
           .Append(new { id = 41898282L, login = "github-actions[bot]", type = "Bot" }));

        var response = Answer(HttpStatusCode.OK, json, "application/json");

        if (page == 1)
            response.Headers.TryAddWithoutValidation("Link", $"<https://api.github.com/repos/{ContributorRepo}/contributors?per_page=100&anon=false&page=2>; rel=\"next\"");

        return response;
    }

    private HttpResponseMessage RevokeGitHub(string body)
    {
        using var document = JsonDocument.Parse(body);

        if (document.RootElement.TryGetProperty("access_token", out var token) && token.GetString() is { } value)
        {
            revoked.Enqueue(value);
            gitHubTokens.TryRemove(value, out _);
        }

        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    // ── Spotify ──────────────────────────────────────────────────────────────────────────────

    private HttpResponseMessage SpotifyToken(Dictionary<string, string> form)
    {
        SpotifyAccount? account = null;

        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code" when form.TryGetValue("code", out var code) && spotifyCodes.TryRemove(code, out var byCode):
                account = byCode;
                break;
            case "refresh_token" when form.TryGetValue("refresh_token", out var refresh) && spotifyRefresh.TryGetValue(refresh, out var byRefresh):
                if (byRefresh.RefreshRevoked)
                    return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "Refresh token revoked" });
                account = byRefresh;
                break;
        }

        if (account is null)
            return Json(HttpStatusCode.BadRequest, new { error = "invalid_grant" });

        var access  = $"sp_at_{Interlocked.Increment(ref nextToken)}";
        var refreshToken = $"sp_rt_{account.Id}";

        spotifyTokens[access]        = account;
        spotifyRefresh[refreshToken] = account;

        return Json(HttpStatusCode.OK, new
        {
            access_token  = access,
            token_type    = "Bearer",
            expires_in    = account.ExpiresIn,
            refresh_token = form.GetValueOrDefault("grant_type") == "refresh_token" ? null : refreshToken,
            scope         = "user-read-currently-playing user-read-playback-state user-read-private user-modify-playback-state"
        });
    }

    /// <summary>Makes every later refresh of this account answer invalid_grant, as a revoked app does.</summary>
    public void RevokeSpotify(string spotifyUserId)
    {
        foreach (var account in spotifyRefresh.Values.Where(a => a.Id == spotifyUserId))
            account.RefreshRevoked = true;
    }

    private HttpResponseMessage SpotifyMe(string bearer)
        => spotifyTokens.TryGetValue(bearer, out var account)
            ? Json(HttpStatusCode.OK, new
            {
                id            = account.Id,
                display_name  = account.DisplayName,
                product       = account.Premium ? "premium" : "free",
                followers     = new { total = 7 },
                external_urls = new { spotify = $"https://open.spotify.com/user/{account.Id}" }
            })
            : Answer(HttpStatusCode.Unauthorized, """{"error":{"status":401,"message":"The access token expired"}}""");

    private HttpResponseMessage CurrentlyPlaying(string bearer)
    {
        if (!spotifyTokens.TryGetValue(bearer, out var account))
            return Answer(HttpStatusCode.Unauthorized, """{"error":{"status":401}}""");

        return playing.TryGetValue(account.Id, out var json)
            ? Answer(HttpStatusCode.OK, json, "application/json")
            : new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    private HttpResponseMessage Command(string bearer, string path, string body)
    {
        if (!spotifyTokens.ContainsKey(bearer))
            return Answer(HttpStatusCode.Unauthorized, """{"error":{"status":401}}""");

        commands.Enqueue((bearer, path, body));
        return new HttpResponseMessage(HttpStatusCode.NoContent);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Form(string body)
        => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
           .Select(pair => pair.Split('=', 2))
           .ToDictionary(kv => Uri.UnescapeDataString(kv[0]), kv => kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "");

    private static HttpResponseMessage Json(HttpStatusCode status, object payload)
        => Answer(status, JsonSerializer.Serialize(payload), "application/json");

    private static HttpResponseMessage Answer(HttpStatusCode status, string body, string mediaType = "text/plain")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
}
