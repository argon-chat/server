namespace ArgonComplexTest.Tests;

using System.Net;
using AccountContracts;
using Argon.Grains.Interfaces;
using BotSuspendedBy = Argon.Core.Entities.Data.BotSuspendedBy;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using ConsoleContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using static DevTeamsHarness;
using AppLifecycle = AccountContracts.BotLifecycleState;

/// <summary>
/// The applications a dev team owns, as the developer console creates and edits them, and what the
/// rest of the product makes of those edits.
/// </summary>
/// <remarks>
/// <para>Every write here has a reader somewhere else: a token is read by the Bot API's
/// authentication handler, a lifecycle state by the bot directory and the install flow, a scope by
/// the OAuth token endpoint, a redirect by the identity server's CORS check. So each test makes its
/// change through <c>IAppManagement</c> and then asks the reader — a console edit that the grain
/// saved and nobody honours is the failure worth catching.</para>
///
/// <para>The sign-in policy (<c>IAppsManagementGrain</c>) is called as the identity server calls it,
/// directly on the grain. <c>AegisOAuthTests</c> drives it over HTTP for the ordinary cases; what is
/// here is the policy's corners — internal apps and the staff mailbox rule.</para>
/// </remarks>
[TestFixture]
public class AppManagementTests : TestBase
{
    private IDevTeamsGrain       Teams      => GetGrainFactory().GetGrain<IDevTeamsGrain>(Guid.Empty);
    private IAppsManagementGrain Policy     => GetGrainFactory().GetGrain<IAppsManagementGrain>(Guid.Empty);
    private IAdminDirectoryGrain Operators  => GetGrainFactory().GetGrain<IAdminDirectoryGrain>(Guid.Empty);

    // ── creating ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A bot username has to end in "bot" and must not already belong to anyone, whatever its case.
    /// </summary>
    /// <remarks>
    /// The console checks first so the client gets an error it can render; the grain checks again
    /// because the two calls race. Both refusals are pinned — the grain's is the one that stands
    /// between two teams claiming the same name in the same second.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_username_has_to_end_in_bot_and_be_unclaimed(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "names");
        var taken = BotUsername("taken");

        await As(owner, c => c.Apps.CreateBotApp(team.teamId, "First", taken, ct).Ok());

        var noSuffix = await As(owner, c => c.Apps.CheckUsernameForBot(team.teamId, "helperbots", ct));
        var claimed  = await As(owner, c => c.Apps.CheckUsernameForBot(team.teamId, taken.ToUpperInvariant(), ct));
        var person   = await As(owner, c => c.Apps.CheckUsernameForBot(team.teamId, owner.Credentials.username, ct));
        var free     = await As(owner, c => c.Apps.CheckUsernameForBot(team.teamId, BotUsername("free"), ct));

        Assert.Multiple(() =>
        {
            Assert.That(noSuffix, Is.EqualTo(CheckBotUsernameValid.POSTFIX_BOT_REQUIRED));
            Assert.That(claimed, Is.EqualTo(CheckBotUsernameValid.ALREADY_CLAIMED), "the check is case-sensitive");
            Assert.That(person, Is.EqualTo(CheckBotUsernameValid.POSTFIX_BOT_REQUIRED));
            Assert.That(free, Is.EqualTo(CheckBotUsernameValid.OK));
        });

        Assert.That(await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Second", taken, ct)),
            Is.EqualTo(new FailedAppDetails(AppManagementError.INVALID_USERNAME)), "the console created a second bot under a taken name");

        Assert.That(async () => await Teams.CreateBotAppAsync(team.teamId, "Second", taken.ToUpperInvariant(), ct),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("claimed"));
        Assert.That(async () => await Teams.CreateBotAppAsync(team.teamId, "Suffixless", "suffixless", ct),
            Throws.InstanceOf<InvalidOperationException>().With.Message.Contains("end in 'bot'"));

        var details = await As(owner, c => c.Teams.GetTeamDetails(team.teamId, ct).Ok());
        Assert.That(details.apps, Has.Count.EqualTo(1), "a refused bot left an application behind");
    }

    /// <summary>
    /// A new bot starts in development with a working token, OAuth required, and a scope list that
    /// shows what it could ask for and what verification would unlock.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_new_bot_starts_in_development_with_a_working_token(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "newbot");
        var username = BotUsername("fresh");

        var created = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Fresh Bot", username, ct).Ok());
        var read    = await As(owner, c => c.Apps.GetAppDetails(team.teamId, created.appId, ct).Ok());
        var status  = await BotGetMeAsync(created.botDetails!.botToken, ct);

        var scopes = read.requiredScopes.ToDictionary(s => s.key);

        Assert.Multiple(() =>
        {
            Assert.That(read.kind, Is.EqualTo(AppKind.BotApp));
            Assert.That(read.clientAppDetails, Is.Null);
            Assert.That(read.clientId, Is.EqualTo(created.clientId));
            Assert.That(read.clientSecret, Is.EqualTo(created.clientSecret));
            Assert.That(read.verificationKey, Is.EqualTo(created.verificationKey));
            Assert.That(read.botDetails!.botToken, Is.EqualTo(created.botDetails.botToken));
            Assert.That(read.botDetails.lifecycleState, Is.EqualTo(AppLifecycle.Development));
            Assert.That(read.botDetails.requiresOAuth2, Is.True);
            Assert.That(read.botDetails.isVerfied, Is.False);
            Assert.That(read.botDetails.maxSpaces, Is.EqualTo(5));

            Assert.That(created.botDetails.botToken, Does.StartWith(TokenPrefixOf(created.appId) + ":"),
                "the token does not carry the app id the authentication handler resolves it by");
            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), "a new bot's own token was refused by the Bot API");

            Assert.That(scopes.Keys, Is.EquivalentTo(new[] { "openid", "identity", "user.read", "email", "offline_access" }),
                "an unverified bot without a staff account was offered scopes beyond the default set");
            Assert.That(scopes["openid"], Is.EqualTo(new ScopeKeyValue(true, "openid", true)));
            Assert.That(scopes["user.read"], Is.EqualTo(new ScopeKeyValue(false, "user.read", false)));
            Assert.That(scopes["offline_access"].isLocked, Is.True, "offline_access is unlocked on an unverified bot");
        });
    }

    /// <summary>
    /// An application is only reachable under the team that owns it, even by someone who is a member of
    /// another team and passes that team's gate.
    /// </summary>
    /// <remarks>
    /// The console resolves the team from the caller's membership and the app from the request, so the
    /// grain matching on both is what stops a developer from editing another team's bot by pairing
    /// their own team id with a guessed app id.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_app_answers_only_under_its_own_team(CancellationToken ct = default)
    {
        var victim   = await CreateSessionAsync(ct);
        var attacker = await CreateSessionAsync(ct);

        var victimTeam   = await CreateTeamAsync(victim, "victim");
        var attackerTeam = await CreateTeamAsync(attacker, "attacker");

        var bot    = await As(victim, c => c.Apps.CreateBotApp(victimTeam.teamId, "Victim Bot", BotUsername("victim"), ct).Ok());
        var client = await As(victim, c => c.Apps.CreateClientApp(victimTeam.teamId, "Victim App", ClientAppPlatform.WebBased, ct).Ok());

        var attempts = new Dictionary<string, Func<DevConsole, Task<object>>>
        {
            ["GetAppDetails(bot)"]    = async c => await c.Apps.GetAppDetails(attackerTeam.teamId, bot.appId, ct),
            ["GetAppDetails(client)"] = async c => await c.Apps.GetAppDetails(attackerTeam.teamId, client.appId, ct),
            ["RegenerateBotToken"]    = async c => await c.Apps.RegenerateBotToken(attackerTeam.teamId, bot.appId, ct),
            ["PublishBot"]            = async c => await c.Apps.PublishBot(attackerTeam.teamId, bot.appId, ct),
            ["SuspendBot"]            = async c => await c.Apps.SuspendBot(attackerTeam.teamId, bot.appId, ct),
            ["UpdateBotEntitlements"] = async c => await c.Apps.UpdateBotEntitlements(attackerTeam.teamId, bot.appId, ulong.MaxValue, ct),
            ["SetBotOAuth"]           = async c => await c.Apps.SetBotOAuth(attackerTeam.teamId, bot.appId, false, ct),
            ["UpdateScope"]           = async c => await c.Apps.UpdateScope(attackerTeam.teamId, client.appId, new ScopeKeyValue(true, "email", false), ct),
            ["RemoveRedirect"]        = async c => await c.Apps.RemoveRedirect(attackerTeam.teamId, client.appId, "https://x.test.local/cb", ct)
        };

        var notFound = new object[]
        {
            new FailedAppDetails(AppManagementError.NOT_FOUND),
            new FailedRegenerateBotToken(AppManagementError.NOT_FOUND),
            new FailedAppManagement(AppManagementError.NOT_FOUND)
        };

        foreach (var (name, call) in attempts)
            Assert.That(await As(attacker, call), Is.AnyOf(notFound), $"{name} reached another team's application");

        var after = await As(victim, c => c.Apps.GetAppDetails(victimTeam.teamId, bot.appId, ct).Ok());

        Assert.Multiple(() =>
        {
            Assert.That(after.botDetails!.botToken, Is.EqualTo(bot.botDetails!.botToken));
            Assert.That(after.botDetails.lifecycleState, Is.EqualTo(AppLifecycle.Development));
            Assert.That(after.botDetails.requiresOAuth2, Is.True);
        });
    }

    // ── what an app may ask for ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Scope toggles are idempotent and reversible, and the token endpoint is given only the scopes
    /// the app is entitled to — a locked scope it asked for anyway stays unhonoured.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Scopes_are_toggled_idempotently_and_only_entitled_ones_are_honoured(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "scopes");
        var bot   = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Scoped", BotUsername("scoped"), ct).Ok());

        await As(owner, c => c.Apps.UpdateScope(team.teamId, bot.appId, new ScopeKeyValue(true, "user.read", false), ct).Ok());
        await As(owner, c => c.Apps.UpdateScope(team.teamId, bot.appId, new ScopeKeyValue(true, "user.read", false), ct).Ok());
        await As(owner, c => c.Apps.UpdateScope(team.teamId, bot.appId, new ScopeKeyValue(true, "offline_access", true), ct).Ok());

        var granted     = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());
        var credentials = await Policy.GetCredentialsForBotAsync(bot.clientId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(granted.requiredScopes.Single(s => s.key == "user.read").isRequired, Is.True);
            Assert.That(granted.requiredScopes.Single(s => s.key == "offline_access").isRequired, Is.True,
                "the console does not show a locked scope the app asked for");
            Assert.That(credentials, Is.Not.Null);
            Assert.That(credentials!.scopes, Is.EqualTo(new[] { "user.read" }),
                "the token endpoint was offered a scope twice, or a locked one");
            Assert.That(credentials.IsAllowedRefreshToken, Is.False,
                "an unverified bot was allowed refresh tokens");
        });

        await As(owner, c => c.Apps.UpdateScope(team.teamId, bot.appId, new ScopeKeyValue(false, "user.read", false), ct).Ok());
        await As(owner, c => c.Apps.UpdateScope(team.teamId, bot.appId, new ScopeKeyValue(false, "email", false), ct).Ok());

        var revoked = await Policy.GetCredentialsForBotAsync(bot.clientId, ct);

        Assert.That(revoked!.scopes, Is.Empty);
    }

    /// <summary>
    /// A redirect is judged by the rules of where the app runs, is stored once, can be removed, and
    /// feeds the identity server's list of allowed origins.
    /// </summary>
    /// <remarks>
    /// The console's accepted cases here are all ones that make no outbound connection — a private-use
    /// scheme for a desktop client — because the web rules dial the redirect host to inspect its
    /// certificate. The grain is written to directly for the web origin the CORS list is checked on.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Redirects_follow_the_rules_of_where_the_app_runs(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var team    = await CreateTeamAsync(owner, "redirects");
        var desktop = await As(owner, c => c.Apps.CreateClientApp(team.teamId, "Desktop", ClientAppPlatform.WindowsDesktop, ct).Ok());
        var bot     = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Web Bot", BotUsername("redir"), ct).Ok());

        const string native = "gl.argon.devteams://callback";

        var added     = await As(owner, c => c.Apps.AddRedirect(team.teamId, desktop.appId, native, ct));
        var duplicate = await As(owner, c => c.Apps.AddRedirect(team.teamId, desktop.appId, native, ct));
        var plainHttp = await As(owner, c => c.Apps.AddRedirect(team.teamId, desktop.appId, "http://devteams.test.local/cb", ct));
        var botNative = await As(owner, c => c.Apps.AddRedirect(team.teamId, bot.appId, native, ct));

        var stored = await As(owner, c => c.Apps.GetAppDetails(team.teamId, desktop.appId, ct).Ok());

        Assert.Multiple(() =>
        {
            Assert.That(added, Is.EqualTo(new AddRedirectResult(true, null)));
            Assert.That(duplicate.ok, Is.False, "the same redirect was stored twice");
            Assert.That(duplicate.error, Is.EqualTo("Redirect already exists."));
            Assert.That(plainHttp.ok, Is.False, "a plain-http redirect to a remote host was accepted");
            Assert.That(plainHttp.error, Does.Contain("HTTPS"));
            Assert.That(botNative.ok, Is.False, "a bot, which runs on the web, was given a private-use scheme");
            Assert.That(stored.allowedRedirects, Is.EqualTo(new[] { native }));
            Assert.That(stored.clientAppDetails!.platform, Is.EqualTo(ClientAppPlatform.WindowsDesktop));
            Assert.That(stored.clientAppDetails.allowedDevelopmentRegenerateCoockies, Is.False);
        });

        await As(owner, c => c.Apps.RemoveRedirect(team.teamId, desktop.appId, native, ct).Ok());
        await As(owner, c => c.Apps.RemoveRedirect(team.teamId, desktop.appId, native, ct).Ok());

        var removed = await As(owner, c => c.Apps.GetAppDetails(team.teamId, desktop.appId, ct).Ok());
        Assert.That(removed.allowedRedirects, Is.Empty, "a removed redirect is still registered");

        var host   = $"devteams-{Guid.NewGuid():N}.test.local";
        var origin = $"https://{host}:8443";

        await Teams.AddRedirectAsync(team.teamId, bot.appId, $"{origin}/oauth/callback?x=1", ct);
        await Teams.AddRedirectAsync(team.teamId, bot.appId, "not a uri at all", ct);

        var origins = await Teams.GetAllAllowedOriginsAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(origins, Does.Contain(origin), "a registered redirect's origin is not allowed by CORS");
            Assert.That(origins, Has.None.Contains("not a uri"));
        });

        await Teams.RemoveRedirectAsync(team.teamId, bot.appId, $"{origin}/oauth/callback?x=1", ct);

        Assert.That(await Teams.GetAllAllowedOriginsAsync(ct), Does.Not.Contain(origin),
            "an origin outlived the only redirect that registered it");
    }

    // ── what the rest of the product reads ───────────────────────────────────────────────────

    /// <summary>
    /// Publishing puts a bot where spaces can find and install it; unpublishing takes it back out.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Publishing_decides_whether_a_space_can_find_and_install_the_bot(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var admin    = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "publish");
        var username = BotUsername("listed");
        var bot      = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Listed Bot", username, ct).Ok());

        var (spaceId, _) = await CreateSpaceWithChannelAsync(admin, "Bot shop", ct);
        var directory    = admin.Client.ForService<IBotManagementInteraction>(FactoryAsp.Services);

        var hidden     = await directory.SearchBots(spaceId, username, ct);
        var refusedDev = await directory.InstallBot(spaceId, bot.appId, ct);

        await As(owner, c => c.Apps.PublishBot(team.teamId, bot.appId, ct).Ok());

        var found   = await directory.SearchBots(spaceId, $"  {username.ToUpperInvariant()} ", ct);
        var details = await directory.GetBotDetails(spaceId, bot.appId, ct);
        var blank   = await directory.SearchBots(spaceId, "   ", ct);

        await InstallAsync(admin, spaceId, bot.appId, ct);
        var twice = await directory.InstallBot(spaceId, bot.appId, ct);

        await As(owner, c => c.Apps.UnpublishBot(team.teamId, bot.appId, ct).Ok());

        var unlisted = await directory.SearchBots(spaceId, username, ct);
        var lifecycle = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());

        Assert.Multiple(() =>
        {
            Assert.That(hidden, Is.Empty, "a bot still in development is listed in the directory");
            Assert.That((refusedDev as FailedInstallBot)?.error, Is.EqualTo(InstallBotError.NOT_FOUND));

            Assert.That(found.Select(b => b.appId), Is.EqualTo(new[] { bot.appId }),
                "a published bot is not found by its username, trimmed and in any case");
            Assert.That(found.Single().username, Is.EqualTo(username));
            Assert.That(details.name, Is.EqualTo("Listed Bot"));
            Assert.That(details.teamName, Is.EqualTo(team.name));
            Assert.That(details.isPublic, Is.True);
            Assert.That(details.maxSpaces, Is.EqualTo(5));
            Assert.That(blank, Is.Empty);

            Assert.That((twice as FailedInstallBot)?.error, Is.EqualTo(InstallBotError.ALREADY_INSTALLED));

            Assert.That(unlisted, Is.Empty, "an unpublished bot is still listed");
            Assert.That(lifecycle.botDetails!.lifecycleState, Is.EqualTo(AppLifecycle.Development));
        });

        Assert.That(async () => await directory.GetBotDetails(spaceId, bot.appId, ct), Throws.Exception,
            "an unpublished bot's page is still served");
    }

    /// <summary>
    /// A regenerated token works at once, and the one it replaced stops working at once.
    /// </summary>
    /// <remarks>
    /// Regenerating is what a developer does after a token leaks. The Bot API's authentication handler
    /// caches a resolved token for minutes, so unless the regeneration drops that entry the leaked
    /// token keeps authenticating for as long as the cache says — which is exactly the window the
    /// developer was trying to close.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_regenerated_token_replaces_the_old_one_at_once(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "rotate");
        var bot   = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Rotating Bot", BotUsername("rotate"), ct).Ok());
        var old   = bot.botDetails!.botToken;

        Assert.That(await BotGetMeAsync(old, ct), Is.EqualTo(HttpStatusCode.OK), "premise: the first token works");

        var fresh = await As(owner, c => c.Apps.RegenerateBotToken(team.teamId, bot.appId, ct).Ok());
        var shown = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());

        var oldStatus   = await BotGetMeAsync(old, ct);
        var freshStatus = await BotGetMeAsync(fresh, ct);

        Assert.Multiple(() =>
        {
            Assert.That(fresh, Is.Not.EqualTo(old));
            Assert.That(fresh, Does.StartWith(TokenPrefixOf(bot.appId) + ":"));
            Assert.That(shown.botDetails!.botToken, Is.EqualTo(fresh));
            Assert.That(freshStatus, Is.EqualTo(HttpStatusCode.OK), "the regenerated token was refused");
            Assert.That(oldStatus, Is.EqualTo(HttpStatusCode.Unauthorized),
                "the token that was regenerated away still authenticates");
        });
    }

    /// <summary>
    /// A bot its team suspends is refused by the Bot API at once, even with a token it used a moment ago.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_suspended_bot_is_refused_at_once(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "suspend");
        var bot   = await CreatePublishedBotAsync(owner, team.teamId, "susp");
        var token = bot.botDetails!.botToken;

        Assert.That(await BotGetMeAsync(token, ct), Is.EqualTo(HttpStatusCode.OK), "premise: the bot is running");

        await As(owner, c => c.Apps.SuspendBot(team.teamId, bot.appId, ct).Ok());

        var suspended = await BotGetMeAsync(token, ct);
        var state     = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());

        Assert.Multiple(() =>
        {
            Assert.That(state.botDetails!.lifecycleState, Is.EqualTo(AppLifecycle.Suspended));
            Assert.That(suspended, Is.EqualTo(HttpStatusCode.Unauthorized), "a suspended bot kept using the Bot API");
        });
    }

    /// <summary>
    /// Raising a bot's entitlements asks every space it is in for approval, and the OAuth switch is saved.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Raising_entitlements_asks_installed_spaces_for_approval(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var admin = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "entitle");
        var bot   = await CreatePublishedBotAsync(owner, team.teamId, "entitle");

        var (spaceId, _) = await CreateSpaceWithChannelAsync(admin, "Entitled", ct);
        await InstallAsync(admin, spaceId, bot.appId, ct);

        var directory = admin.Client.ForService<IBotManagementInteraction>(FactoryAsp.Services);
        var before    = (await directory.GetInstalledBots(spaceId, ct)).Single(b => b.appId == bot.appId);

        var raised = (ulong)(before.requiredEntitlements | ArgonEntitlement.ManageChannels);

        await As(owner, c => c.Apps.UpdateBotEntitlements(team.teamId, bot.appId, raised, ct).Ok());
        await As(owner, c => c.Apps.SetBotOAuth(team.teamId, bot.appId, false, ct).Ok());

        var saved   = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());
        var pending = (await directory.GetInstalledBots(spaceId, ct)).Single(b => b.appId == bot.appId);
        var approve = await directory.ApproveBotEntitlements(spaceId, bot.appId, ct);
        var settled = (await directory.GetInstalledBots(spaceId, ct)).Single(b => b.appId == bot.appId);

        Assert.Multiple(() =>
        {
            Assert.That(before.pendingApproval, Is.False);
            Assert.That(saved.botDetails!.requiredEntitlements, Is.EqualTo(raised));
            Assert.That(saved.botDetails.requiresOAuth2, Is.False);
            Assert.That(pending.pendingApproval, Is.True, "a space was not asked to approve the bot's new entitlements");
            Assert.That((ulong)pending.requiredEntitlements, Is.EqualTo(raised));
            Assert.That(approve, Is.InstanceOf<SuccessApproval>());
            Assert.That(settled.pendingApproval, Is.False);
        });
    }

    // ── internal apps and the sign-in policy ─────────────────────────────────────────────────

    /// <summary>
    /// A verified bot whose own account carries the staff badge is offered the internal scopes,
    /// whatever other badges that account also carries.
    /// </summary>
    /// <remarks>
    /// Badges accumulate — the inventory grants them — so "has the staff badge" is a membership test,
    /// which is how the identity server's own user-info handler reads it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_staff_bot_is_offered_the_internal_scopes_whatever_else_it_wears(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "staffbot");
        var bot   = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Staff Bot", BotUsername("staff"), ct).Ok());

        Assert.That((await Operators.SetBotVerifiedAsync(bot.appId, true, ct)).success, Is.True);

        await SetBadgesAsync(bot.appId, ["staff"], ct);
        var staffOnly = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());

        await SetBadgesAsync(bot.appId, ["staff", "early_supporter"], ct);
        var staffAndMore = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());

        await As(owner, c => c.Apps.UpdateScope(team.teamId, bot.appId, new ScopeKeyValue(true, "internal.read", false), ct).Ok());
        await As(owner, c => c.Apps.UpdateScope(team.teamId, bot.appId, new ScopeKeyValue(true, "offline_access", false), ct).Ok());
        var credentials = await Policy.GetCredentialsForBotAsync(bot.clientId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(staffOnly.requiredScopes.Select(s => s.key), Does.Contain("internal.read"));
            Assert.That(staffOnly.requiredScopes.Single(s => s.key == "offline_access").isLocked, Is.False,
                "verification did not unlock offline_access");
            Assert.That(staffAndMore.requiredScopes.Select(s => s.key), Does.Contain("internal.read"),
                "a second badge on a staff bot's account took its internal scopes away");
            Assert.That(credentials!.scopes, Does.Contain("internal.read"));
            Assert.That(credentials.IsAllowedRefreshToken, Is.True);
        });
    }

    /// <summary>
    /// An internal app admits only team members with a staff mailbox; an unapproved app only its own
    /// team; a published bot anyone; an unknown client nobody.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task The_sign_in_policy_follows_what_kind_of_app_it_is(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var staffer  = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "policy");

        await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, staffer.Credentials.username, ct));
        await As(staffer, c => c.Teams.AcceptTeamInvite(team.teamId, ct));
        await SetEmailAsync(staffer.UserId, $"staff-{Guid.NewGuid():N}@argon.gl", ct);

        var tool = await As(owner, c => c.Apps.CreateClientApp(team.teamId, "Internal Tool", ClientAppPlatform.WebBased, ct).Ok());
        Assert.That((await Operators.SetAppInternalAsync(tool.appId, true, ct)).success, Is.True);

        var bot     = await CreatePublishedBotAsync(owner, team.teamId, "policy");
        var unreviewed = await As(owner, c => c.Apps.CreateClientApp(team.teamId, "Unreviewed App", ClientAppPlatform.WebBased, ct).Ok());

        var toolStranger = await Policy.CanBeLoginForAppAsync(tool.clientId, stranger.UserId, ct);
        var toolOwner    = await Policy.CanBeLoginForAppAsync(tool.clientId, owner.UserId, ct);
        var toolStaffer  = await Policy.CanBeLoginForAppAsync(tool.clientId, staffer.UserId, ct);
        var botStranger  = await Policy.CanBeLoginForAppAsync(bot.clientId, stranger.UserId, ct);
        var unknown      = await Policy.CanBeLoginForAppAsync("no-such-client", owner.UserId, ct);
        var privOwner    = await Policy.CanBeLoginForAppAsync(unreviewed.clientId, owner.UserId, ct);
        var privStranger = await Policy.CanBeLoginForAppAsync(unreviewed.clientId, stranger.UserId, ct);

        var consent     = await Policy.GetOAuthAppInfoAsync(bot.clientId, new List<string> { "openid", "user.read" }, ct);
        var toolConsent = await Policy.GetOAuthAppInfoAsync(tool.clientId, new List<string> { "openid" }, ct);
        var noConsent   = await Policy.GetOAuthAppInfoAsync("no-such-client", new List<string> { "openid" }, ct);
        var noSecretFor = await Policy.GetCredentialsForBotAsync("no-such-client", ct);

        Assert.Multiple(() =>
        {
            Assert.That(toolStranger, Is.EqualTo(new LoginAllowedResult(false, "Internal apps require team membership")));
            Assert.That(toolOwner.IsAllowed, Is.False, "a team member without a staff mailbox reached an internal app");
            Assert.That(toolOwner.Reason, Does.Contain("security requirements"));
            Assert.That(toolStaffer, Is.EqualTo(new LoginAllowedResult(true, null)));
            Assert.That(botStranger, Is.EqualTo(new LoginAllowedResult(true, null)), "a published bot refused an ordinary user");
            Assert.That(unknown, Is.EqualTo(new LoginAllowedResult(false, "App not found")));
            Assert.That(privOwner, Is.EqualTo(new LoginAllowedResult(true, null)));
            Assert.That(privStranger, Is.EqualTo(new LoginAllowedResult(false, "Unapproved apps require team membership")),
                "an app nobody has reviewed let an outsider sign in");

            Assert.That(consent, Is.Not.Null);
            Assert.That(consent!.AppId, Is.EqualTo(bot.appId));
            Assert.That(consent.DeveloperName, Is.EqualTo(team.name));
            Assert.That(consent.RequestedScopes, Is.EqualTo(new[] { "openid", "user.read" }));
            Assert.That(toolConsent!.AppName, Is.EqualTo("Internal Tool"));
            Assert.That(toolConsent.DeveloperName, Is.EqualTo(team.name));
            Assert.That(toolConsent.IsInternalApp, Is.True, "the consent screen does not say the app is internal");
            Assert.That(noConsent, Is.Null);
            Assert.That(noSecretFor, Is.Null);
        });
    }

    /// <summary>
    /// An internal client app that is verified is offered the internal scopes and refresh tokens; an app
    /// with no secret has no credentials to give the token endpoint at all.
    /// </summary>
    /// <remarks>
    /// Client-app verification and an empty secret have no console or operator flow, so both are seeded.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_verified_internal_client_app_is_offered_the_internal_scopes(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "internal");
        var tool  = await As(owner, c => c.Apps.CreateClientApp(team.teamId, "Verified Tool", ClientAppPlatform.WebBased, ct).Ok());

        Assert.That((await Operators.SetAppInternalAsync(tool.appId, true, ct)).success, Is.True);

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            await db.AppClientEntities
               .Where(a => a.AppId == tool.appId)
               .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsVerified, true), ct);
        }

        await As(owner, c => c.Apps.UpdateScope(team.teamId, tool.appId, new ScopeKeyValue(true, "infrastructure.write", false), ct).Ok());

        var details     = await As(owner, c => c.Apps.GetAppDetails(team.teamId, tool.appId, ct).Ok());
        var credentials = await Policy.GetCredentialsForBotAsync(tool.clientId, ct);

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            await db.AppEntities
               .Where(a => a.AppId == tool.appId)
               .ExecuteUpdateAsync(s => s.SetProperty(a => a.ClientSecret, string.Empty), ct);
        }

        var secretless = await Policy.GetCredentialsForBotAsync(tool.clientId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(details.clientAppDetails!.isInternalApp, Is.True);
            Assert.That(details.clientAppDetails.isVerfied, Is.True);
            Assert.That(details.clientAppDetails.allowedDevelopmentRegenerateCoockies, Is.True);
            Assert.That(details.requiredScopes.Select(s => s.key), Is.SupersetOf(new[] { "role", "internal.read", "argx.write" }));
            Assert.That(credentials!.scopes, Is.EqualTo(new[] { "infrastructure.write" }));
            Assert.That(secretless, Is.Null, "an app with no client secret was handed credentials");
        });
    }

    // ── who may lift a suspension ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A bot an operator suspended stays suspended until an operator lifts it, and the suspension stops
    /// the bot on the Bot API at once.
    /// </summary>
    /// <remarks>
    /// The lifecycle has one Suspended value for two different things — a team switching its own bot
    /// off, and an operator taking it off the platform — so <c>BotEntity.SuspendedBy</c> records which
    /// it was. The console refuses every lifecycle change on an operator's suspension with
    /// <c>SUSPENDED_BY_OPERATOR</c>; asking to suspend it again changes nothing, the operator's claim
    /// included.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_operator_suspension_cannot_be_lifted_by_the_team(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "opsusp");
        var bot   = await CreatePublishedBotAsync(owner, team.teamId, "opsusp");
        var token = bot.botDetails!.botToken;

        Assert.That(await BotGetMeAsync(token, ct), Is.EqualTo(HttpStatusCode.OK), "premise: the bot is running");

        Assert.That((await Operators.SetBotLifecycleStateAsync(bot.appId, AdminBotLifecycleState.Suspended, ct)).success, Is.True);

        var stopped = await BotGetMeAsync(token, ct);

        var refusals = new Dictionary<string, Func<DevConsole, Task<IAppManagementResult>>>
        {
            ["PublishBot"]   = c => c.Apps.PublishBot(team.teamId, bot.appId, ct),
            ["UnpublishBot"] = c => c.Apps.UnpublishBot(team.teamId, bot.appId, ct)
        };

        foreach (var (name, call) in refusals)
            Assert.That(await As(owner, call), Is.EqualTo(new FailedAppManagement(AppManagementError.SUSPENDED_BY_OPERATOR)),
                $"{name} was allowed on a bot an operator suspended");

        await As(owner, c => c.Apps.SuspendBot(team.teamId, bot.appId, ct).Ok());

        var state = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());
        var who   = await SuspendedByAsync(bot.appId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(stopped, Is.EqualTo(HttpStatusCode.Unauthorized),
                "a bot an operator suspended kept using the Bot API on a cached token");
            Assert.That(state.botDetails!.lifecycleState, Is.EqualTo(AppLifecycle.Suspended),
                "the team republished a bot an operator had suspended");
            Assert.That(who, Is.EqualTo(BotSuspendedBy.Operator),
                "the team suspending it again took the suspension over from the operator");
        });

        Assert.That((await Operators.SetBotLifecycleStateAsync(bot.appId, AdminBotLifecycleState.Published, ct)).success, Is.True);

        var lifted = await BotGetMeAsync(token, ct);

        await As(owner, c => c.Apps.UnpublishBot(team.teamId, bot.appId, ct).Ok());
        var afterLift = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());

        Assert.Multiple(async () =>
        {
            Assert.That(lifted, Is.EqualTo(HttpStatusCode.OK), "a lifted suspension still refused the bot's token");
            Assert.That(afterLift.botDetails!.lifecycleState, Is.EqualTo(AppLifecycle.Development),
                "the team could not manage its bot after the operator lifted the suspension");
            Assert.That(await SuspendedByAsync(bot.appId, ct), Is.EqualTo(BotSuspendedBy.None));
        });
    }

    /// <summary>
    /// A team lifts a suspension it imposed itself — unless an operator has since made it theirs.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_team_lifts_its_own_suspension_but_not_one_an_operator_took_over(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "selfsusp");
        var bot   = await CreatePublishedBotAsync(owner, team.teamId, "selfsusp");

        await As(owner, c => c.Apps.SuspendBot(team.teamId, bot.appId, ct).Ok());
        var ownSuspension = await SuspendedByAsync(bot.appId, ct);

        await As(owner, c => c.Apps.PublishBot(team.teamId, bot.appId, ct).Ok());
        var republished = await As(owner, c => c.Apps.GetAppDetails(team.teamId, bot.appId, ct).Ok());

        await As(owner, c => c.Apps.SuspendBot(team.teamId, bot.appId, ct).Ok());
        Assert.That((await Operators.SetBotLifecycleStateAsync(bot.appId, AdminBotLifecycleState.Suspended, ct)).success, Is.True);

        var takenOver = await SuspendedByAsync(bot.appId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(ownSuspension, Is.EqualTo(BotSuspendedBy.Team));
            Assert.That(republished.botDetails!.lifecycleState, Is.EqualTo(AppLifecycle.Published),
                "a team could not lift a suspension it imposed itself");
            Assert.That(takenOver, Is.EqualTo(BotSuspendedBy.Operator));
        });

        Assert.That(await As(owner, c => c.Apps.PublishBot(team.teamId, bot.appId, ct)),
            Is.EqualTo(new FailedAppManagement(AppManagementError.SUSPENDED_BY_OPERATOR)),
            "the team lifted a suspension an operator had taken over");
    }

    /// <summary>
    /// A suspension from before anyone recorded who imposed it is an operator's to lift.
    /// </summary>
    /// <remarks>
    /// Rows suspended before <c>SuspendedBy</c> existed read <c>None</c>; the console has no way to
    /// tell a team's from an operator's, so it assumes the one it must not undo. Seeded, because no
    /// flow produces such a row any more.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_suspension_of_unknown_origin_is_left_to_an_operator(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "legacy");
        var bot   = await CreatePublishedBotAsync(owner, team.teamId, "legacy");

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            await db.BotEntities
               .Where(b => b.AppId == bot.appId)
               .ExecuteUpdateAsync(s => s
                   .SetProperty(b => b.LifecycleState, Argon.Core.Entities.Data.BotLifecycleState.Suspended)
                   .SetProperty(b => b.SuspendedBy, BotSuspendedBy.None), ct);
        }

        Assert.That(await As(owner, c => c.Apps.PublishBot(team.teamId, bot.appId, ct)),
            Is.EqualTo(new FailedAppManagement(AppManagementError.SUSPENDED_BY_OPERATOR)),
            "the team lifted a suspension nobody can say it imposed");
    }

    // ── what a bot may be called ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A bot username obeys the same shape as a person's: letters, digits and underscores, four to
    /// thirty-two of them.
    /// </summary>
    /// <remarks>
    /// Bots share the username namespace and the member list with people, so a bot must not be able to
    /// take a name no person could — with spaces, slashes, or letters from another script that render
    /// like someone else's. The console is told why with <c>INVALID_FORMAT</c>; the grain refuses on
    /// its own too, for a caller that skipped the check.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_bot_username_is_held_to_the_same_shape_as_a_persons(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "shape");

        string[] malformed =
        [
            "a b bot",
            "../../bot",
            "dot.bot",
            "\u0430dmin_" + Guid.NewGuid().ToString("N")[..6] + "bot",
            new string('x', 30) + "bot",
            "bot"
        ];

        foreach (var name in malformed)
        {
            var verdict = await As(owner, c => c.Apps.CheckUsernameForBot(team.teamId, name, ct));
            Assert.That(verdict, Is.EqualTo(CheckBotUsernameValid.INVALID_FORMAT), $"'{name}' was not refused as badly formed");

            Assert.That(await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Malformed", name, ct)),
                Is.EqualTo(new FailedAppDetails(AppManagementError.INVALID_USERNAME)), $"the console created a bot called '{name}'");
            Assert.That(async () => await Teams.CreateBotAppAsync(team.teamId, "Malformed", name, ct),
                Throws.InstanceOf<InvalidOperationException>(), $"the grain created a bot called '{name}'");
        }

        var edge     = "Edge_" + Guid.NewGuid().ToString("N")[..24] + "Bot";
        var accepted = await As(owner, c => c.Apps.CheckUsernameForBot(team.teamId, edge, ct));
        var created  = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Longest name", edge, ct).Ok());
        var details  = await As(owner, c => c.Teams.GetTeamDetails(team.teamId, ct).Ok());

        Assert.Multiple(() =>
        {
            Assert.That(edge, Has.Length.EqualTo(32), "premise: the name sits on the length limit");
            Assert.That(accepted, Is.EqualTo(CheckBotUsernameValid.OK), "a well-formed name at the limit was refused");
            Assert.That(details.apps.Select(a => a.appId), Is.EqualTo(new[] { created.appId }),
                "a refused name left an application behind");
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The <c>{hex(reversed app id)}</c> half of a token the authentication handler parses.</summary>
    private static string TokenPrefixOf(Guid appId)
    {
        Span<byte> bytes = stackalloc byte[16];
        appId.TryWriteBytes(bytes);
        bytes.Reverse();
        return Convert.ToHexString(bytes);
    }

    /// <summary>The badges on a bot's own account — the inventory is what grants them in production.</summary>
    private static async Task SetBadgesAsync(Guid userId, List<string> badges, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var profile = await db.UserProfiles.FirstAsync(p => p.UserId == userId, ct);
        profile.Badges = badges;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Who the database says suspended the bot.</summary>
    private static async Task<BotSuspendedBy> SuspendedByAsync(Guid appId, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        return await db.BotEntities.Where(b => b.AppId == appId).Select(b => b.SuspendedBy).SingleAsync(ct);
    }

    /// <summary>A mailbox the account never verified — the staff rule reads only the address.</summary>
    private static async Task SetEmailAsync(Guid userId, string email, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var changed = await db.Users
           .Where(u => u.Id == userId)
           .ExecuteUpdateAsync(s => s.SetProperty(u => u.Email, email), ct);

        Assert.That(changed, Is.EqualTo(1));
    }
}
