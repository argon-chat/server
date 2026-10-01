namespace Argon.Features.Integrations.Connections.Providers;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using static ConnectionDetailKeys;

/// <summary>
/// Steam: OpenID 2.0 for the identity, the Web API key for everything else, and no token at all.
/// </summary>
/// <remarks>
/// <para>The state rides inside <c>openid.return_to</c>, because that is the only thing Steam hands
/// back verbatim. The assertion is verified with a second round trip to Steam
/// (<c>check_authentication</c>), and <c>return_to</c> is checked against ours, so a response minted
/// for another site does not open a link here.</para>
///
/// <para>Games owned and account age are only there when the profile is public; a private profile
/// links fine and shows the persona alone.</para>
/// </remarks>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parsers are exercised against recorded answers.")]
public sealed partial class SteamConnectionProvider(HttpClient http, IOptions<ConnectionsOptions> options) : IConnectionProvider
{
    private const string OpenIdEndpoint   = "https://steamcommunity.com/openid/login";
    private const string OpenIdNamespace  = "http://specs.openid.net/auth/2.0";
    private const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";
    private const string Api              = "https://api.steampowered.com";

    private readonly SteamConnectionOptions steam = options.Value.Steam;

    public ConnectionProvider   Kind         => ConnectionProvider.STEAM;
    public ConnectionCapability Capabilities => ConnectionCapability.DETAILS;

    public bool IsConfigured => steam.IsConfigured;
    public bool UsesPkce     => false;
    public bool KeepsTokens  => false;

    public string BuildAuthorizationUrl(HandshakeContext ctx)
    {
        var returnTo = OAuthCodeFlow.BuildUrl(ctx.RedirectUri, [new KeyValuePair<string, string?>("state", ctx.State)]);
        var realm    = new Uri(ctx.RedirectUri).GetLeftPart(UriPartial.Authority);

        return OAuthCodeFlow.BuildUrl(OpenIdEndpoint,
        [
            new("openid.ns", OpenIdNamespace),
            new("openid.mode", "checkid_setup"),
            new("openid.return_to", returnTo),
            new("openid.realm", realm),
            new("openid.identity", IdentifierSelect),
            new("openid.claimed_id", IdentifierSelect)
        ]);
    }

    public async Task<HandshakeResult> CompleteAsync(HandshakeContext ctx, IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var mode = query.GetValueOrDefault("openid.mode");

        if (mode == "cancel")
            return HandshakeResult.Denied();

        if (mode != "id_res")
            return HandshakeResult.Failed($"openid.mode {mode ?? "missing"}");

        if (!query.TryGetValue("openid.return_to", out var returnTo)
         || !returnTo.StartsWith(ctx.RedirectUri, StringComparison.Ordinal))
            return HandshakeResult.Failed("return_to is not ours");

        if (!TryParseSteamId(query.GetValueOrDefault("openid.claimed_id"), out var steamId))
            return HandshakeResult.Failed("claimed_id is not a Steam id");

        if (!await VerifyAssertionAsync(query, ct))
            return HandshakeResult.Failed("assertion did not verify");

        var snapshot = await ReadAsync(steamId, ct);

        return HandshakeResult.Linked(snapshot.Identity, null, snapshot.Details, "");
    }

    private async Task<bool> VerifyAssertionAsync(IReadOnlyDictionary<string, string> query, CancellationToken ct)
    {
        var form = query
           .Where(kv => kv.Key.StartsWith("openid.", StringComparison.Ordinal) && kv.Key != "openid.mode")
           .Append(new KeyValuePair<string, string>("openid.mode", "check_authentication"))
           .ToList();

        using var response = await http.PostAsync(OpenIdEndpoint, new FormUrlEncodedContent(form), ct);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new ProviderCallException(response.StatusCode, OpenIdEndpoint, body);

        return IsValidAssertion(body);
    }

    public static bool IsValidAssertion(string body)
        => body.Split('\n').Select(l => l.Trim()).Contains("is_valid:true", StringComparer.Ordinal);

    public static bool TryParseSteamId(string? claimedId, out string steamId)
    {
        steamId = "";

        if (claimedId is null)
            return false;

        var match = ClaimedId().Match(claimedId);

        if (!match.Success)
            return false;

        steamId = match.Groups["id"].Value;
        return true;
    }

    public Task<ProviderToken?> RefreshAsync(ProviderToken token, CancellationToken ct) => Task.FromResult<ProviderToken?>(null);
    public Task RevokeAsync(ProviderToken token, CancellationToken ct)                  => Task.CompletedTask;

    public Task<ProviderSnapshot> FetchAsync(ConnectionIdentity identity, ProviderToken? token, CancellationToken ct)
        => ReadAsync(identity.ExternalId, ct);

    private async Task<ProviderSnapshot> ReadAsync(string steamId, CancellationToken ct)
    {
        var summariesUrl = $"{Api}/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString(steam.WebApiKey)}&steamids={steamId}";

        using var summaries = await OAuthCodeFlow.GetJsonAsync(http, summariesUrl, null, ct);

        var player = summaries.RootElement.Object("response")?.TryGetProperty("players", out var players) == true
                  && players.ValueKind == JsonValueKind.Array && players.GetArrayLength() > 0
            ? players[0]
            : throw new ProviderCallException(HttpStatusCode.BadGateway, $"{Api}/ISteamUser/GetPlayerSummaries", "no player");

        var identity = ParseIdentity(steamId, player);
        var details  = new List<ConnectionDetail>();

        if (player.Number("timecreated") is { } created)
            details.Add(Date(Since, DateTimeOffset.FromUnixTimeSeconds(created)));

        // Both are absent for a private profile, and either may be down on its own. Neither is
        // allowed to fail the link.
        if (await TryNumberAsync($"{Api}/IPlayerService/GetOwnedGames/v1/?key={Uri.EscapeDataString(steam.WebApiKey)}&steamid={steamId}&include_played_free_games=1",
                "game_count", ct) is { } games)
            details.Add(Number(SteamGames, games));

        if (await TryNumberAsync($"{Api}/IPlayerService/GetSteamLevel/v1/?key={Uri.EscapeDataString(steam.WebApiKey)}&steamid={steamId}",
                "player_level", ct) is { } level)
            details.Add(Number(SteamLevel, level));

        return new ProviderSnapshot(identity, details);
    }

    public static ConnectionIdentity ParseIdentity(string steamId, JsonElement player)
        => new(steamId,
            player.String("personaname") ?? steamId,
            player.String("profileurl") ?? $"https://steamcommunity.com/profiles/{steamId}",
            player.String("avatarfull") ?? player.String("avatarmedium"));

    private async Task<long?> TryNumberAsync(string url, string property, CancellationToken ct)
    {
        try
        {
            using var document = await OAuthCodeFlow.GetJsonAsync(http, url, null, ct);
            return document.RootElement.Object("response")?.Number(property);
        }
        catch (ProviderCallException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    [GeneratedRegex("^https?://steamcommunity\\.com/openid/id/(?<id>\\d{5,20})$", RegexOptions.CultureInvariant)]
    private static partial Regex ClaimedId();
}
