namespace Argon.Features.Integrations.Connections;

/// <summary>
/// The keys a <c>ConnectionDetail</c> may carry. Stable: the client translates them, so a rename is
/// a wire change in all but name.
/// </summary>
public static class ConnectionDetailKeys
{
    /// <summary>When the account was created, ISO-8601 date. Every provider that says so.</summary>
    public const string Since = "since";

    public const string GitHubPublicRepos = "github.public_repos";
    public const string GitHubFollowers   = "github.followers";

    public const string SteamGames = "steam.games";
    public const string SteamLevel = "steam.level";

    public const string SpotifyPremium   = "spotify.premium";
    public const string SpotifyFollowers = "spotify.followers";

    public const string TwitterFollowers = "twitter.followers";
    public const string TwitterVerified  = "twitter.verified";

    public const string TwitchBroadcasterType = "twitch.broadcaster_type";

    public const string YouTubeSubscribers = "youtube.subscribers";
    public const string YouTubeVideos      = "youtube.videos";

    public static ConnectionDetail Number(string key, long value) => new(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), ConnectionDetailKind.NUMBER);
    public static ConnectionDetail Text(string key, string value)  => new(key, value, ConnectionDetailKind.TEXT);
    public static ConnectionDetail Flag(string key, bool value)    => new(key, value ? "true" : "false", ConnectionDetailKind.FLAG);
    public static ConnectionDetail Url(string key, string value)   => new(key, value, ConnectionDetailKind.URL);

    public static ConnectionDetail Date(string key, DateTimeOffset value)
        => new(key, value.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ConnectionDetailKind.DATE);
}
