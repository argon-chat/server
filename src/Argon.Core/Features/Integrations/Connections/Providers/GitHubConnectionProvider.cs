namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ConnectionDetailKeys;

/// <summary>
/// GitHub, as an OAuth App: no scope (the public profile is all that is read), a token that never
/// expires, and the contributor listing the trophy is paid from.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parsers are exercised against recorded answers.")]
public sealed partial class GitHubConnectionProvider(HttpClient http, IOptions<ConnectionsOptions> options)
    : OAuthConnectionProvider(http, options.Value.GitHub)
{
    private const string Api = "https://api.github.com";

    private readonly GitHubConnectionOptions github = options.Value.GitHub;

    private static readonly KeyValuePair<string, string>[] ApiHeaders =
    [
        new("User-Agent", "Argon"),
        new("X-GitHub-Api-Version", "2022-11-28")
    ];

    public override ConnectionProvider   Kind         => ConnectionProvider.GITHUB;
    public override ConnectionCapability Capabilities => ConnectionCapability.DETAILS | ConnectionCapability.TROPHY;

    // OAuth Apps ignore PKCE; the exchange is client-secret only.
    public override bool UsesPkce => false;

    protected override string AuthorizeEndpoint => "https://github.com/login/oauth/authorize";
    protected override string TokenEndpoint     => "https://github.com/login/oauth/access_token";
    protected override string Scopes            => "";

    protected override TokenClientAuth TokenAuth => TokenClientAuth.Body;

    protected override IEnumerable<KeyValuePair<string, string?>> ExtraAuthorizeParameters
        => [new("allow_signup", "false")];

    protected override async Task<ProviderSnapshot> ReadAsync(ProviderToken token, OAuthTokenResponse? raw, CancellationToken ct)
    {
        using var document = await OAuthCodeFlow.GetJsonAsync(Http, $"{Api}/user", token.AccessToken, ct, ApiHeaders);

        return Parse(document.RootElement);
    }

    public static ProviderSnapshot Parse(JsonElement user)
    {
        var id    = user.Number("id") ?? throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/user", "no id");
        var login = user.String("login") ?? id.ToString(CultureInfo.InvariantCulture);

        var identity = new ConnectionIdentity(
            id.ToString(CultureInfo.InvariantCulture),
            login,
            user.String("html_url") ?? $"https://github.com/{login}",
            user.String("avatar_url"));

        var details = new List<ConnectionDetail>();

        if (user.Number("public_repos") is { } repos)
            details.Add(Number(GitHubPublicRepos, repos));
        if (user.Number("followers") is { } followers)
            details.Add(Number(GitHubFollowers, followers));
        if (user.Date("created_at") is { } since)
            details.Add(Date(Since, since));

        return new ProviderSnapshot(identity, details);
    }

    /// <summary>OAuth App tokens do not expire and have no refresh token; the one we hold stays good.</summary>
    public override Task<ProviderToken?> RefreshAsync(ProviderToken token, CancellationToken ct)
        => Task.FromResult<ProviderToken?>(token);

    public override async Task RevokeAsync(ProviderToken token, CancellationToken ct)
    {
        var endpoint = $"{Api}/applications/{App.ClientId}/grant";

        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);

        request.Headers.Authorization = BasicClientCredentials();
        request.Headers.TryAddWithoutValidation("User-Agent", "Argon");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Content = JsonContent.Create(new { access_token = token.AccessToken });

        using var response = await Http.SendAsync(request, ct);

        if (!response.IsSuccessStatusCode && response.StatusCode is not HttpStatusCode.NotFound)
            throw new ProviderCallException(response.StatusCode, endpoint, await response.Content.ReadAsStringAsync(ct));
    }

    // ── the contributor listing, for IConnectionTrophiesGrain ────────────────────────────────

    public bool CanListContributors
        => !string.IsNullOrWhiteSpace(github.ServerToken) && github.ContributorRepos.Count > 0;

    /// <summary>
    /// The numeric ids of everyone with a commit on the default branch of any configured repository.
    /// </summary>
    /// <remarks>
    /// Ids, not logins: logins change, ids do not. GitHub caps the listing at 500 per repository and
    /// pages it by 100; ten pages is the safety stop, not a limit anyone is expected to reach.
    /// </remarks>
    public async Task<HashSet<long>> ListContributorIdsAsync(CancellationToken ct)
    {
        var ids = new HashSet<long>();

        foreach (var repo in github.ContributorRepos)
        {
            var url = $"{Api}/repos/{repo}/contributors?per_page=100&anon=false";

            for (var page = 0; url is not null && page < 10; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", github.ServerToken);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

                foreach (var (name, value) in ApiHeaders)
                    request.Headers.TryAddWithoutValidation(name, value);

                using var response = await Http.SendAsync(request, ct);

                // An empty repository answers 204 with no body.
                if (response.StatusCode == HttpStatusCode.NoContent)
                    break;

                var body = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                    throw new ProviderCallException(response.StatusCode, url, body);

                ids.UnionWith(ParseContributorIds(body));

                url = NextLink(response.Headers);
            }
        }

        return ids;
    }

    public static IEnumerable<long> ParseContributorIds(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var contributor in document.RootElement.EnumerateArray())
        {
            if (string.Equals(contributor.String("type"), "Bot", StringComparison.OrdinalIgnoreCase))
                continue;

            if (contributor.Number("id") is { } id)
                yield return id;
        }
    }

    public static string? NextLink(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("Link", out var values))
            return null;

        foreach (var value in values)
        {
            var match = LinkNext().Match(value);

            if (match.Success)
                return match.Groups["url"].Value;
        }

        return null;
    }

    [GeneratedRegex("<(?<url>[^>]+)>;\\s*rel=\"next\"", RegexOptions.CultureInvariant)]
    private static partial Regex LinkNext();
}
