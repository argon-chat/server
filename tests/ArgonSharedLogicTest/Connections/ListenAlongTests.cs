namespace ArgonSharedLogicTest.Connections;

using System.Net;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Spotify;
using Argon.Grains;
using Argon.Services;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using static ConnectionsTestOptions;

/// <summary>
/// Listen along, the pure parts: where the host is now, which Spotify refusal means what, how a
/// play command is addressed, and the listener-to-host pointer.
/// </summary>
[TestFixture]
public class ListenAlongTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    private static SpotifyTrack Track(bool isPlaying, int progressMs, int durationMs, DateTimeOffset observedAt, string? url = null)
        => new("t1", "Title", new IonArray<string>(["Artist"]), "Album", null, durationMs, progressMs, observedAt, isPlaying, null,
            url ?? "https://open.spotify.com/track/t1", true);

    [Test]
    public void The_host_position_runs_while_playing_and_stands_still_while_paused()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ListenAlongGrain.PositionNow(Track(true, 10_000, 200_000, Now.AddSeconds(-30)), Now), Is.EqualTo(40_000));
            Assert.That(ListenAlongGrain.PositionNow(Track(false, 10_000, 200_000, Now.AddSeconds(-30)), Now), Is.EqualTo(10_000));
            Assert.That(ListenAlongGrain.PositionNow(Track(true, 195_000, 200_000, Now.AddSeconds(-30)), Now), Is.EqualTo(200_000), "clamped to the track");
        });
    }

    [Test]
    public void A_play_command_names_tracks_and_episodes_by_their_own_uri()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpotifyPlayerClient.ItemUri(Track(true, 0, 1, Now)), Is.EqualTo("spotify:track:t1"));
            Assert.That(SpotifyPlayerClient.ItemUri(Track(true, 0, 1, Now, "https://open.spotify.com/episode/t1")), Is.EqualTo("spotify:episode:t1"));
        });
    }

    [Test]
    public void Spotify_refusals_are_told_apart_by_status_and_reason()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SpotifyPlayerClient.ClassifyCommandFailure(HttpStatusCode.NotFound, """{"error":{"status":404,"reason":"NO_ACTIVE_DEVICE"}}"""), Is.EqualTo(SpotifyCommandResult.NoActiveDevice));
            Assert.That(SpotifyPlayerClient.ClassifyCommandFailure(HttpStatusCode.Forbidden, """{"error":{"status":403,"reason":"PREMIUM_REQUIRED"}}"""), Is.EqualTo(SpotifyCommandResult.PremiumRequired));
            Assert.That(SpotifyPlayerClient.ClassifyCommandFailure(HttpStatusCode.Forbidden, """{"error":{"status":403,"message":"Insufficient client scope"}}"""), Is.EqualTo(SpotifyCommandResult.Unauthorized));
            Assert.That(SpotifyPlayerClient.ClassifyCommandFailure(HttpStatusCode.Unauthorized, ""), Is.EqualTo(SpotifyCommandResult.Unauthorized));
            Assert.That(SpotifyPlayerClient.ClassifyCommandFailure(HttpStatusCode.TooManyRequests, "not json"), Is.EqualTo(SpotifyCommandResult.RateLimited));
            Assert.That(SpotifyPlayerClient.ClassifyCommandFailure(HttpStatusCode.BadGateway, null), Is.EqualTo(SpotifyCommandResult.Error));
        });
    }

    [Test]
    public async Task A_play_command_carries_the_uri_and_the_position_and_spends_the_budget()
    {
        var cache   = new InMemoryArgonCacheDatabase(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())));
        var handler = new FakeHttpHandler().On(HttpMethod.Put, "https://api.spotify.com/v1/me/player/play", HttpStatusCode.NoContent, "");
        var options = Default();

        options.Spotify.RequestsPer30s = 1;

        var client = new SpotifyPlayerClient(handler.Client(), cache, Options.Create(options));
        var token  = new ProviderToken("listener-token", null, null, "user-modify-playback-state");
        var at     = DateTimeOffset.FromUnixTimeSeconds(1_900_000_000); // a half-minute window nothing else in this fixture uses

        var first  = await client.PlayAsync(token, Track(true, 40_000, 200_000, at), 40_500, at, CancellationToken.None);
        var second = await client.PauseAsync(token, at.AddSeconds(1), CancellationToken.None);

        var (request, body) = handler.CallTo("https://api.spotify.com/v1/me/player/play");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(SpotifyCommandResult.Ok));
            Assert.That(request.Headers.Authorization?.Parameter, Is.EqualTo("listener-token"));
            Assert.That(body, Does.Contain("\"uris\":[\"spotify:track:t1\"]"));
            Assert.That(body, Does.Contain("\"position_ms\":40500"));
            Assert.That(second, Is.EqualTo(SpotifyCommandResult.RateLimited), "the one unit of budget was spent on the play");
            Assert.That(handler.Calls, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task The_directory_points_a_listener_at_one_host_until_cleared()
    {
        var directory = new ListenAlongDirectory(new InMemoryArgonCacheDatabase(new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()))));
        var listener  = Guid.NewGuid();
        var host      = Guid.NewGuid();
        var other     = Guid.NewGuid();

        Assert.That(await directory.GetHostAsync(listener), Is.Null);

        await directory.SetHostAsync(listener, host);
        Assert.That(await directory.GetHostAsync(listener), Is.EqualTo(host));

        await directory.SetHostAsync(listener, other);
        Assert.That(await directory.GetHostAsync(listener), Is.EqualTo(other), "joining another host moves the pointer");

        await directory.ClearAsync(listener);
        Assert.That(await directory.GetHostAsync(listener), Is.Null);
    }
}
