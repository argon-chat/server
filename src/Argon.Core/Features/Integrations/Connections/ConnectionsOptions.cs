namespace Argon.Features.Integrations.Connections;

using Argon.Features.Clustering;

/// <summary>
/// Linked external accounts: the providers, the callback, the token sealing key and the clocks.
/// </summary>
/// <remarks>
/// Bound on the silo roles that run the adapters and nowhere else: the entry point's callback
/// endpoints take everything they show from the handshake grain's answer, so no client secret is
/// ever materialised on the role the internet can reach.
/// </remarks>
public sealed class ConnectionsOptions : IValidatableFeatureOptions
{
    public const string SectionName = "Connections";

    public bool Enabled { get; set; } = true;

    /// <summary>Where providers redirect to: <c>{PublicCallbackBase}/connections/callback/{provider}</c>.</summary>
    public string PublicCallbackBase { get; set; } = "";

    /// <summary>The web client, for the "sign in to Argon in this browser" link on the callback page.</summary>
    public string WebAppUrl { get; set; } = "";

    /// <summary>How long a started handshake, and a parked result awaiting sign-in, stay valid.</summary>
    public TimeSpan HandshakeTtl { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>AES-256-GCM key for tokens at rest, base64 of 32 bytes. Vault.</summary>
    public string TokenKey { get; set; } = "";

    /// <summary>Which key <see cref="TokenKey"/> is; written into every sealed blob. 1..255.</summary>
    public int TokenKeyVersion { get; set; } = 1;

    /// <summary>Earlier keys by version, kept until every row sealed under them has been re-sealed.</summary>
    public Dictionary<string, string> RetiredTokenKeys { get; set; } = new();

    public int      BeginConnectPerMinute { get; set; } = 5;
    public TimeSpan RefreshCooldown       { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan MaintenanceInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How close to its expiry a token is refreshed when it is opened for use. Short on purpose:
    /// Spotify and Google tokens live an hour, and a lead longer than that refreshes on every open.
    /// </summary>
    public TimeSpan TokenRefreshLead { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often maintenance renews the token of a connection nothing has used. That renewal is what
    /// notices a grant revoked at the provider, and it keeps refresh tokens that expire when idle alive.
    /// </summary>
    public TimeSpan TokenKeepAliveEvery { get; set; } = TimeSpan.FromDays(1);

    public int MaintenanceBatch { get; set; } = 200;

    public GitHubConnectionOptions   GitHub   { get; set; } = new();
    public SteamConnectionOptions    Steam    { get; set; } = new();
    public SpotifyConnectionOptions  Spotify  { get; set; } = new();
    public ProviderConnectionOptions Twitter  { get; set; } = new() { DetailsRefreshEvery = TimeSpan.FromDays(30) };
    public TwitchConnectionOptions   Twitch   { get; set; } = new();
    public ProviderConnectionOptions YouTube  { get; set; } = new();
    public ProviderConnectionOptions Telegram { get; set; } = new();

    public ProviderConnectionOptions For(ConnectionProvider provider) => provider switch
    {
        ConnectionProvider.GITHUB   => GitHub,
        ConnectionProvider.STEAM    => Steam,
        ConnectionProvider.SPOTIFY  => Spotify,
        ConnectionProvider.TWITTER  => Twitter,
        ConnectionProvider.TWITCH   => Twitch,
        ConnectionProvider.YOUTUBE  => YouTube,
        ConnectionProvider.TELEGRAM => Telegram,
        _                           => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    public string CallbackUrl(ConnectionProvider provider)
        => ConnectionProviders.CallbackUrl(PublicCallbackBase, provider);

    /// <summary>Whether any provider has an app registered; nothing is offered or validated otherwise.</summary>
    public bool AnyProviderConfigured => ConnectionProviders.All.Any(p => For(p).IsConfigured);

    public void Validate(IFeatureConfigurationReport report)
    {
        // A deployment with no provider registered has nothing to offer and nothing to get wrong;
        // the section may be absent altogether. The rules below bind once one app is set up.
        if (!Enabled || !AnyProviderConfigured)
            return;

        report.Require(Uri.TryCreate(PublicCallbackBase, UriKind.Absolute, out var callback)
                       && callback.Scheme is "https" or "http"
                       && string.IsNullOrEmpty(callback.Query),
            nameof(PublicCallbackBase), "must be an absolute http(s) address without a query");

        report.Prefer(callback is null || callback.Scheme == "https" || callback.IsLoopback,
            nameof(PublicCallbackBase), "is plain http on a public host; every provider redirects a code to it");

        report.Prefer(string.IsNullOrWhiteSpace(WebAppUrl) || Uri.TryCreate(WebAppUrl, UriKind.Absolute, out _), nameof(WebAppUrl),
            "is not an absolute address, so the callback page cannot send a signed-out browser to the web client");

        report.RequireRange(HandshakeTtl, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), nameof(HandshakeTtl));
        report.RequireRange(BeginConnectPerMinute, 1, 1000, nameof(BeginConnectPerMinute));
        report.RequireRange(RefreshCooldown, TimeSpan.Zero, TimeSpan.FromDays(1), nameof(RefreshCooldown));
        report.RequireRange(MaintenanceInterval, TimeSpan.FromMinutes(1), TimeSpan.FromDays(1), nameof(MaintenanceInterval));
        report.RequireRange(TokenRefreshLead, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), nameof(TokenRefreshLead));
        report.RequireRange(TokenKeepAliveEvery, TimeSpan.FromHours(1), TimeSpan.FromDays(30), nameof(TokenKeepAliveEvery));
        report.RequireRange(MaintenanceBatch, 1, 10_000, nameof(MaintenanceBatch));
        report.RequireRange(TokenKeyVersion, 1, 255, nameof(TokenKeyVersion));

        var keepsTokens = GitHub.IsConfigured || Spotify.IsConfigured || Twitter.IsConfigured
                       || Twitch.IsConfigured || YouTube.IsConfigured;

        report.Require(!keepsTokens || TokenSealer.IsUsableKey(TokenKey), nameof(TokenKey),
            "must be base64 of 32 bytes while any provider that keeps tokens has a client id");

        foreach (var (version, key) in RetiredTokenKeys)
        {
            report.Require(int.TryParse(version, out var v) && v is >= 1 and <= 255 && v != TokenKeyVersion,
                nameof(RetiredTokenKeys), $"has a version '{version}' that is not 1..255 or is the current one");
            report.Require(TokenSealer.IsUsableKey(key), nameof(RetiredTokenKeys), $"key {version} is not base64 of 32 bytes");
        }

        foreach (var provider in ConnectionProviders.All)
        {
            var section = For(provider);

            report.RequireRange(section.DetailsRefreshEvery, TimeSpan.FromHours(1), TimeSpan.FromDays(365),
                $"{provider}:{nameof(ProviderConnectionOptions.DetailsRefreshEvery)}");

            report.Require(!section.IsConfigured || section is SteamConnectionOptions || !string.IsNullOrWhiteSpace(section.ClientSecret),
                $"{provider}:{nameof(ProviderConnectionOptions.ClientSecret)}", "is empty while the client id is set");
        }

        foreach (var repo in GitHub.ContributorRepos)
        {
            report.Require(repo.Count(c => c == '/') == 1 && !repo.StartsWith('/') && !repo.EndsWith('/'),
                $"{nameof(GitHub)}:{nameof(GitHubConnectionOptions.ContributorRepos)}", $"'{repo}' is not owner/name");
        }

        report.Require(string.IsNullOrEmpty(Twitch.EventSubSecret) || Twitch.EventSubSecret.Length is >= 10 and <= 100,
            $"{nameof(Twitch)}:{nameof(TwitchConnectionOptions.EventSubSecret)}", "must be 10 to 100 characters, as Twitch requires");
        report.RequireRange(Twitch.StreamRefreshEvery, TimeSpan.FromMinutes(1), TimeSpan.FromHours(1), $"{nameof(Twitch)}:{nameof(TwitchConnectionOptions.StreamRefreshEvery)}");

        report.Require(!string.IsNullOrWhiteSpace(GitHub.ContributorCoin), $"{nameof(GitHub)}:{nameof(GitHubConnectionOptions.ContributorCoin)}", "cannot be empty");
        report.Require(!string.IsNullOrWhiteSpace(GitHub.ContributorBadge), $"{nameof(GitHub)}:{nameof(GitHubConnectionOptions.ContributorBadge)}", "cannot be empty");
        report.RequireRange(Spotify.RequestsPer30s, 1, 100_000, $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.RequestsPer30s)}");
        report.RequireRange(Spotify.PlayingPollMax, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.PlayingPollMax)}");
        report.RequireRange(Spotify.PausedPoll, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10), $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.PausedPoll)}");
        report.RequireRange(Spotify.IdlePoll, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(10), $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.IdlePoll)}");
        report.RequireRange(Spotify.IdleStopAfter, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6), $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.IdleStopAfter)}");
        report.RequireRange(Spotify.PausedRetractAfter, TimeSpan.FromSeconds(10), TimeSpan.FromHours(1), $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.PausedRetractAfter)}");
        report.RequireRange(Spotify.ListenAlongPoll, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2), $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.ListenAlongPoll)}");
        report.RequireRange(Spotify.MaxListenersPerHost, 1, 1000, $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.MaxListenersPerHost)}");
        report.RequireRange(Spotify.ListenerFailuresBeforeDrop, 1, 100, $"{nameof(Spotify)}:{nameof(SpotifyConnectionOptions.ListenerFailuresBeforeDrop)}");
    }
}

/// <summary>
/// One provider's app registration. A provider with no client id is not offered and is never called.
/// </summary>
public class ProviderConnectionOptions
{
    public string ClientId     { get; set; } = "";
    public string ClientSecret { get; set; } = "";

    /// <summary>How old a connection's details may get before the maintenance tick fetches them again.</summary>
    public TimeSpan DetailsRefreshEvery { get; set; } = TimeSpan.FromDays(7);

    public virtual bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

public sealed class GitHubConnectionOptions : ProviderConnectionOptions
{
    /// <summary>A read-only token for listing contributors; the user's own token cannot see private repositories.</summary>
    public string ServerToken { get; set; } = "";

    /// <summary><c>owner/name</c> per entry. Commits in any of them earn the contributor coin.</summary>
    public List<string> ContributorRepos { get; set; } = new();

    public string ContributorCoin  { get; set; } = "coin_argon_contributor";
    public string ContributorBadge { get; set; } = "contributor";
}

public sealed class SteamConnectionOptions : ProviderConnectionOptions
{
    /// <summary>Steam has no OAuth app; the Web API key is the whole registration.</summary>
    public string WebApiKey { get; set; } = "";

    public override bool IsConfigured => !string.IsNullOrWhiteSpace(WebApiKey);
}

public sealed class TwitchConnectionOptions : ProviderConnectionOptions
{
    /// <summary>
    /// The secret Twitch signs EventSub messages with. Set, the connection can be shown as a
    /// streaming status; empty, Twitch links and shows details only.
    /// </summary>
    public string EventSubSecret { get; set; } = "";

    /// <summary>How often a live stream is read again for its title and game, and to notice a missed offline.</summary>
    public TimeSpan StreamRefreshEvery { get; set; } = TimeSpan.FromMinutes(15);

    public bool StreamingStatusEnabled => IsConfigured && !string.IsNullOrEmpty(EventSubSecret);
}

public sealed class SpotifyConnectionOptions : ProviderConnectionOptions
{
    /// <summary>The application-wide budget the activity poller spends per rolling half minute; Spotify's own limit is per app.</summary>
    public int RequestsPer30s { get; set; } = 150;

    /// <summary>The longest wait between two reads while a track is playing; a track about to end is read sooner.</summary>
    public TimeSpan PlayingPollMax { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The wait between reads while playback is paused.</summary>
    public TimeSpan PausedPoll { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The wait between reads while nothing is playing.</summary>
    public TimeSpan IdlePoll { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>How long nothing may play before the poller stops until a session attaches or the client hints.</summary>
    public TimeSpan IdleStopAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How long a paused track stays on the card before it is retracted.</summary>
    public TimeSpan PausedRetractAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The longest wait between two reads of a host who has listeners.</summary>
    public TimeSpan ListenAlongPoll { get; set; } = TimeSpan.FromSeconds(10);

    public int MaxListenersPerHost { get; set; } = 20;

    /// <summary>How many commands in a row a listener's Spotify may refuse before they are dropped from the party.</summary>
    public int ListenerFailuresBeforeDrop { get; set; } = 3;
}

public static class ConnectionProviders
{
    public static readonly ConnectionProvider[] All =
    [
        ConnectionProvider.GITHUB, ConnectionProvider.STEAM, ConnectionProvider.SPOTIFY, ConnectionProvider.TWITTER,
        ConnectionProvider.TWITCH, ConnectionProvider.YOUTUBE, ConnectionProvider.TELEGRAM
    ];

    public static string Slug(ConnectionProvider provider) => provider switch
    {
        ConnectionProvider.GITHUB   => "github",
        ConnectionProvider.STEAM    => "steam",
        ConnectionProvider.SPOTIFY  => "spotify",
        ConnectionProvider.TWITTER  => "twitter",
        ConnectionProvider.TWITCH   => "twitch",
        ConnectionProvider.YOUTUBE  => "youtube",
        ConnectionProvider.TELEGRAM => "telegram",
        _                           => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    public static string DisplayName(ConnectionProvider provider) => provider switch
    {
        ConnectionProvider.GITHUB   => "GitHub",
        ConnectionProvider.STEAM    => "Steam",
        ConnectionProvider.SPOTIFY  => "Spotify",
        ConnectionProvider.TWITTER  => "X",
        ConnectionProvider.TWITCH   => "Twitch",
        ConnectionProvider.YOUTUBE  => "YouTube",
        ConnectionProvider.TELEGRAM => "Telegram",
        _                           => provider.ToString()
    };

    public static bool TryParse(string? slug, out ConnectionProvider provider)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(Slug(candidate), slug, StringComparison.OrdinalIgnoreCase))
            {
                provider = candidate;
                return true;
            }
        }

        provider = default;
        return false;
    }

    /// <summary>The per-user feature flag that gates a provider: <c>connections.github</c> and so on.</summary>
    public static string FlagOf(ConnectionProvider provider) => $"connections.{Slug(provider)}";

    public static string CallbackUrl(string publicBase, ConnectionProvider provider)
        => $"{publicBase.TrimEnd('/')}/connections/callback/{Slug(provider)}";
}
