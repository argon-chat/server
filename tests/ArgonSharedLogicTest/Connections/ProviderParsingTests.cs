namespace ArgonSharedLogicTest.Connections;

using System.Text.Json;
using Argon.Features.Integrations.Connections;
using Argon.Features.Integrations.Connections.Providers;
using ArgonContracts;
using static Argon.Features.Integrations.Connections.ConnectionDetailKeys;

/// <summary>
/// Each provider's identity and details, read off recorded answers. The transport is the vendor's;
/// what is ours is which field becomes the id, which the handle, and which facts are shown.
/// </summary>
[TestFixture]
public class ProviderParsingTests
{
    internal const string GitHubUser = """
        {"login":"octocat","id":583231,"avatar_url":"https://avatars.githubusercontent.com/u/583231?v=4",
         "html_url":"https://github.com/octocat","public_repos":8,"followers":9000,"created_at":"2011-01-25T18:44:36Z"}
        """;

    internal const string SpotifyMe = """
        {"id":"wizzler","display_name":"JM Wizzler","external_urls":{"spotify":"https://open.spotify.com/user/wizzler"},
         "followers":{"total":3829},"images":[{"url":"https://i.scdn.co/image/abc","height":64,"width":64}],"product":"premium"}
        """;

    internal const string TwitterMe = """
        {"data":{"id":"2244994945","name":"X Dev","username":"XDevelopers","created_at":"2013-12-14T04:35:55.000Z","verified":true,
         "profile_image_url":"https://pbs.twimg.com/x.jpg","public_metrics":{"followers_count":600000,"following_count":10}}}
        """;

    internal const string TwitchUsers = """
        {"data":[{"id":"141981764","login":"twitchdev","display_name":"TwitchDev","type":"","broadcaster_type":"partner",
         "profile_image_url":"https://static-cdn.jtvnw.net/x.png","created_at":"2016-12-14T20:32:28Z"}]}
        """;

    internal const string YouTubeChannels = """
        {"items":[{"id":"UC_x5XG1OV2P6uZZ5FSM9Ttw","snippet":{"title":"Google Developers","customUrl":"@googledevelopers",
         "publishedAt":"2007-08-23T00:34:43Z","thumbnails":{"default":{"url":"https://yt3.ggpht.com/x"}}},
         "statistics":{"viewCount":"1","subscriberCount":"2350000","hiddenSubscriberCount":false,"videoCount":"5764"}}]}
        """;

    internal const string SteamPlayer = """
        {"steamid":"76561197960435530","personaname":"Robin","profileurl":"https://steamcommunity.com/id/robinwalker/",
         "avatarfull":"https://avatars.steamstatic.com/x_full.jpg","timecreated":1063407589,"communityvisibilitystate":3}
        """;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? Detail(ProviderSnapshot snapshot, string key) => snapshot.Details.FirstOrDefault(d => d.key == key)?.value;

    [Test]
    public void GitHub_is_the_numeric_id_the_login_and_three_facts()
    {
        var snapshot = GitHubConnectionProvider.Parse(Json(GitHubUser));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Identity, Is.EqualTo(new ConnectionIdentity("583231", "octocat", "https://github.com/octocat", "https://avatars.githubusercontent.com/u/583231?v=4")));
            Assert.That(Detail(snapshot, GitHubPublicRepos), Is.EqualTo("8"));
            Assert.That(Detail(snapshot, GitHubFollowers), Is.EqualTo("9000"));
            Assert.That(Detail(snapshot, Since), Is.EqualTo("2011-01-25"));
            Assert.That(snapshot.Details.Single(d => d.key == Since).kind, Is.EqualTo(ConnectionDetailKind.DATE));
        });
    }

    [Test]
    public void GitHub_contributors_are_ids_without_bots_and_the_next_page_comes_from_the_link_header()
    {
        var ids = GitHubConnectionProvider.ParseContributorIds("""[{"id":1,"type":"User"},{"id":2,"type":"Bot"},{"id":3,"type":"User"},{"login":"anon"}]""").ToList();

        using var paged = new HttpResponseMessage();
        paged.Headers.TryAddWithoutValidation("Link",
            "<https://api.github.com/repositories/1/contributors?per_page=100&page=2>; rel=\"next\", <https://api.github.com/repositories/1/contributors?per_page=100&page=5>; rel=\"last\"");

        using var last = new HttpResponseMessage();
        last.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repositories/1/contributors?page=1>; rel=\"prev\"");

        Assert.Multiple(() =>
        {
            Assert.That(ids, Is.EqualTo(new long[] { 1, 3 }));
            Assert.That(GitHubConnectionProvider.NextLink(paged.Headers), Is.EqualTo("https://api.github.com/repositories/1/contributors?per_page=100&page=2"));
            Assert.That(GitHubConnectionProvider.NextLink(last.Headers), Is.Null);
            Assert.That(GitHubConnectionProvider.NextLink(new HttpResponseMessage().Headers), Is.Null);
            Assert.That(GitHubConnectionProvider.ParseContributorIds("{}"), Is.Empty);
        });
    }

    [Test]
    public void Spotify_says_whether_the_account_is_premium()
    {
        var snapshot = SpotifyConnectionProvider.Parse(Json(SpotifyMe));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Identity, Is.EqualTo(new ConnectionIdentity("wizzler", "JM Wizzler", "https://open.spotify.com/user/wizzler", "https://i.scdn.co/image/abc")));
            Assert.That(Detail(snapshot, SpotifyPremium), Is.EqualTo("true"));
            Assert.That(Detail(snapshot, SpotifyFollowers), Is.EqualTo("3829"));
        });

        var free = SpotifyConnectionProvider.Parse(Json("""{"id":"u","product":"free"}"""));

        Assert.Multiple(() =>
        {
            Assert.That(Detail(free, SpotifyPremium), Is.EqualTo("false"));
            Assert.That(free.Identity.Name, Is.EqualTo("u"), "no display name falls back to the id");
            Assert.That(free.Identity.Url, Is.EqualTo("https://open.spotify.com/user/u"));
        });
    }

    [Test]
    public void Twitter_is_the_id_the_handle_and_the_metrics()
    {
        var snapshot = TwitterConnectionProvider.Parse(Json(TwitterMe));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Identity, Is.EqualTo(new ConnectionIdentity("2244994945", "XDevelopers", "https://x.com/XDevelopers", "https://pbs.twimg.com/x.jpg")));
            Assert.That(Detail(snapshot, TwitterFollowers), Is.EqualTo("600000"));
            Assert.That(Detail(snapshot, TwitterVerified), Is.EqualTo("true"));
            Assert.That(Detail(snapshot, Since), Is.EqualTo("2013-12-14"));
        });
    }

    [Test]
    public void Twitch_is_the_first_user_of_the_answer()
    {
        var snapshot = TwitchConnectionProvider.Parse(Json(TwitchUsers));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Identity, Is.EqualTo(new ConnectionIdentity("141981764", "TwitchDev", "https://twitch.tv/twitchdev", "https://static-cdn.jtvnw.net/x.png")));
            Assert.That(Detail(snapshot, TwitchBroadcasterType), Is.EqualTo("partner"));
            Assert.That(Detail(snapshot, Since), Is.EqualTo("2016-12-14"));
            Assert.That(() => TwitchConnectionProvider.Parse(Json("""{"data":[]}""")), Throws.TypeOf<ProviderCallException>());
        });

        var plain = TwitchConnectionProvider.Parse(Json("""{"data":[{"id":"1","login":"x","broadcaster_type":""}]}"""));

        Assert.That(plain.Details.Any(d => d.key == TwitchBroadcasterType), Is.False, "an empty type is no fact");
    }

    [Test]
    public void YouTube_is_the_channel_and_hides_subscribers_when_the_channel_does()
    {
        var snapshot = YouTubeConnectionProvider.Parse(Json(YouTubeChannels));
        var hidden   = YouTubeConnectionProvider.Parse(Json(YouTubeChannels.Replace("\"hiddenSubscriberCount\":false", "\"hiddenSubscriberCount\":true")));

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Identity, Is.EqualTo(new ConnectionIdentity("UC_x5XG1OV2P6uZZ5FSM9Ttw", "Google Developers", "https://www.youtube.com/@googledevelopers", "https://yt3.ggpht.com/x")));
            Assert.That(Detail(snapshot, YouTubeSubscribers), Is.EqualTo("2350000"));
            Assert.That(Detail(snapshot, YouTubeVideos), Is.EqualTo("5764"));
            Assert.That(Detail(snapshot, Since), Is.EqualTo("2007-08-23"));
            Assert.That(hidden.Details.Any(d => d.key == YouTubeSubscribers), Is.False);
            Assert.That(() => YouTubeConnectionProvider.Parse(Json("""{"items":[]}""")), Throws.TypeOf<ProviderCallException>(), "a Google account without a channel");
        });
    }

    [Test]
    public void Steam_reads_the_persona_and_recognises_its_own_claimed_ids()
    {
        var identity = SteamConnectionProvider.ParseIdentity("76561197960435530", Json(SteamPlayer));

        Assert.Multiple(() =>
        {
            Assert.That(identity, Is.EqualTo(new ConnectionIdentity("76561197960435530", "Robin", "https://steamcommunity.com/id/robinwalker/", "https://avatars.steamstatic.com/x_full.jpg")));
            Assert.That(SteamConnectionProvider.TryParseSteamId("https://steamcommunity.com/openid/id/76561197960435530", out var id), Is.True);
            Assert.That(id, Is.EqualTo("76561197960435530"));
            Assert.That(SteamConnectionProvider.TryParseSteamId("https://evil.test/openid/id/76561197960435530", out _), Is.False);
            Assert.That(SteamConnectionProvider.TryParseSteamId("https://steamcommunity.com/openid/id/abc", out _), Is.False);
            Assert.That(SteamConnectionProvider.TryParseSteamId(null, out _), Is.False);
            Assert.That(SteamConnectionProvider.IsValidAssertion("ns:http://specs.openid.net/auth/2.0\nis_valid:true\n"), Is.True);
            Assert.That(SteamConnectionProvider.IsValidAssertion("ns:http://specs.openid.net/auth/2.0\nis_valid:false\n"), Is.False);
            Assert.That(SteamConnectionProvider.IsValidAssertion(""), Is.False);
        });
    }

    [Test]
    public void Telegram_is_the_subject_and_the_username_when_there_is_one()
    {
        var withUsername = TelegramConnectionProvider.IdentityFrom(new Dictionary<string, string>
        {
            ["sub"] = "777", ["preferred_username"] = "durov", ["name"] = "Pavel", ["picture"] = "https://t.me/i/userpic/x.jpg"
        });

        var nameOnly = TelegramConnectionProvider.IdentityFrom(new Dictionary<string, string> { ["sub"] = "778", ["name"] = "Anna" });

        Assert.Multiple(() =>
        {
            Assert.That(withUsername, Is.EqualTo(new ConnectionIdentity("777", "durov", "https://t.me/durov", "https://t.me/i/userpic/x.jpg")));
            Assert.That(nameOnly, Is.EqualTo(new ConnectionIdentity("778", "Anna", null, null)));
            Assert.That(() => TelegramConnectionProvider.IdentityFrom(new Dictionary<string, string>()), Throws.TypeOf<ProviderCallException>());
        });
    }

    [Test]
    public void Detail_helpers_render_invariantly()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Number("k", 1234567), Is.EqualTo(new ConnectionDetail("k", "1234567", ConnectionDetailKind.NUMBER)));
            Assert.That(Date("k", new DateTimeOffset(2011, 1, 25, 23, 59, 0, TimeSpan.FromHours(-5))), Is.EqualTo(new ConnectionDetail("k", "2011-01-26", ConnectionDetailKind.DATE)));
            Assert.That(Flag("k", true).value, Is.EqualTo("true"));
        });
    }
}
