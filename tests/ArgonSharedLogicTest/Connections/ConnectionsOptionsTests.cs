namespace ArgonSharedLogicTest.Connections;

using Argon.Features.Clustering;
using Argon.Features.Integrations.Connections;
using ArgonContracts;
using static ConnectionsTestOptions;

/// <summary>
/// The provider table (slugs, flags, callback addresses) and what a usable configuration is.
/// </summary>
[TestFixture]
public class ConnectionsOptionsTests
{
    [Test]
    public void Every_provider_has_a_slug_that_round_trips()
    {
        foreach (var provider in ConnectionProviders.All)
        {
            Assert.That(ConnectionProviders.TryParse(ConnectionProviders.Slug(provider), out var parsed), Is.True, provider.ToString());
            Assert.That(parsed, Is.EqualTo(provider));
            Assert.That(ConnectionProviders.Slug(provider), Does.Match("^[a-z]+$"));
        }

        Assert.Multiple(() =>
        {
            Assert.That(ConnectionProviders.TryParse("GitHub", out var github), Is.True);
            Assert.That(github, Is.EqualTo(ConnectionProvider.GITHUB));
            Assert.That(ConnectionProviders.TryParse("discord", out _), Is.False);
            Assert.That(ConnectionProviders.TryParse(null, out _), Is.False);
            Assert.That(ConnectionProviders.All, Has.Length.EqualTo(Enum.GetValues<ConnectionProvider>().Length), "a new enum member needs a slug");
        });
    }

    [Test]
    public void Flags_and_callbacks_are_named_after_the_slug()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ConnectionProviders.FlagOf(ConnectionProvider.SPOTIFY), Is.EqualTo("connections.spotify"));
            Assert.That(ConnectionProviders.CallbackUrl("https://api.argon.test/", ConnectionProvider.TELEGRAM),
                Is.EqualTo("https://api.argon.test/connections/callback/telegram"));
            Assert.That(Default().CallbackUrl(ConnectionProvider.STEAM), Is.EqualTo("https://api.argon.test/connections/callback/steam"));
        });
    }

    [Test]
    public void Every_provider_has_a_section_and_twitter_refreshes_monthly()
    {
        var options = new ConnectionsOptions();

        foreach (var provider in ConnectionProviders.All)
            Assert.That(options.For(provider), Is.Not.Null, provider.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(options.Twitter.DetailsRefreshEvery, Is.EqualTo(TimeSpan.FromDays(30)));
            Assert.That(options.GitHub.DetailsRefreshEvery, Is.EqualTo(TimeSpan.FromDays(7)));
            Assert.That(options.GitHub.ContributorCoin, Is.EqualTo("coin_argon_contributor"));
            Assert.That(options.GitHub.ContributorBadge, Is.EqualTo("contributor"));
            Assert.That(options.Steam.IsConfigured, Is.False);
            Assert.That(Default().Steam.IsConfigured, Is.True, "a Web API key is Steam's whole registration");
        });
    }

    [Test]
    public void A_complete_configuration_validates()
    {
        var report = new Report();

        Default().Validate(report);

        Assert.That(report.Errors, Is.Empty);
    }

    [Test]
    public void A_client_id_needs_a_secret_and_tokens_need_a_key()
    {
        var noSecret = Default();
        noSecret.Spotify.ClientSecret = "";

        var noKey = Default("");

        var steamOnly = new ConnectionsOptions { PublicCallbackBase = "https://api.argon.test", Steam = new() { WebApiKey = "k" } };

        Assert.Multiple(() =>
        {
            Assert.That(Errors(noSecret), Has.Some.Contains("ClientSecret"));
            Assert.That(Errors(noKey), Has.Some.Contains("TokenKey"));
            Assert.That(Errors(steamOnly), Is.Empty, "Steam keeps no token, so no key is needed for it alone");
        });
    }

    [Test]
    public void The_callback_base_and_the_repositories_are_checked()
    {
        var relative = Default();
        relative.PublicCallbackBase = "api.argon.test/";

        var withQuery = Default();
        withQuery.PublicCallbackBase = "https://api.argon.test/?x=1";

        var badRepo = Default();
        badRepo.GitHub.ContributorRepos = ["argon-chat", "a/b/c"];

        var retired = Default();
        retired.RetiredTokenKeys["1"] = "nope";

        Assert.Multiple(() =>
        {
            Assert.That(Errors(relative), Has.Some.Contains("PublicCallbackBase"));
            Assert.That(Errors(withQuery), Has.Some.Contains("PublicCallbackBase"));
            Assert.That(Errors(badRepo), Has.Count.EqualTo(2));
            Assert.That(Errors(retired), Has.Some.Contains("RetiredTokenKeys"));
        });
    }

    [Test]
    public void Nothing_registered_means_nothing_to_validate()
    {
        var githubOnly = new ConnectionsOptions { GitHub = new() { ClientId = "id", ClientSecret = "s" } };

        Assert.Multiple(() =>
        {
            Assert.That(Errors(new ConnectionsOptions { Enabled = false }), Is.Empty);
            Assert.That(Errors(new ConnectionsOptions()), Is.Empty, "an absent section is a deployment without providers, not a broken one");
            Assert.That(new ConnectionsOptions().AnyProviderConfigured, Is.False);
            Assert.That(Errors(githubOnly), Has.Some.Contains("PublicCallbackBase"), "the first registered app is what makes the callback required");
            Assert.That(Errors(githubOnly), Has.Some.Contains("TokenKey"));
        });
    }

    private static List<string> Errors(ConnectionsOptions options)
    {
        var report = new Report();
        options.Validate(report);
        return report.Errors;
    }

    private sealed class Report : IFeatureConfigurationReport
    {
        public List<string> Errors { get; } = [];

        public string Section       => ConnectionsOptions.SectionName;
        public bool   SectionExists => true;

        public TOther Read<TOther>(string section) where TOther : class => throw new NotSupportedException();

        public void Require(bool condition, string setting, string message)
        {
            if (!condition)
                Errors.Add($"{setting}: {message}");
        }

        public void Invalid(string message) => Errors.Add(message);

        public void Prefer(bool condition, string setting, string message) { }

        public void Required(string? value, string setting) => Require(!string.IsNullOrWhiteSpace(value), setting, "required");

        public void RequireUri(string? value, string setting, params string[] schemes) { }

        public void RequireFile(string? path, string setting) { }

        public void RequireRange(int value, int min, int max, string setting)
            => Require(value >= min && value <= max, setting, $"must be in [{min}, {max}]");

        public void RequireRange(TimeSpan value, TimeSpan min, TimeSpan max, string setting)
            => Require(value >= min && value <= max, setting, $"must be in [{min}, {max}]");
    }
}
