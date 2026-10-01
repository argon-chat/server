namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Integrations.Connections;
using ArgonContracts;
using ConsoleContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The operator console's view of linked accounts: listing them, suspending one (hidden, not
/// linkable over, poller stopped) and lifting it, and force-unlinking with the grant revoked at
/// the provider — each with its audit line.
/// </summary>
[TestFixture]
public class AdminConnectionsTests : AdminTestBase
{
    protected override Guid SystemOperatorId  => Guid.Parse("00000000-0000-0000-0000-0000000ad401");
    protected override Guid RegularOperatorId => Guid.Parse("00000000-0000-0000-0000-0000000ad402");

    private static IConnectionsInteraction ConnectionsOf(TestUserSession session)
        => session.Client.ForService<IConnectionsInteraction>(ChannelTestKit.Services);

    /// <summary>A GitHub connection written straight to the table, its token sealed the way a link seals it.</summary>
    private async Task<(string ExternalId, string Token)> SeedGitHubAsync(Guid userId, CancellationToken ct)
    {
        var sealer     = FactoryAsp.Services.GetRequiredService<TokenSealer>();
        var id         = Guid.CreateVersion7();
        var externalId = FactoryAsp.Services.GetRequiredService<FakeConnectionsApi>().NextOutsiderId().ToString();
        var token      = $"gho_seeded_{Guid.NewGuid():N}";
        var now        = DateTimeOffset.UtcNow;

        await using var db = await NewDbAsync(ct);

        db.UserConnections.Add(new UserConnectionEntity
        {
            Id              = id,
            UserId          = userId,
            Provider        = ConnectionProvider.GITHUB,
            ExternalId      = externalId,
            ExternalName    = "seeded",
            ExternalUrl     = "https://github.com/seeded",
            SealedTokens    = sealer.Seal(new ProviderToken(token, null, null, ""), id, ConnectionProvider.GITHUB),
            TokenKeyVersion = sealer.CurrentVersion,
            CreatedAt       = now,
            UpdatedAt       = now
        });

        await db.SaveChangesAsync(ct);

        return (externalId, token);
    }

    [Test, CancelAfter(120_000)]
    public async Task An_operator_lists_and_suspends_a_connection_and_the_user_cannot_link_over_it(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var (externalId, _) = await SeedGitHubAsync(person.UserId, ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var listed = (await admin.GetUserConnections(person.UserId, ct)).connections.Values;

        Assert.Multiple(() =>
        {
            Assert.That(listed.Select(c => (c.provider, c.externalId, c.status)),
                Is.EqualTo(new[] { (ConnectionProvider.GITHUB, externalId, ConnectionStatus.ACTIVE) }));
            Assert.That(listed.Single().name, Is.EqualTo("seeded"));
        });

        var suspended = await admin.SetConnectionSuspended(person.UserId, ConnectionProvider.GITHUB, true, ct);

        Assert.That(suspended.success, Is.True, suspended.error);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await admin.GetUserConnections(person.UserId, ct)).connections.Values.Single().status, Is.EqualTo(ConnectionStatus.SUSPENDED));
            Assert.That((await person.Users.GetMyProfile(ct)).connections?.Values, Is.Empty, "a suspended connection still shows on the card");
            Assert.That(await ConnectionsOf(person).BeginConnect(ConnectionProvider.GITHUB, ConnectReturnKind.WEB, true, ct),
                Is.EqualTo(new FailedBeginConnect(BeginConnectError.CONNECTION_SUSPENDED)), "the user re-linked over a suspension");
            Assert.That(await AuditAsync(admin, "SuspendConnection", person.UserId.ToString(), ct), Has.Count.EqualTo(1));
        });

        var lifted = await admin.SetConnectionSuspended(person.UserId, ConnectionProvider.GITHUB, false, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(lifted.success, Is.True, lifted.error);
            Assert.That((await admin.GetUserConnections(person.UserId, ct)).connections.Values.Single().status, Is.EqualTo(ConnectionStatus.ACTIVE),
                "a lifted suspension with its token intact is active again");
            Assert.That((await person.Users.GetMyProfile(ct)).connections?.Values, Has.Count.EqualTo(1));
            Assert.That(await AuditAsync(admin, "UnsuspendConnection", person.UserId.ToString(), ct), Has.Count.EqualTo(1));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_force_unlink_revokes_at_the_provider_and_is_audited(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);
        var (_, token) = await SeedGitHubAsync(person.UserId, ct);
        var fake       = FactoryAsp.Services.GetRequiredService<FakeConnectionsApi>();

        var (scope, admin) = Admin();
        await using var _ = scope;

        var unlinked = await admin.ForceUnlinkConnection(person.UserId, ConnectionProvider.GITHUB, ct);
        var again    = await admin.ForceUnlinkConnection(person.UserId, ConnectionProvider.GITHUB, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(unlinked.success, Is.True, unlinked.error);
            Assert.That(again.success, Is.False, "there was nothing left to unlink");
            Assert.That(fake.RevokedGitHubTokens, Does.Contain(token), "the grant was not revoked at GitHub");
            Assert.That((await admin.GetUserConnections(person.UserId, ct)).connections.Values, Is.Empty);
            Assert.That(await AuditAsync(admin, "ForceUnlinkConnection", person.UserId.ToString(), ct), Has.Count.EqualTo(1),
                "a refused second unlink must not leave an audit line");
        });

        await using var db = await NewDbAsync(ct);

        Assert.That(await db.UserConnections.AnyAsync(c => c.UserId == person.UserId, ct), Is.False);
    }

    [Test, CancelAfter(120_000)]
    public async Task Acting_on_a_connection_that_does_not_exist_fails_without_an_audit_line(CancellationToken ct = default)
    {
        var person = await CreateSessionAsync(ct);

        var (scope, admin) = Admin();
        await using var _ = scope;

        var suspended = await admin.SetConnectionSuspended(person.UserId, ConnectionProvider.STEAM, true, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(suspended.success, Is.False);
            Assert.That((await admin.GetUserConnections(person.UserId, ct)).connections.Values, Is.Empty);
            Assert.That(await AuditAsync(admin, "SuspendConnection", person.UserId.ToString(), ct), Is.Empty);
        });
    }
}
