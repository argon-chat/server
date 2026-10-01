namespace Argon.Features.Integrations.Connections.Spotify;

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using System.Text.Json;
using Argon.Services;
using ion.runtime;

public enum SpotifyPlaybackState
{
    Playing,

    /// <summary>Nothing playing, a private session, an ad, or a local file: nothing to show.</summary>
    Nothing,

    /// <summary>Not asked: the application budget for this half minute is spent, or a 429 backoff is in force.</summary>
    Skipped,

    RateLimited,

    /// <summary>The token is refused; the connection needs re-authentication.</summary>
    Unauthorized,

    Error
}

/// <summary>What a player command — play, pause, seek — came back with.</summary>
public enum SpotifyCommandResult
{
    Ok,
    NoActiveDevice,
    PremiumRequired,
    Unauthorized,
    RateLimited,
    Error
}

public sealed record SpotifyPlayback(SpotifyPlaybackState State, SpotifyTrack? Track, TimeSpan? RetryAfter)
{
    public static readonly SpotifyPlayback Nothing      = new(SpotifyPlaybackState.Nothing, null, null);
    public static readonly SpotifyPlayback Unauthorized = new(SpotifyPlaybackState.Unauthorized, null, null);
    public static readonly SpotifyPlayback Error        = new(SpotifyPlaybackState.Error, null, null);

    public static SpotifyPlayback Playing(SpotifyTrack track)      => new(SpotifyPlaybackState.Playing, track, null);
    public static SpotifyPlayback Skipped(TimeSpan retryAfter)     => new(SpotifyPlaybackState.Skipped, null, retryAfter);
    public static SpotifyPlayback RateLimited(TimeSpan retryAfter) => new(SpotifyPlaybackState.RateLimited, null, retryAfter);
}

/// <summary>
/// One Spotify call — what is playing now — behind the application-wide budget.
/// </summary>
/// <remarks>
/// <para>Spotify's rate limit is per application over a rolling thirty-second window and is not
/// published, so every read first takes one unit from a counter keyed by the half minute
/// (<c>conn:spotify:budget:{window}</c>); a counter past <see cref="SpotifyConnectionOptions.RequestsPer30s"/>
/// answers <see cref="SpotifyPlaybackState.Skipped"/> and the caller tries again later. A 429 writes
/// a backoff until <c>Retry-After</c> has passed (<c>conn:spotify:backoff</c>) that every poller on
/// every silo honours, so one bad minute slows everyone down instead of stacking more 429s.</para>
///
/// <para>Discord avoids polling by riding Spotify's undocumented dealer WebSocket; that is not
/// offered to third parties and is not used here.</para>
/// </remarks>
[ExcludeFromCodeCoverage(Justification = "Vendor HTTP adapter; the parser and the budget are exercised in the fast suite.")]
public sealed class SpotifyPlayerClient(HttpClient http, IArgonCacheDatabase cache, IOptions<ConnectionsOptions> options)
{
    public const string Endpoint = "https://api.spotify.com/v1/me/player/currently-playing?additional_types=track,episode";

    private const string BudgetKeyPrefix = "conn:spotify:budget:";
    private const string BackoffKey      = "conn:spotify:backoff";

    private static readonly TimeSpan DefaultRetry = TimeSpan.FromSeconds(30);

    public async Task<SpotifyPlayback> GetCurrentlyPlayingAsync(ProviderToken token, DateTimeOffset now, CancellationToken ct)
    {
        if (await BackoffRemainingAsync(now, ct) is { } wait)
            return SpotifyPlayback.Skipped(wait);

        if (!await TryAcquireAsync(now, ct))
            return SpotifyPlayback.Skipped(DefaultRetry);

        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, ct);

        switch (response.StatusCode)
        {
            case HttpStatusCode.NoContent:
                return SpotifyPlayback.Nothing;
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return SpotifyPlayback.Unauthorized;
            case HttpStatusCode.TooManyRequests:
            {
                var retry = response.Headers.RetryAfter?.Delta ?? DefaultRetry;

                if (retry < TimeSpan.FromSeconds(1))
                    retry = TimeSpan.FromSeconds(1);

                await cache.StringSetAsync(BackoffKey, (now + retry).ToUnixTimeSeconds().ToString(), retry, ct);
                return SpotifyPlayback.RateLimited(retry);
            }
        }

        if (!response.IsSuccessStatusCode)
            return SpotifyPlayback.Error;

        var body = await response.Content.ReadAsStringAsync(ct);

        if (string.IsNullOrWhiteSpace(body))
            return SpotifyPlayback.Nothing;

        try
        {
            using var document = JsonDocument.Parse(body);

            return ParseCurrentlyPlaying(document.RootElement, now) is { } track
                ? SpotifyPlayback.Playing(track)
                : SpotifyPlayback.Nothing;
        }
        catch (JsonException)
        {
            return SpotifyPlayback.Error;
        }
    }

    /// <summary>
    /// The track or episode in a <c>currently-playing</c> answer, or null for anything that is not
    /// one: an ad, a local file (no id), an unknown item type.
    /// </summary>
    public static SpotifyTrack? ParseCurrentlyPlaying(JsonElement root, DateTimeOffset observedAt)
    {
        var type = root.String("currently_playing_type");

        if (type is not ("track" or "episode"))
            return null;

        if (root.Object("item") is not { } item)
            return null;

        var id = item.String("id");

        if (string.IsNullOrEmpty(id) || item.Boolean("is_local") == true)
            return null;

        var title     = item.String("name") ?? "";
        var isPlaying = root.Boolean("is_playing") ?? false;
        var progress  = (int)Math.Clamp(root.Number("progress_ms") ?? 0, 0, int.MaxValue);
        var duration  = (int)Math.Clamp(item.Number("duration_ms") ?? 0, 0, int.MaxValue);

        List<string> artists;
        string       album;
        string?      art;

        if (type == "track")
        {
            artists = item.TryGetProperty("artists", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(a => a.String("name")).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList()
                : [];

            var albumElement = item.Object("album");

            album = albumElement?.String("name") ?? "";
            art   = albumElement is { } a ? PickArt(a) : null;
        }
        else
        {
            var show = item.Object("show")?.String("name");

            artists = show is null ? [] : [show];
            album   = show ?? "";
            art     = PickArt(item);
        }

        return new SpotifyTrack(
            id,
            title,
            new IonArray<string>(artists),
            album,
            art,
            duration,
            progress,
            observedAt,
            isPlaying,
            root.Object("context")?.String("uri"),
            item.Object("external_urls")?.String("spotify") ?? $"https://open.spotify.com/{type}/{id}",
            listenAlongOpen: false);
    }

    /// <summary>The 300px image when there is one — the card's size — else the first, which Spotify lists largest first.</summary>
    private static string? PickArt(JsonElement owner)
    {
        if (!owner.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;

        string? first = null;

        foreach (var image in images.EnumerateArray())
        {
            var url = image.String("url");

            if (url is null)
                continue;

            first ??= url;

            if (image.Number("width") is { } width && width is >= 200 and <= 400)
                return url;
        }

        return first;
    }

    // ── commands, for listen-along ──────────────────────────────────────────────────────────

    /// <summary>Starts the item on the listener's active device at <paramref name="positionMs"/>.</summary>
    public Task<SpotifyCommandResult> PlayAsync(ProviderToken token, SpotifyTrack track, int positionMs, DateTimeOffset now, CancellationToken ct)
        => CommandAsync(token, HttpMethod.Put, $"{PlayerApi}/play", now, ct,
            JsonSerializer.Serialize(new { uris = new[] { ItemUri(track) }, position_ms = Math.Max(0, positionMs) }));

    public Task<SpotifyCommandResult> PauseAsync(ProviderToken token, DateTimeOffset now, CancellationToken ct)
        => CommandAsync(token, HttpMethod.Put, $"{PlayerApi}/pause", now, ct, null);

    public Task<SpotifyCommandResult> ResumeAsync(ProviderToken token, int positionMs, DateTimeOffset now, CancellationToken ct)
        => CommandAsync(token, HttpMethod.Put, $"{PlayerApi}/play", now, ct,
            JsonSerializer.Serialize(new { position_ms = Math.Max(0, positionMs) }));

    public Task<SpotifyCommandResult> SeekAsync(ProviderToken token, int positionMs, DateTimeOffset now, CancellationToken ct)
        => CommandAsync(token, HttpMethod.Put, $"{PlayerApi}/seek?position_ms={Math.Max(0, positionMs)}", now, ct, null);

    private const string PlayerApi = "https://api.spotify.com/v1/me/player";

    /// <summary>The Spotify URI the play command takes; episodes link under /episode/.</summary>
    public static string ItemUri(SpotifyTrack track)
        => track.url?.Contains("/episode/", StringComparison.Ordinal) == true
            ? $"spotify:episode:{track.trackId}"
            : $"spotify:track:{track.trackId}";

    private async Task<SpotifyCommandResult> CommandAsync(ProviderToken token, HttpMethod method, string url, DateTimeOffset now,
        CancellationToken ct, string? jsonBody)
    {
        if (await BackoffRemainingAsync(now, ct) is not null || !await TryAcquireAsync(now, ct))
            return SpotifyCommandResult.RateLimited;

        using var request = new HttpRequestMessage(method, url);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);

        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
            return SpotifyCommandResult.Ok;

        var body = await response.Content.ReadAsStringAsync(ct);

        return ClassifyCommandFailure(response.StatusCode, body);
    }

    /// <summary>
    /// Spotify's player errors carry a <c>reason</c>: <c>NO_ACTIVE_DEVICE</c> on a 404 and
    /// <c>PREMIUM_REQUIRED</c> on a 403 are the two a listener can do something about.
    /// </summary>
    public static SpotifyCommandResult ClassifyCommandFailure(HttpStatusCode status, string? body)
    {
        var reason = "";

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                reason = document.RootElement.Object("error")?.String("reason") ?? "";
            }
            catch (JsonException)
            {
                // Not JSON: the status alone decides.
            }
        }

        return status switch
        {
            HttpStatusCode.NotFound                                                   => SpotifyCommandResult.NoActiveDevice,
            HttpStatusCode.Forbidden when reason == "PREMIUM_REQUIRED"                => SpotifyCommandResult.PremiumRequired,
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized                   => SpotifyCommandResult.Unauthorized,
            HttpStatusCode.TooManyRequests                                            => SpotifyCommandResult.RateLimited,
            _                                                                         => SpotifyCommandResult.Error
        };
    }

    private async Task<bool> TryAcquireAsync(DateTimeOffset now, CancellationToken ct)
    {
        var window = now.ToUnixTimeSeconds() / 30;
        var key    = BudgetKeyPrefix + window;
        var count  = await cache.StringIncrementAsync(key, ct);

        if (count == 1)
            await cache.UpdateStringExpirationAsync(key, TimeSpan.FromSeconds(90), ct);

        return count <= options.Value.Spotify.RequestsPer30s;
    }

    private async Task<TimeSpan?> BackoffRemainingAsync(DateTimeOffset now, CancellationToken ct)
    {
        var stored = await cache.StringGetAsync(BackoffKey, ct);

        if (!long.TryParse(stored, out var until))
            return null;

        var remaining = until - now.ToUnixTimeSeconds();

        return remaining > 0 ? TimeSpan.FromSeconds(remaining) : null;
    }
}

/// <summary>When to read Spotify again, from what the last read said.</summary>
public static class SpotifyPollSchedule
{
    public static readonly TimeSpan MinPoll = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Playing: just after the track ends, capped at <see cref="SpotifyConnectionOptions.PlayingPollMax"/>
    /// and floored at <see cref="MinPoll"/>. Paused: <see cref="SpotifyConnectionOptions.PausedPoll"/>.
    /// </summary>
    public static TimeSpan AfterPlayback(SpotifyTrack track, DateTimeOffset now, SpotifyConnectionOptions options)
    {
        if (!track.isPlaying)
            return options.PausedPoll;

        var remaining = TimeSpan.FromMilliseconds(track.durationMs - track.progressMs) - (now - track.observedAt);
        var next      = remaining + TimeSpan.FromSeconds(2);

        if (next > options.PlayingPollMax)
            next = options.PlayingPollMax;

        return next < MinPoll ? MinPoll : next;
    }

    public static TimeSpan AfterThrottle(TimeSpan? retryAfter)
        => retryAfter is { } wait && wait > TimeSpan.FromSeconds(30) ? wait : TimeSpan.FromSeconds(30);
}
