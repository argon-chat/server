namespace ArgonComplexTest.Tests;

using System.Net;
using System.Text.RegularExpressions;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static ChannelTestKit;

/// <summary>
/// Linked accounts end to end, against <see cref="FakeConnectionsApi"/>: the browser handshake and
/// its gate, the contributor coin, profile visibility, unlinking, the maintenance pass and the
/// Spotify activity.
/// </summary>
[TestFixture]
public class ConnectionsTests : TestBase
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(20);

    private static FakeConnectionsApi Fake => Services.GetRequiredService<FakeConnectionsApi>();

    private static IConnectionsInteraction ConnectionsOf(TestUserSession session)
        => session.Client.ForService<IConnectionsInteraction>(Services);

    // ── the browser half ─────────────────────────────────────────────────────────────────────

    private sealed record Page(string Kind, Guid? ResumeId, string Html);

    private static async Task<(string State, Guid HandshakeId, string Url)> BeginAsync(TestUserSession session, ConnectionProvider provider,
        CancellationToken ct, bool replace = false)
    {
        var begun = await ConnectionsOf(session).BeginConnect(provider, ConnectReturnKind.WEB, replace, ct);

        Assert.That(begun, Is.InstanceOf<SuccessBeginConnect>(), $"BeginConnect was refused: {(begun as FailedBeginConnect)?.error}");

        var success = (SuccessBeginConnect)begun;
        var state   = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(new Uri(success.url).Query)["state"].ToString();

        Assert.That(state, Is.Not.Empty, "the authorize URL carries no state");

        return (state, success.handshakeId, success.url);
    }

    /// <summary>The provider's redirect: no session cookie, as a cross-site navigation carries none.</summary>
    private async Task<Page> CallbackAsync(string provider, string query, CancellationToken ct)
    {
        using var response = await HttpClient.GetAsync($"/connections/callback/{provider}?{query}", ct);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the callback did not render a page");

        return Read(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The same-site continuation, with the web session of <paramref name="browser"/> if any.</summary>
    private async Task<Page> ResumeAsync(Guid handshakeId, TestUserSession? browser, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/connections/resume/{handshakeId}");

        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");

        if (browser is not null)
            request.Headers.TryAddWithoutValidation("Cookie", $"__Host-ArgonAccess={browser.Token}");

        using var response = await HttpClient.SendAsync(request, ct);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the resume did not render a page");

        return Read(await response.Content.ReadAsStringAsync(ct));
    }

    private static Page Read(string html)
    {
        var kind   = ConnectionPagePatterns.Kind().Match(html);
        var resume = ConnectionPagePatterns.Resume().Match(html);

        Assert.That(kind.Success, Is.True, $"the page names no result:\n{html}");

        return new Page(kind.Groups[1].Value, resume.Success ? Guid.Parse(resume.Groups[1].Value) : null, html);
    }

    /// <summary>A whole GitHub link for <paramref name="session"/>, answered as the GitHub account <paramref name="gitHubId"/>.</summary>
    private async Task<Page> LinkGitHubAsync(TestUserSession session, long gitHubId, string login, CancellationToken ct, bool replace = false)
    {
        var (state, _, _) = await BeginAsync(session, ConnectionProvider.GITHUB, ct, replace);
        var parked        = await CallbackAsync("github", $"code={Fake.GitHubCode(gitHubId, login)}&state={state}", ct);

        Assert.That(parked.Kind, Is.EqualTo("Continue"), "the callback did not park the exchange for the browser");
        Assert.That(parked.ResumeId, Is.Not.Null, "the parked page does not say where to continue");

        return await ResumeAsync(parked.ResumeId!.Value, session, ct);
    }

    private async Task<FakeConnectionsApi.SpotifyAccount> LinkSpotifyAsync(TestUserSession session, CancellationToken ct, int expiresIn = 3600, bool premium = true)
    {
        var account = new FakeConnectionsApi.SpotifyAccount($"sp{Guid.NewGuid():N}"[..20], "Listener", premium, expiresIn);

        var (state, _, _) = await BeginAsync(session, ConnectionProvider.SPOTIFY, ct);
        var parked        = await CallbackAsync("spotify", $"code={Fake.SpotifyCode(account)}&state={state}", ct);
        var linked        = await ResumeAsync(parked.ResumeId!.Value, session, ct);

        Assert.That(linked.Kind, Is.EqualTo("Linked"), "the Spotify link did not complete");

        return account;
    }

    private static async Task<UserConnection?> MineAsync(TestUserSession session, ConnectionProvider provider, CancellationToken ct)
        => (await ConnectionsOf(session).GetMyConnections(ct)).Values.FirstOrDefault(c => c.provider == provider);

    private static async Task<bool> HasCoinAsync(TestUserSession session, CancellationToken ct)
        => (await session.Inventory.GetMyInventoryItems(ct)).Values.Any(i => i.id == "coin_argon_contributor");

    // ── the handshake ────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Configured_providers_are_offered_and_the_rest_are_not(CancellationToken ct = default)
    {
        var session   = await CreateSessionAsync(ct);
        var providers = (await ConnectionsOf(session).GetProviders(ct)).Values.Select(p => p.provider).ToList();

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(providers, Is.EquivalentTo(new[] { ConnectionProvider.GITHUB, ConnectionProvider.SPOTIFY }));
            Assert.That(await ConnectionsOf(session).BeginConnect(ConnectionProvider.STEAM, ConnectReturnKind.WEB, false, ct),
                Is.EqualTo(new FailedBeginConnect(BeginConnectError.PROVIDER_DISABLED)));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_github_account_links_through_the_browser_and_pays_the_contributor_coin(CancellationToken ct = default)
    {
        var session  = await CreateSessionAsync(ct);
        var gitHubId = Fake.NextContributorId();

        await using var realtime = await RealtimeClient.ConnectAsync(session, ct);

        var (state, _, url) = await BeginAsync(session, ConnectionProvider.GITHUB, ct);

        Assert.Multiple(() =>
        {
            Assert.That(url, Does.StartWith("https://github.com/login/oauth/authorize?"));
            Assert.That(url, Does.Contain("redirect_uri=https%3A%2F%2Fapi.test.local%2Fconnections%2Fcallback%2Fgithub"));
        });

        var parked = await CallbackAsync("github", $"code={Fake.GitHubCode(gitHubId, "octo")}&state={state}", ct);
        var page   = await ResumeAsync(parked.ResumeId!.Value, session, ct);

        Assert.That(page.Kind, Is.EqualTo("Linked"), page.Html);

        var linked = await MineAsync(session, ConnectionProvider.GITHUB, ct);
        var told   = await realtime.WaitForAsync<UserConnectionsUpdated>(e => e.connections.Values.Any(c => c.provider == ConnectionProvider.GITHUB), Window, ct: ct);
        var me     = await session.Users.GetMyProfile(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(linked, Is.Not.Null);
            Assert.That(linked!.externalId, Is.EqualTo(gitHubId.ToString()));
            Assert.That(linked.name, Is.EqualTo("octo"));
            Assert.That(linked.url, Is.EqualTo("https://github.com/octo"));
            Assert.That(linked.verified, Is.True);
            Assert.That(linked.status, Is.EqualTo(ConnectionStatus.ACTIVE));
            Assert.That(linked.details.Values.Select(d => d.key), Is.SupersetOf(new[] { "github.public_repos", "github.followers", "since" }));
            Assert.That(told.userId, Is.EqualTo(session.UserId));
            Assert.That(await HasCoinAsync(session, ct), Is.True, "a contributor was not paid the coin");
            Assert.That(me.badges.Values, Does.Contain("contributor"), "the coin did not put the contributor badge on the card");
            Assert.That(me.connections?.Values.Select(c => c.provider), Is.EqualTo(new[] { ConnectionProvider.GITHUB }));
        });

        await using (var db = await DbAsync(ct))
        {
            var row = await db.UserConnections.AsNoTracking().SingleAsync(c => c.UserId == session.UserId, ct);

            Assert.Multiple(() =>
            {
                Assert.That(row.SealedTokens, Is.Not.Null);
                Assert.That(System.Text.Encoding.UTF8.GetString(row.SealedTokens!), Does.Not.Contain("gho_"), "the token is stored in the clear");
            });
        }
    }

    [Test, CancelAfter(120_000)]
    public async Task Someone_who_never_committed_links_without_a_coin(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        Assert.That((await LinkGitHubAsync(session, Fake.NextOutsiderId(), "visitor", ct)).Kind, Is.EqualTo("Linked"));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await MineAsync(session, ConnectionProvider.GITHUB, ct), Is.Not.Null);
            Assert.That(await HasCoinAsync(session, ct), Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task One_github_account_vouches_for_one_argon_account_and_pays_once(CancellationToken ct = default)
    {
        var first    = await CreateSessionAsync(ct);
        var second   = await CreateSessionAsync(ct);
        var gitHubId = Fake.NextContributorId();

        Assert.That((await LinkGitHubAsync(first, gitHubId, "shared", ct)).Kind, Is.EqualTo("Linked"));
        Assert.That((await LinkGitHubAsync(second, gitHubId, "shared", ct)).Kind, Is.EqualTo("AlreadyLinkedElsewhere"),
            "a second Argon account took a GitHub account that is linked already");

        Assert.That(await ConnectionsOf(first).Disconnect(ConnectionProvider.GITHUB, ct), Is.True);

        Assert.That((await LinkGitHubAsync(second, gitHubId, "shared", ct)).Kind, Is.EqualTo("Linked"),
            "once released, the GitHub account can be linked elsewhere");

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await HasCoinAsync(first, ct), Is.True, "the coin stays with whoever was paid it");
            Assert.That(await HasCoinAsync(second, ct), Is.False, "relinking the same GitHub account minted a second coin");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_browser_signed_in_as_someone_else_cannot_finish_the_handshake(CancellationToken ct = default)
    {
        var victim   = await CreateSessionAsync(ct);
        var attacker = await CreateSessionAsync(ct);

        // The attacker starts a handshake for their own account and gets the victim's browser to
        // finish it: the provider consents as the victim, the browser is signed in as the victim.
        var (state, _, _) = await BeginAsync(attacker, ConnectionProvider.GITHUB, ct);
        var parked        = await CallbackAsync("github", $"code={Fake.GitHubCode(Fake.NextContributorId(), "victim")}&state={state}", ct);
        var refused       = await ResumeAsync(parked.ResumeId!.Value, victim, ct);
        var again         = await ResumeAsync(parked.ResumeId!.Value, attacker, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(refused.Kind, Is.EqualTo("WrongUser"));
            Assert.That(again.Kind, Is.EqualTo("Expired"), "a refused handshake stayed usable");
            Assert.That(await MineAsync(attacker, ConnectionProvider.GITHUB, ct), Is.Null, "the attacker got the victim's GitHub");
            Assert.That(await HasCoinAsync(attacker, ct), Is.False);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_browser_with_no_session_is_asked_to_sign_in_and_can_then_finish(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (state, _, _) = await BeginAsync(session, ConnectionProvider.GITHUB, ct);
        var parked        = await CallbackAsync("github", $"code={Fake.GitHubCode(Fake.NextOutsiderId(), "later")}&state={state}", ct);
        var signIn        = await ResumeAsync(parked.ResumeId!.Value, null, ct);
        var finished      = await ResumeAsync(parked.ResumeId!.Value, session, ct);

        Assert.Multiple(() =>
        {
            Assert.That(signIn.Kind, Is.EqualTo("NeedsSignIn"));
            Assert.That(signIn.Html, Does.Contain("https://app.test.local"), "the page does not send the browser to the web client");
            Assert.That(signIn.ResumeId, Is.EqualTo(parked.ResumeId), "the sign-in page lost the continuation");
            Assert.That(finished.Kind, Is.EqualTo("Linked"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_state_is_good_once_and_a_refusal_at_the_provider_links_nothing(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (state, _, _) = await BeginAsync(session, ConnectionProvider.GITHUB, ct);
        var first         = await CallbackAsync("github", $"code={Fake.GitHubCode(Fake.NextOutsiderId(), "once")}&state={state}", ct);
        var replay        = await CallbackAsync("github", $"code={Fake.GitHubCode(Fake.NextOutsiderId(), "twice")}&state={state}", ct);

        var (denyState, _, _) = await BeginAsync(session, ConnectionProvider.GITHUB, ct);
        var denied            = await CallbackAsync("github", $"error=access_denied&state={denyState}", ct);
        var forged            = await CallbackAsync("github", "code=x&state=never-issued", ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.Kind, Is.EqualTo("Continue"));
            Assert.That(replay.Kind, Is.EqualTo("Expired"), "a replayed callback was accepted");
            Assert.That(denied.Kind, Is.EqualTo("Denied"));
            Assert.That(forged.Kind, Is.EqualTo("Expired"));
            Assert.That(await MineAsync(session, ConnectionProvider.GITHUB, ct), Is.Null, "nothing should be linked before the browser finishes");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_second_account_of_the_same_provider_needs_replace(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        Assert.That((await LinkGitHubAsync(session, Fake.NextOutsiderId(), "first", ct)).Kind, Is.EqualTo("Linked"));

        Assert.That(await ConnectionsOf(session).BeginConnect(ConnectionProvider.GITHUB, ConnectReturnKind.WEB, false, ct),
            Is.EqualTo(new FailedBeginConnect(BeginConnectError.PROVIDER_ALREADY_LINKED)));

        var replacement = Fake.NextOutsiderId();

        Assert.That((await LinkGitHubAsync(session, replacement, "second", ct, replace: true)).Kind, Is.EqualTo("Linked"));

        var mine = await MineAsync(session, ConnectionProvider.GITHUB, ct);

        Assert.That(mine?.externalId, Is.EqualTo(replacement.ToString()));
    }

    // ── what others see ──────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task The_profile_shows_connections_by_the_options_and_the_privacy_rule(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var member   = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, ct);
        await JoinAsync(owner, member, spaceId, ct);

        Assert.That((await LinkGitHubAsync(owner, Fake.NextOutsiderId(), "shown", ct)).Kind, Is.EqualTo("Linked"));

        async Task<IReadOnlyList<ProfileConnection>> SeenBy(TestUserSession viewer)
        {
            var lookup = await viewer.Users.LookupProfile(owner.UserId, ct);
            return lookup is SuccessLookupProfile found ? found.profile.connections?.Values.ToList() ?? [] : [];
        }

        var shown = await SeenBy(member);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(shown.Select(c => c.name), Is.EqualTo(new[] { "shown" }));
            Assert.That(shown.Single().details.Values, Is.Not.Empty);
            Assert.That(await SeenBy(stranger), Is.Empty, "someone with no standing saw the owner's connections");
            Assert.That((await ConnectionsOf(stranger).GetUserConnections(owner.UserId, ct)).Values, Is.Empty);
        });

        var hidden = await ConnectionsOf(owner).UpdateOptions(ConnectionProvider.GITHUB, new IonPartial<ConnectionOptions>().Modify(x => x.showDetails, false), ct);

        Assert.That(hidden, Is.InstanceOf<SuccessUpdateConnection>());
        Assert.That((await SeenBy(member)).Single().details.Values, Is.Empty, "showDetails off still showed the details");

        await ConnectionsOf(owner).UpdateOptions(ConnectionProvider.GITHUB, new IonPartial<ConnectionOptions>().Modify(x => x.displayOnProfile, false), ct);
        Assert.That(await SeenBy(member), Is.Empty, "displayOnProfile off still showed the connection");

        await ConnectionsOf(owner).UpdateOptions(ConnectionProvider.GITHUB, new IonPartial<ConnectionOptions>().Modify(x => x.displayOnProfile, true), ct);
        Assert.That(await SeenBy(member), Has.Count.EqualTo(1));

        Assert.That(await owner.Privacy.SetPrivacyRule("connections.visibility", PrivacyRuleMode.NOBODY, null, IonArray<Guid>.Empty, IonArray<Guid>.Empty, ct), Is.True);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await SeenBy(member), Is.Empty, "the privacy rule did not hide the connections");
            Assert.That((await owner.Users.GetMyProfile(ct)).connections?.Values, Has.Count.EqualTo(1), "the owner always sees their own");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Options_a_provider_cannot_honour_are_refused(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        Assert.That((await LinkGitHubAsync(session, Fake.NextOutsiderId(), "plain", ct)).Kind, Is.EqualTo("Linked"));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await ConnectionsOf(session).UpdateOptions(ConnectionProvider.GITHUB,
                new IonPartial<ConnectionOptions>().Modify(x => x.displayAsStatus, true), ct),
                Is.EqualTo(new FailedUpdateConnection(ConnectionError.OPTION_NOT_SUPPORTED)), "GitHub has no status to show");
            Assert.That(await ConnectionsOf(session).UpdateOptions(ConnectionProvider.SPOTIFY,
                new IonPartial<ConnectionOptions>().Modify(x => x.showDetails, false), ct),
                Is.EqualTo(new FailedUpdateConnection(ConnectionError.NOT_LINKED)));
        });
    }

    // ── unlink, maintenance ──────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Unlinking_revokes_at_the_provider_and_drops_the_row(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        Assert.That((await LinkGitHubAsync(session, Fake.NextOutsiderId(), "leaving", ct)).Kind, Is.EqualTo("Linked"));

        var revokedBefore = Fake.RevokedGitHubTokens.Count;

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await ConnectionsOf(session).Disconnect(ConnectionProvider.GITHUB, ct), Is.True);
            Assert.That(await ConnectionsOf(session).Disconnect(ConnectionProvider.GITHUB, ct), Is.False, "a second disconnect found something");
            Assert.That(Fake.RevokedGitHubTokens.Count, Is.GreaterThan(revokedBefore), "the grant was not revoked at GitHub");
            Assert.That(await MineAsync(session, ConnectionProvider.GITHUB, ct), Is.Null);
        });

        await using var db = await DbAsync(ct);

        Assert.That(await db.UserConnections.AnyAsync(c => c.UserId == session.UserId, ct), Is.False);
    }

    [Test, CancelAfter(120_000)]
    public async Task Maintenance_turns_a_revoked_grant_into_needs_reauth_with_one_notification(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var account = await LinkSpotifyAsync(session, ct, expiresIn: 30);

        Fake.RevokeSpotify(account.Id);

        // Nothing has used the connection for longer than the keep-alive period.
        await using (var db = await DbAsync(ct))
        {
            await db.UserConnections
               .Where(c => c.UserId == session.UserId)
               .ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, DateTimeOffset.UtcNow.AddDays(-2)), ct);
        }

        var maintenance = Grains.GetGrain<IConnectionsMaintenanceGrain>(IConnectionsMaintenanceGrain.SingletonId);

        await maintenance.RunOnceAsync(ct);
        await maintenance.RunOnceAsync(ct);

        var connection = await MineAsync(session, ConnectionProvider.SPOTIFY, ct);
        var notices    = (await session.Users.GetNotificationFeed(50, null, ct)).Values.Where(n => n.type == "connection_needs_reauth").ToList();

        Assert.Multiple(() =>
        {
            Assert.That(connection?.status, Is.EqualTo(ConnectionStatus.NEEDS_REAUTH));
            Assert.That(notices, Has.Count.EqualTo(1), "a revoked grant should notify exactly once");
        });

        Assert.That(await ConnectionsOf(session).RefreshConnection(ConnectionProvider.SPOTIFY, ct),
            Is.EqualTo(new FailedRefreshConnection(ConnectionError.NEEDS_REAUTH)));

        // Re-authenticating the same account needs no "replace" and makes the connection whole again.
        var relinked = await LinkSpotifyAgainAsync(session, account, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(relinked.Kind, Is.EqualTo("Linked"));
            Assert.That((await MineAsync(session, ConnectionProvider.SPOTIFY, ct))?.status, Is.EqualTo(ConnectionStatus.ACTIVE));
        });
    }

    private async Task<Page> LinkSpotifyAgainAsync(TestUserSession session, FakeConnectionsApi.SpotifyAccount account, CancellationToken ct)
    {
        var fresh         = new FakeConnectionsApi.SpotifyAccount(account.Id, account.DisplayName, account.Premium, 3600);
        var (state, _, _) = await BeginAsync(session, ConnectionProvider.SPOTIFY, ct);
        var parked        = await CallbackAsync("spotify", $"code={Fake.SpotifyCode(fresh)}&state={state}", ct);

        return await ResumeAsync(parked.ResumeId!.Value, session, ct);
    }

    // ── the Spotify activity ─────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Spotify_shown_as_status_reaches_the_space_and_leaves_with_the_option(CancellationToken ct = default)
    {
        var listener = await CreateSessionAsync(ct);
        var observer = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(observer, ct);
        await JoinAsync(observer, listener, spaceId, ct);

        await using var watching = await RealtimeClient.ConnectAsync(observer, ct);
        await using var online   = await RealtimeClient.ConnectAsync(listener, ct);

        var account = await LinkSpotifyAsync(listener, ct);
        var trackId = $"t{Guid.NewGuid():N}"[..22];

        Fake.SetPlaying(account.Id, FakeConnectionsApi.Track(trackId, "Cut To The Feeling", "Carly Rae Jepsen"));

        var mark = watching.Mark();

        var shown = await ConnectionsOf(listener).UpdateOptions(ConnectionProvider.SPOTIFY,
            new IonPartial<ConnectionOptions>().Modify(x => x.displayAsStatus, true), ct);

        Assert.That(shown, Is.InstanceOf<SuccessUpdateConnection>());

        var record  = await watching.WaitForRecordAsync<OnUserPresenceActivityChanged>(
            e => e.userId == listener.UserId && e.presence.spotify?.trackId == trackId, Window, mark, ct);
        var changed = (OnUserPresenceActivityChanged)record.Event;

        Assert.Multiple(() =>
        {
            Assert.That(record.SpaceId, Is.EqualTo(spaceId));
            Assert.That(changed.presence.kind, Is.EqualTo(ActivityPresenceKind.LISTEN));
            Assert.That(changed.presence.source, Is.EqualTo(ActivitySource.SPOTIFY));
            Assert.That(changed.presence.titleName, Is.EqualTo("Cut To The Feeling - Carly Rae Jepsen"));
            Assert.That(changed.presence.spotify!.artists.Values, Is.EqualTo(new[] { "Carly Rae Jepsen" }));
            Assert.That(changed.presence.spotify.listenAlongOpen, Is.True, "listen-along is allowed by default");
            Assert.That(changed.presence.endTimestampSeconds, Is.GreaterThan(changed.presence.startTimestampSeconds));
        });

        var hideMark = watching.Mark();

        await ConnectionsOf(listener).UpdateOptions(ConnectionProvider.SPOTIFY,
            new IonPartial<ConnectionOptions>().Modify(x => x.displayAsStatus, false), ct);

        await watching.WaitForRecordAsync<OnUserPresenceActivityRemoved>(e => e.userId == listener.UserId, Window, hideMark, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task A_listener_follows_the_host_through_the_server(CancellationToken ct = default)
    {
        var host     = await CreateSessionAsync(ct);
        var listener = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(host, ct);
        await JoinAsync(host, listener, spaceId, ct);

        await using var hostOnline     = await RealtimeClient.ConnectAsync(host, ct);
        await using var listenerOnline = await RealtimeClient.ConnectAsync(listener, ct);

        var hostAccount     = await LinkSpotifyAsync(host, ct);
        var listenerAccount = await LinkSpotifyAsync(listener, ct);
        var trackId         = $"t{Guid.NewGuid():N}"[..22];

        Fake.SetPlaying(hostAccount.Id, FakeConnectionsApi.Track(trackId, "Song", "Band", progressMs: 40_000));

        var mark = hostOnline.Mark();

        await ConnectionsOf(host).UpdateOptions(ConnectionProvider.SPOTIFY, new IonPartial<ConnectionOptions>().Modify(x => x.displayAsStatus, true), ct);

        // The host's activity has to be up before anyone can listen along with it.
        await PollAsync(() => Grains.GetGrain<ISpotifyPresenceGrain>(host.UserId).GetCurrentAsync(), t => t?.trackId == trackId, Window, ct);

        var commandsBefore = Fake.SpotifyCommands.Count;
        var joined         = await ConnectionsOf(listener).JoinListenAlong(host.UserId, ct);

        Assert.That(joined, Is.InstanceOf<SuccessListenAlong>(), $"the join was refused: {(joined as FailedListenAlong)?.error}");

        var play = Fake.SpotifyCommands.Skip(commandsBefore).FirstOrDefault(c => c.Command.EndsWith("/play", StringComparison.Ordinal));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(((SuccessListenAlong)joined).state.listeners.Values, Is.EqualTo(new[] { listener.UserId }));
            Assert.That(play.Body, Does.Contain($"spotify:track:{trackId}"), "the listener's Spotify was not started on the host's track");
            Assert.That((await ConnectionsOf(listener).GetListenAlongState(ct))?.hostUserId, Is.EqualTo(host.UserId));
            Assert.That((await ConnectionsOf(host).GetListenAlongState(ct))?.listeners.Values, Is.EqualTo(new[] { listener.UserId }));
        });

        await hostOnline.WaitForAsync<ListenAlongChanged>(e => e.listeners.Values.Contains(listener.UserId), Window, ct: ct);

        var endMark = listenerOnline.Mark();

        await ConnectionsOf(listener).LeaveListenAlong(ct);

        var ended = (ListenAlongEnded)(await listenerOnline.WaitForRecordAsync<ListenAlongEnded>(e => e.hostUserId == host.UserId, Window, endMark, ct)).Event;

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(ended.reason, Is.EqualTo(ListenAlongEndReason.LEFT));
            Assert.That(await ConnectionsOf(listener).GetListenAlongState(ct), Is.Null);
        });

        var free = await LinkFreeAccountAsync(ct);

        Assert.That(await ConnectionsOf(free).JoinListenAlong(host.UserId, ct), Is.InstanceOf<FailedListenAlong>(),
            "an account with no standing towards the host joined");

        _ = listenerAccount;
    }

    private async Task<TestUserSession> LinkFreeAccountAsync(CancellationToken ct)
    {
        var session = await CreateSessionAsync(ct);
        await LinkSpotifyAsync(session, ct, premium: false);
        return session;
    }
}

internal static partial class ConnectionPagePatterns
{
    [GeneratedRegex("name=\"argon-connection-result\" content=\"([A-Za-z]+)\"")]
    public static partial Regex Kind();

    [GeneratedRegex("/connections/resume/([0-9a-fA-F-]{36})")]
    public static partial Regex Resume();
}
