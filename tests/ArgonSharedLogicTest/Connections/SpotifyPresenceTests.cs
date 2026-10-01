namespace ArgonSharedLogicTest.Connections;

using System.Net;
using System.Text.Json;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Spotify;
using Argon.Features.Logic;
using Argon.Grains;
using Argon.Services;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using static ConnectionsTestOptions;
using ActivitySource = ArgonContracts.ActivitySource;

/// <summary>
/// The Spotify activity: what a <c>currently-playing</c> answer becomes, when it is read again, how
/// the application budget holds, and how a provider slot sits beside the session activities.
/// </summary>
[TestFixture]
public class SpotifyPresenceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private const string TrackJson = """
        {"timestamp":1700000000000,"progress_ms":61000,"is_playing":true,"currently_playing_type":"track",
         "context":{"uri":"spotify:playlist:37i9dQZF1DXcBWIGoYBM5M","type":"playlist"},
         "item":{"id":"11dFghVXANMlKmJXsNCbNl","name":"Cut To The Feeling","duration_ms":207959,"is_local":false,
                 "artists":[{"name":"Carly Rae Jepsen"},{"name":"Someone"}],
                 "album":{"name":"Cut To The Feeling","images":[{"url":"https://i.scdn.co/640","width":640,"height":640},{"url":"https://i.scdn.co/300","width":300,"height":300},{"url":"https://i.scdn.co/64","width":64,"height":64}]},
                 "external_urls":{"spotify":"https://open.spotify.com/track/11dFghVXANMlKmJXsNCbNl"}}}
        """;

    private const string EpisodeJson = """
        {"progress_ms":5000,"is_playing":false,"currently_playing_type":"episode",
         "item":{"id":"ep1","name":"Episode 12","duration_ms":3600000,"images":[{"url":"https://i.scdn.co/ep","width":300,"height":300}],
                 "show":{"name":"Some Podcast"},"external_urls":{"spotify":"https://open.spotify.com/episode/ep1"}}}
        """;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static SpotifyTrack Track(bool isPlaying = true, int progressMs = 61000, int durationMs = 207959, DateTimeOffset? observedAt = null, bool listenAlong = false)
        => new("t1", "Title", new IonArray<string>(["Artist"]), "Album", null, durationMs, progressMs, observedAt ?? Now, isPlaying,
            null, "https://open.spotify.com/track/t1", listenAlong);

    private static SpotifyPlayerClient Client(FakeHttpHandler handler, IArgonCacheDatabase cache, int budget = 150)
    {
        var options = Default();
        options.Spotify.RequestsPer30s = budget;
        return new SpotifyPlayerClient(handler.Client(), cache, Options.Create(options));
    }

    private static InMemoryArgonCacheDatabase Cache()
        => new(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));

    // ── parsing ──────────────────────────────────────────────────────────────────────────────

    [Test]
    public void A_track_becomes_a_SpotifyTrack_with_the_card_sized_art()
    {
        var track = SpotifyPlayerClient.ParseCurrentlyPlaying(Json(TrackJson), Now);

        Assert.That(track, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(track!.trackId, Is.EqualTo("11dFghVXANMlKmJXsNCbNl"));
            Assert.That(track.title, Is.EqualTo("Cut To The Feeling"));
            Assert.That(track.artists.Values, Is.EqualTo(new[] { "Carly Rae Jepsen", "Someone" }));
            Assert.That(track.album, Is.EqualTo("Cut To The Feeling"));
            Assert.That(track.albumArtUrl, Is.EqualTo("https://i.scdn.co/300"), "the 300px image, not the largest");
            Assert.That(track.durationMs, Is.EqualTo(207959));
            Assert.That(track.progressMs, Is.EqualTo(61000));
            Assert.That(track.isPlaying, Is.True);
            Assert.That(track.observedAt, Is.EqualTo(Now));
            Assert.That(track.contextUri, Is.EqualTo("spotify:playlist:37i9dQZF1DXcBWIGoYBM5M"));
            Assert.That(track.url, Is.EqualTo("https://open.spotify.com/track/11dFghVXANMlKmJXsNCbNl"));
            Assert.That(track.listenAlongOpen, Is.False);
        });
    }

    [Test]
    public void An_episode_is_a_track_whose_artist_is_the_show()
    {
        var episode = SpotifyPlayerClient.ParseCurrentlyPlaying(Json(EpisodeJson), Now);

        Assert.That(episode, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(episode!.trackId, Is.EqualTo("ep1"));
            Assert.That(episode.artists.Values, Is.EqualTo(new[] { "Some Podcast" }));
            Assert.That(episode.album, Is.EqualTo("Some Podcast"));
            Assert.That(episode.albumArtUrl, Is.EqualTo("https://i.scdn.co/ep"));
            Assert.That(episode.isPlaying, Is.False);
        });
    }

    [Test]
    public void Ads_local_files_and_unknown_items_are_nothing()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpotifyPlayerClient.ParseCurrentlyPlaying(Json("""{"currently_playing_type":"ad","is_playing":true,"item":null}"""), Now), Is.Null);
            Assert.That(SpotifyPlayerClient.ParseCurrentlyPlaying(Json("""{"currently_playing_type":"track","item":{"id":null,"is_local":true,"name":"x"}}"""), Now), Is.Null);
            Assert.That(SpotifyPlayerClient.ParseCurrentlyPlaying(Json("""{"currently_playing_type":"unknown","item":{"id":"x"}}"""), Now), Is.Null);
            Assert.That(SpotifyPlayerClient.ParseCurrentlyPlaying(Json("{}"), Now), Is.Null);
        });
    }

    // ── the client: budget and answers ───────────────────────────────────────────────────────

    [Test]
    public async Task The_answers_map_to_states_and_a_429_arms_a_shared_backoff()
    {
        var cache = Cache();
        var token = new ProviderToken("at", "rt", null, "");

        var ok = await Client(new FakeHttpHandler().On(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing", HttpStatusCode.OK, TrackJson), cache)
           .GetCurrentlyPlayingAsync(token, Now, CancellationToken.None);

        var silent = await Client(new FakeHttpHandler().On(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing", HttpStatusCode.NoContent, ""), cache)
           .GetCurrentlyPlayingAsync(token, Now, CancellationToken.None);

        var refused = await Client(new FakeHttpHandler().On(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing", HttpStatusCode.Unauthorized, "{}"), cache)
           .GetCurrentlyPlayingAsync(token, Now, CancellationToken.None);

        var throttled = await Client(new FakeHttpHandler().On(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing", _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
                return response;
            }), cache)
           .GetCurrentlyPlayingAsync(token, Now, CancellationToken.None);

        // Another poller, another silo: the backoff is in the shared cache, so it is not asked at all.
        var afterwards = new FakeHttpHandler().On(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing", HttpStatusCode.OK, TrackJson);
        var skipped    = await Client(afterwards, cache).GetCurrentlyPlayingAsync(token, Now.AddSeconds(10), CancellationToken.None);
        var later      = await Client(afterwards, cache).GetCurrentlyPlayingAsync(token, Now.AddSeconds(50), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(ok.State, Is.EqualTo(SpotifyPlaybackState.Playing));
            Assert.That(ok.Track?.trackId, Is.EqualTo("11dFghVXANMlKmJXsNCbNl"));
            Assert.That(silent.State, Is.EqualTo(SpotifyPlaybackState.Nothing));
            Assert.That(refused.State, Is.EqualTo(SpotifyPlaybackState.Unauthorized));
            Assert.That(throttled.State, Is.EqualTo(SpotifyPlaybackState.RateLimited));
            Assert.That(throttled.RetryAfter, Is.EqualTo(TimeSpan.FromSeconds(45)));
            Assert.That(skipped.State, Is.EqualTo(SpotifyPlaybackState.Skipped));
            Assert.That(afterwards.Calls, Has.Count.EqualTo(1), "the request inside the backoff never left");
            Assert.That(later.State, Is.EqualTo(SpotifyPlaybackState.Playing));
        });
    }

    [Test]
    public async Task The_budget_is_per_half_minute_across_every_poller()
    {
        var cache   = Cache();
        var handler = new FakeHttpHandler().On(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing", HttpStatusCode.NoContent, "");
        var token   = new ProviderToken("at", null, null, "");
        var at      = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000); // a fresh window, shared by nothing else in this fixture

        var first  = await Client(handler, cache, budget: 2).GetCurrentlyPlayingAsync(token, at, CancellationToken.None);
        var second = await Client(handler, cache, budget: 2).GetCurrentlyPlayingAsync(token, at.AddSeconds(5), CancellationToken.None);
        var third  = await Client(handler, cache, budget: 2).GetCurrentlyPlayingAsync(token, at.AddSeconds(10), CancellationToken.None);
        var next   = await Client(handler, cache, budget: 2).GetCurrentlyPlayingAsync(token, at.AddSeconds(31), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.State, Is.EqualTo(SpotifyPlaybackState.Nothing));
            Assert.That(second.State, Is.EqualTo(SpotifyPlaybackState.Nothing));
            Assert.That(third.State, Is.EqualTo(SpotifyPlaybackState.Skipped));
            Assert.That(next.State, Is.EqualTo(SpotifyPlaybackState.Nothing), "the next half minute has its own budget");
            Assert.That(handler.Calls, Has.Count.EqualTo(3));
        });
    }

    // ── the clock ────────────────────────────────────────────────────────────────────────────

    [Test]
    public void The_next_read_lands_just_after_the_track_ends_within_the_cap()
    {
        var options = new SpotifyConnectionOptions();

        var endingSoon = Track(progressMs: 200_000, durationMs: 210_000);                 // 10 s left
        var longWay    = Track(progressMs: 10_000, durationMs: 300_000);                  // 290 s left
        var overdue    = Track(progressMs: 209_000, durationMs: 210_000, observedAt: Now.AddSeconds(-30));
        var paused     = Track(isPlaying: false);

        Assert.Multiple(() =>
        {
            Assert.That(SpotifyPollSchedule.AfterPlayback(endingSoon, Now, options), Is.EqualTo(TimeSpan.FromSeconds(12)));
            Assert.That(SpotifyPollSchedule.AfterPlayback(longWay, Now, options), Is.EqualTo(options.PlayingPollMax));
            Assert.That(SpotifyPollSchedule.AfterPlayback(overdue, Now, options), Is.EqualTo(SpotifyPollSchedule.MinPoll));
            Assert.That(SpotifyPollSchedule.AfterPlayback(paused, Now, options), Is.EqualTo(options.PausedPoll));
            Assert.That(SpotifyPollSchedule.AfterThrottle(null), Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(SpotifyPollSchedule.AfterThrottle(TimeSpan.FromSeconds(5)), Is.EqualTo(TimeSpan.FromSeconds(30)));
            Assert.That(SpotifyPollSchedule.AfterThrottle(TimeSpan.FromSeconds(90)), Is.EqualTo(TimeSpan.FromSeconds(90)));
        });
    }

    // ── the activity ─────────────────────────────────────────────────────────────────────────

    [Test]
    public void The_presence_carries_the_track_and_both_ends_of_the_bar()
    {
        var track    = Track(progressMs: 61_000, durationMs: 207_959);
        var presence = SpotifyPresenceGrain.ToPresence(track);

        Assert.Multiple(() =>
        {
            Assert.That(presence.kind, Is.EqualTo(ActivityPresenceKind.LISTEN));
            Assert.That(presence.source, Is.EqualTo(ActivitySource.SPOTIFY));
            Assert.That(presence.titleName, Is.EqualTo("Title - Artist"));
            Assert.That(presence.startTimestampSeconds, Is.EqualTo((ulong)(Now.ToUnixTimeSeconds() - 61)));
            Assert.That(presence.endTimestampSeconds, Is.EqualTo((ulong)(Now.ToUnixTimeSeconds() - 61 + 207)));
            Assert.That(presence.spotify, Is.EqualTo(track));
        });
    }

    [Test]
    public void Only_a_new_track_a_pause_a_seek_or_the_listen_along_flag_is_a_change()
    {
        var before   = Track(progressMs: 10_000, observedAt: Now);
        var drifted  = Track(progressMs: 40_500, observedAt: Now.AddSeconds(30));   // 500 ms off the expected 40 000
        var seeked   = Track(progressMs: 120_000, observedAt: Now.AddSeconds(30));
        var paused   = Track(isPlaying: false, progressMs: 40_000, observedAt: Now.AddSeconds(30));
        var another  = Track(progressMs: 40_000, observedAt: Now.AddSeconds(30)) with { trackId = "t2" };
        var openable = Track(progressMs: 40_000, observedAt: Now.AddSeconds(30), listenAlong: true);

        Assert.Multiple(() =>
        {
            Assert.That(SpotifyPresenceGrain.Changed(before, drifted), Is.False, "the clock running is not a change");
            Assert.That(SpotifyPresenceGrain.Changed(before, seeked), Is.True);
            Assert.That(SpotifyPresenceGrain.Changed(before, paused), Is.True);
            Assert.That(SpotifyPresenceGrain.Changed(before, another), Is.True);
            Assert.That(SpotifyPresenceGrain.Changed(before, openable), Is.True);
        });
    }

    // ── the provider slot in the presence service ────────────────────────────────────────────

    [Test]
    public async Task A_provider_activity_shows_only_while_the_user_has_a_live_session_and_outranks_a_bare_listen_of_the_same_kind()
    {
        var service = new UserPresenceService(Cache(), Options.Create(new PresenceTimingOptions()));
        var userId  = Guid.NewGuid();
        var spotify = SpotifyPresenceGrain.ToPresence(Track(progressMs: 61_000));

        await service.SetProviderActivity(userId, "spotify", spotify);

        Assert.That(await service.GetUsersActivityPresence(userId), Is.Null, "nobody online, nothing shown");

        await service.SetSessionOnlineAsync(userId, "desktop");

        // Compared field by field: the activity comes back through JSON, and IonArray is not a value type.
        var shown = await service.GetUsersActivityPresence(userId);

        Assert.Multiple(() =>
        {
            Assert.That(shown?.source, Is.EqualTo(ActivitySource.SPOTIFY));
            Assert.That(shown?.spotify?.trackId, Is.EqualTo("t1"));
            Assert.That(shown?.spotify?.artists.Values, Is.EqualTo(new[] { "Artist" }));
            Assert.That(shown?.endTimestampSeconds, Is.EqualTo(spotify.endTimestampSeconds));
        });

        // The desktop's own media-session LISTEN, started later, for the same track: the Spotify one still wins.
        var bare = new UserActivityPresence(ActivityPresenceKind.LISTEN, (ulong)Now.ToUnixTimeSeconds() + 100, "Title - Artist", null, null, null, null);
        await service.BroadcastActivityPresence(bare, userId, "desktop");

        Assert.That((await service.GetUsersActivityPresence(userId))?.source, Is.EqualTo(ActivitySource.SPOTIFY));

        // A game started after both is a different kind and the newest start, so it is what the single-activity wire shows.
        var game = new UserActivityPresence(ActivityPresenceKind.GAME, (ulong)Now.ToUnixTimeSeconds() + 200, "Portal 2", null, null, null, null);
        await service.BroadcastActivityPresence(game, userId, "desktop");

        Assert.That((await service.GetUsersActivityPresence(userId))?.titleName, Is.EqualTo("Portal 2"));

        Assert.Multiple(async () =>
        {
            Assert.That(await service.RemoveProviderActivity(userId, "spotify"), Is.True);
            Assert.That(await service.RemoveProviderActivity(userId, "spotify"), Is.False);
            Assert.That((await service.GetUserActivitiesAsync(userId)).Select(a => a.kind), Is.EquivalentTo(new[] { ActivityPresenceKind.GAME }));
        });
    }

    [Test]
    public void The_representative_prefers_a_provider_backed_activity_per_kind_then_the_newest_start()
    {
        var bareListen = new UserActivityPresence(ActivityPresenceKind.LISTEN, 300, "x", null, null, null, null);
        var spotify    = new UserActivityPresence(ActivityPresenceKind.LISTEN, 100, "y", ActivitySource.SPOTIFY, null, null, null);
        var client     = new UserActivityPresence(ActivityPresenceKind.LISTEN, 250, "z", ActivitySource.CLIENT, null, null, null);
        var game       = new UserActivityPresence(ActivityPresenceKind.GAME, 200, "g", null, null, null, null);

        Assert.Multiple(() =>
        {
            Assert.That(UserPresenceService.PickRepresentativeActivity([]), Is.Null);
            Assert.That(UserPresenceService.PickRepresentativeActivity([bareListen, spotify, client]), Is.EqualTo(spotify), "CLIENT is not provider-backed");
            Assert.That(UserPresenceService.PickRepresentativeActivity([bareListen, spotify, game]), Is.EqualTo(game), "across kinds the newest start wins");
            Assert.That(UserPresenceService.PickRepresentativeActivity([bareListen, client]), Is.EqualTo(bareListen));
        });
    }
}
