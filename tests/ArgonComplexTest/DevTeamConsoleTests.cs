namespace ArgonComplexTest.Tests;

using AccountContracts;
using Argon.Core.Entities.Data;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Account;
using Microsoft.EntityFrameworkCore;
using static DevTeamsHarness;

/// <summary>
/// Dev teams as the developer console shows them: who is in a team, who has been asked, and who may
/// look.
/// </summary>
/// <remarks>
/// <para>Driven through <c>ITeamConsole</c> rather than against <c>IDevTeamsGrain</c>, because the
/// console is where a membership becomes a permission: <c>TeamAccessChecker</c> stands between every
/// team-scoped call and the grain, and caches its answers. A grain-level test would pass while the
/// gate in front of it refused the member it had just admitted.</para>
///
/// <para>The grain is called directly only where the console cannot say the thing being tested — an
/// invite with a lifetime of seconds rather than the console's fixed day.</para>
/// </remarks>
[TestFixture]
public class DevTeamConsoleTests : TestBase
{
    private IDevTeamsGrain Teams => GetGrainFactory().GetGrain<IDevTeamsGrain>(Guid.Empty);

    /// <summary>
    /// A team lists its creator as its owner, and the team list counts the apps the team owns.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_new_team_lists_its_creator_as_owner_and_counts_its_apps(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "own");

        var empty = await As(owner, c => c.Teams.GetMyTeams(ct));

        var client = await As(owner, c => c.Apps.CreateClientApp(team.teamId, "Console Client", ClientAppPlatform.LinuxDesktop, ct));
        var bot    = await As(owner, c => c.Apps.CreateBotApp(team.teamId, "Console Bot", BotUsername("own"), ct));

        var listed  = await As(owner, c => c.Teams.GetMyTeams(ct));
        var details = await As(owner, c => c.Teams.GetTeamDetails(team.teamId, ct));

        var member = details.members.Single();

        Assert.Multiple(() =>
        {
            Assert.That(team.ownerId, Is.EqualTo(owner.UserId));
            Assert.That(empty.Single(t => t.teamId == team.teamId).appsCount, Is.Zero);
            Assert.That(listed.Single(t => t.teamId == team.teamId).appsCount, Is.EqualTo(2));
            Assert.That(listed.Single(t => t.teamId == team.teamId).name, Is.EqualTo(team.name));

            Assert.That(details.ownerId, Is.EqualTo(owner.UserId));
            Assert.That(member.user.userId, Is.EqualTo(owner.UserId));
            Assert.That(member.user.username, Is.EqualTo(owner.Credentials.username));
            Assert.That(member.isOwner, Is.True);
            Assert.That(member.isPending, Is.False);

            Assert.That(details.apps.Select(a => (a.appId, a.kind, a.clientId)), Is.EquivalentTo(new[]
            {
                (client.appId, AppKind.ClientApp, client.clientId),
                (bot.appId, AppKind.BotApp, bot.clientId)
            }));
        });
    }

    /// <summary>
    /// Nobody outside a team can read it, list its invites, invite into it or create apps in it.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Everything_team_scoped_is_refused_to_a_stranger(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var team     = await CreateTeamAsync(owner, "closed");
        var app      = await As(owner, c => c.Apps.CreateClientApp(team.teamId, "Closed", ClientAppPlatform.WebBased, ct));

        var refused = new Dictionary<string, Func<DevConsole, Task>>
        {
            ["GetTeamDetails"]   = c => c.Teams.GetTeamDetails(team.teamId, ct),
            ["GetTeamInvites"]   = c => c.Teams.GetTeamInvites(team.teamId, ct),
            ["InviteUserToTeam"] = c => c.Teams.InviteUserToTeam(team.teamId, stranger.Credentials.username, ct),
            ["CreateClientApp"]  = c => c.Apps.CreateClientApp(team.teamId, "Mine now", ClientAppPlatform.WebBased, ct),
            ["CreateBotApp"]     = c => c.Apps.CreateBotApp(team.teamId, "Mine now", BotUsername("steal"), ct),
            ["GetAppDetails"]    = c => c.Apps.GetAppDetails(team.teamId, app.appId, ct),
            ["UpdateScope"]      = c => c.Apps.UpdateScope(team.teamId, app.appId, new ScopeKeyValue(true, "email", false), ct),
            ["RemoveRedirect"]   = c => c.Apps.RemoveRedirect(team.teamId, app.appId, "https://x.test.local/cb", ct)
        };

        foreach (var (name, call) in refused)
        {
            Assert.That(async () => await As(stranger, call), Throws.InstanceOf<UnauthorizedAccessException>(),
                $"{name} answered a caller who is not in the team");
        }

        var theirs = await As(stranger, c => c.Teams.GetMyTeams(ct));
        Assert.That(theirs.Select(t => t.teamId), Does.Not.Contain(team.teamId));
    }

    /// <summary>
    /// An invite is visible to the team and to the invitee until it is answered, and accepting it makes
    /// the invitee a member.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task An_invite_is_listed_on_both_sides_until_it_is_accepted(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var invitee = await CreateSessionAsync(ct);
        var team    = await CreateTeamAsync(owner, "invite");

        var sent = await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, invitee.Credentials.username.ToUpperInvariant(), ct));

        var teamSide    = await As(owner, c => c.Teams.GetTeamInvites(team.teamId, ct));
        var inviteeSide = await As(invitee, c => c.Teams.GetMyInvites(ct));

        var again   = await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, invitee.Credentials.username, ct));
        var nobody  = await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, $"nobody_{Guid.NewGuid():N}"[..24], ct));
        var ownSelf = await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, owner.Credentials.username, ct));

        Assert.Multiple(() =>
        {
            Assert.That(sent, Is.EqualTo(InviteUserError.OK), "a username is matched case-insensitively");
            Assert.That(again, Is.EqualTo(InviteUserError.ALREADY_INVITED));
            Assert.That(nobody, Is.EqualTo(InviteUserError.USER_NOT_FOUND));
            Assert.That(ownSelf, Is.EqualTo(InviteUserError.ALREADY_IN_TEAM));

            var listed = teamSide.Single();
            Assert.That(listed.from.userId, Is.EqualTo(owner.UserId));
            Assert.That(listed.to.userId, Is.EqualTo(invitee.UserId));
            Assert.That(listed.to.username, Is.EqualTo(invitee.Credentials.username));

            var mine = inviteeSide.Single();
            Assert.That(mine.team.teamId, Is.EqualTo(team.teamId));
            Assert.That(mine.team.name, Is.EqualTo(team.name));
            Assert.That(mine.from.userId, Is.EqualTo(owner.UserId));
        });

        await As(invitee, c => c.Teams.AcceptTeamInvite(team.teamId, ct));

        var afterTeam    = await As(owner, c => c.Teams.GetTeamInvites(team.teamId, ct));
        var afterInvitee = await As(invitee, c => c.Teams.GetMyInvites(ct));
        var details      = await As(owner, c => c.Teams.GetTeamDetails(team.teamId, ct));
        var theirTeams   = await As(invitee, c => c.Teams.GetMyTeams(ct));
        var reinvite     = await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, invitee.Credentials.username, ct));

        Assert.Multiple(() =>
        {
            Assert.That(afterTeam, Is.Empty, "an accepted invite is still listed as pending for the team");
            Assert.That(afterInvitee, Is.Empty, "an accepted invite is still listed for the invitee");
            Assert.That(details.members.Select(m => (m.user.userId, m.isOwner)),
                Is.EquivalentTo(new[] { (owner.UserId, true), (invitee.UserId, false) }));
            Assert.That(theirTeams.Select(t => t.teamId), Does.Contain(team.teamId));
            Assert.That(reinvite, Is.EqualTo(InviteUserError.ALREADY_IN_TEAM));
        });
    }

    /// <summary>
    /// A declined invite is gone for both sides, and the team can ask again.
    /// </summary>
    /// <remarks>
    /// Invites are keyed by team and invitee, one row per pair. Declining marks the row revoked rather
    /// than removing it, so a second invite has to reuse that row: adding a new one collides with the
    /// key and the console's "invite" button fails for that person for good.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_declined_invite_can_be_sent_again(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var invitee = await CreateSessionAsync(ct);
        var team    = await CreateTeamAsync(owner, "decline");

        Assert.That(await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, invitee.Credentials.username, ct)),
            Is.EqualTo(InviteUserError.OK));

        await As(invitee, c => c.Teams.DeclineTeamInvite(team.teamId, ct));

        var teamSide    = await As(owner, c => c.Teams.GetTeamInvites(team.teamId, ct));
        var inviteeSide = await As(invitee, c => c.Teams.GetMyInvites(ct));
        var joined      = await Teams.IsUserInTeamAsync(invitee.UserId, team.teamId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(teamSide, Is.Empty);
            Assert.That(inviteeSide, Is.Empty);
            Assert.That(joined, Is.False, "declining an invite made the invitee a member");
        });

        Assert.That(async () => await As(invitee, c => c.Teams.AcceptTeamInvite(team.teamId, ct)),
            Throws.InstanceOf<InvalidOperationException>(), "a declined invite could still be accepted");

        // Declining again, with nothing left to decline, is not an error.
        await As(invitee, c => c.Teams.DeclineTeamInvite(team.teamId, ct));

        var second = await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, invitee.Credentials.username, ct));
        var listed = await As(invitee, c => c.Teams.GetMyInvites(ct));

        Assert.Multiple(() =>
        {
            Assert.That(second, Is.EqualTo(InviteUserError.OK), "the team could not invite someone who once declined");
            Assert.That(listed.Select(i => i.team.teamId), Is.EqualTo(new[] { team.teamId }));
        });

        await As(invitee, c => c.Teams.AcceptTeamInvite(team.teamId, ct));

        Assert.That(await Teams.IsUserInTeamAsync(invitee.UserId, team.teamId, ct), Is.True,
            "the second invite could not be accepted");
    }

    /// <summary>
    /// An invite that has run out is gone: it cannot be accepted, and it does not stop the team from
    /// sending a fresh one.
    /// </summary>
    /// <remarks>
    /// Both lists already hide an expired invite, so a team that sees no pending invite and presses
    /// "invite" again has to get one — not <c>ALREADY_INVITED</c> for an invite nobody can see or use.
    /// The lifetime is seconds here, which only the grain can express; the console always asks for a day.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_expired_invite_does_not_block_a_new_one(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var invitee = await CreateSessionAsync(ct);
        var team    = await CreateTeamAsync(owner, "expire");

        Assert.That(await Teams.InviteUserToTeamAsync(team.teamId, owner.UserId, invitee.Credentials.username,
            TimeSpan.FromSeconds(1), ct), Is.EqualTo(InviteUserError.OK));

        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        var teamSide    = await As(owner, c => c.Teams.GetTeamInvites(team.teamId, ct));
        var inviteeSide = await As(invitee, c => c.Teams.GetMyInvites(ct));

        Assert.Multiple(() =>
        {
            Assert.That(teamSide, Is.Empty, "an expired invite is still listed for the team");
            Assert.That(inviteeSide, Is.Empty, "an expired invite is still listed for the invitee");
        });

        Assert.That(async () => await As(invitee, c => c.Teams.AcceptTeamInvite(team.teamId, ct)),
            Throws.InstanceOf<InvalidOperationException>(), "an expired invite was accepted");

        var fresh = await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, invitee.Credentials.username, ct));

        Assert.That(fresh, Is.EqualTo(InviteUserError.OK),
            "the team was told the user is already invited, by an invite that has expired");

        await As(invitee, c => c.Teams.AcceptTeamInvite(team.teamId, ct));

        Assert.That(await Teams.IsUserInTeamAsync(invitee.UserId, team.teamId, ct), Is.True);
    }

    /// <summary>
    /// A person who was refused a team before joining it is let in as soon as they join.
    /// </summary>
    /// <remarks>
    /// <c>TeamAccessChecker</c> caches its answer for <c>AccountConsole:AccessCacheTtl</c> — fifty
    /// minutes shipped. A cached "yes" going stale is written down as the price of the cache; a cached
    /// "no" is a different thing: it locks a person who opened the team's page from the invite out of
    /// the team they then accepted, for the better part of an hour.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Accepting_an_invite_opens_the_team_at_once(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var invitee = await CreateSessionAsync(ct);
        var team    = await CreateTeamAsync(owner, "gate");

        await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, invitee.Credentials.username, ct));

        Assert.That(async () => await As(invitee, c => c.Teams.GetTeamDetails(team.teamId, ct)),
            Throws.InstanceOf<UnauthorizedAccessException>(), "premise: an invitee is not yet a member");

        await As(invitee, c => c.Teams.AcceptTeamInvite(team.teamId, ct));

        var seen = await As(invitee, c => c.Teams.GetTeamDetails(team.teamId, ct));

        Assert.That(seen.members.Select(m => m.user.userId), Does.Contain(invitee.UserId));
    }

    /// <summary>
    /// The owner gate lets the owner through and nobody else, members included.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Only_the_owner_passes_the_owner_gate(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);
        var team   = await CreateTeamAsync(owner, "ownergate");

        await As(owner, c => c.Teams.InviteUserToTeam(team.teamId, member.Credentials.username, ct));
        await As(member, c => c.Teams.AcceptTeamInvite(team.teamId, ct));

        await As(owner, c => c.Access.EnsureTeamOwnerAsync(owner.UserId, team.teamId, ct));
        await As(member, c => c.Access.EnsureTeamMemberAsync(member.UserId, team.teamId, ct));

        Assert.That(async () => await As(member, c => c.Access.EnsureTeamOwnerAsync(member.UserId, team.teamId, ct)),
            Throws.InstanceOf<UnauthorizedAccessException>(), "a member who does not own the team passed the owner gate");
    }

    /// <summary>
    /// An application of a kind the console has no view for is listed with its kind, and refused in
    /// detail rather than described as something it is not.
    /// </summary>
    /// <remarks>
    /// <c>DevAppType.WebApp</c> has no table of its own and no console flow creates one, so the row is
    /// seeded; it is the only way to reach the third arm of the kind mapping.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_app_of_an_unsupported_kind_is_listed_but_not_described(CancellationToken ct = default)
    {
        var owner = await CreateSessionAsync(ct);
        var team  = await CreateTeamAsync(owner, "webapp");
        var appId = Guid.NewGuid();

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            db.AppEntities.Add(new DevAppEntity
            {
                AppId        = appId,
                TeamId       = team.teamId,
                Name         = "Seeded Web App",
                ClientId     = $"webapp-{appId:N}",
                ClientSecret = Guid.NewGuid().ToString("N"),
                AppType      = DevAppType.WebApp,
                CreatedAt    = DateTimeOffset.UtcNow,
                UpdatedAt    = DateTimeOffset.UtcNow
            });

            await db.SaveChangesAsync(ct);
        }

        var details = await As(owner, c => c.Teams.GetTeamDetails(team.teamId, ct));

        Assert.That(details.apps.Single(a => a.appId == appId).kind, Is.EqualTo(AppKind.WebApp));
        Assert.That(async () => await As(owner, c => c.Apps.GetAppDetails(team.teamId, appId, ct)),
            Throws.InstanceOf<NotSupportedException>());
    }
}
