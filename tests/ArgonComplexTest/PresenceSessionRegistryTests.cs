namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Auth;
using Argon.Features.EF;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime.client;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Data.Common;

/// <summary>
/// The devices screen lists every signed-in device, connected or not, from the sessions registry
/// (a database row per credential, written through the Redis transit), and can sign any of them out.
/// </summary>
[TestFixture]
public class PresenceSessionRegistryTests : TestBase
{
    private ISessionRegistryFlushGrain Flusher
        => GetGrainFactory().GetGrain<ISessionRegistryFlushGrain>(ISessionRegistryFlushGrain.SingletonId);

    private ISessionRegistryTransit Transit
        => FactoryAsp.Services.GetRequiredService<ISessionRegistryTransit>();

    private Task<ApplicationDbContext> NewDbAsync(CancellationToken ct)
        => FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync(ct);

    [Test, CancelAfter(120_000)]
    public async Task A_device_that_is_not_connected_is_listed_offline_and_can_be_signed_out(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        // The phone never touches the hub, so presence knows nothing about it.
        await using var onLaptop = await RealtimeClient.ConnectAsync(laptop, ct);

        var listed = await Poll.ForValueAsync(
            async () => (await laptop.Security.GetSessions(ct)).Values.ToArray(),
            rows => rows.Any(x => x.sessionId == phone.Session.SessionId)
                    && rows.Any(x => x.sessionId == laptop.SessionId && x.online),
            PresenceWaits.Settle, ct: ct);

        var phoneRow  = listed.Single(x => x.sessionId == phone.Session.SessionId);
        var laptopRow = listed.Single(x => x.sessionId == laptop.SessionId);

        Assert.Multiple(() =>
        {
            Assert.That(phoneRow.online, Is.False, "a device that never connected reads as online");
            Assert.That(phoneRow.isCurrent, Is.False, "another device reads as the caller's own");
            Assert.That(laptopRow.isCurrent, Is.True, "the caller's own row is not marked current");
            Assert.That(listed[0].sessionId, Is.EqualTo(laptop.SessionId), "the caller's row is not first");
        });

        var revoked = await laptop.Security.RevokeSession(phone.Session.SessionId, ct);

        Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
            $"signing out a device that is not connected failed: {(revoked as FailedRevokeSession)?.error}");

        var after   = (await laptop.Security.GetSessions(ct)).Values.Select(x => x.sessionId).ToArray();
        var refresh = await phone.Session.Identity.GetMyAuthorization("", phone.RefreshToken, ct);

        Assert.Multiple(() =>
        {
            Assert.That(after, Does.Not.Contain(phone.Session.SessionId), "the signed-out device is still on the devices screen");
            Assert.That(after, Does.Contain(laptop.SessionId), "signing out another device took the caller's row with it");
            Assert.That((refresh as BadAuthStatus)?.error, Is.EqualTo(BadAuthKind.SESSION_EXPIRED),
                "the signed-out device's refresh token still mints");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_row_survives_the_flush_and_follows_its_credential_across_launches(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        await Flusher.RunFlushAsync();

        await using (var db = await NewDbAsync(ct))
        {
            var rows = await db.UserSessions.Where(x => x.UserId == laptop.UserId).ToListAsync(ct);

            Assert.That(rows.Select(x => x.PresenceSessionId),
                Is.EquivalentTo(new Guid?[] { laptop.SessionId, phone.Session.SessionId }),
                "the flush did not write one row per sign-in");
        }

        // With the transit forgotten, what is on the screen comes from the database alone.
        await Transit.ForgetUserAsync(laptop.UserId, ct);

        var fromDatabase = (await laptop.Security.GetSessions(ct)).Values.Select(x => x.sessionId).ToArray();

        Assert.That(fromDatabase, Is.EquivalentTo(new[] { laptop.SessionId, phone.Session.SessionId }),
            "the devices screen forgot a device once its transit record was gone");

        // The phone relaunches: a fresh presence sid, the same refresh token.
        var relaunched = new DefaultHeaderInterceptor(Guid.Parse(phone.MachineId));
        var client     = IonClient.Create(HttpClient, WsFactory);

        client.WithInterceptor(relaunched);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var minted = await client.ForService<IIdentityInteraction>(scope.ServiceProvider)
           .GetMyAuthorization("", phone.RefreshToken, ct);

        Assert.That(minted, Is.InstanceOf<GoodAuthStatus>(),
            $"the relaunched device could not refresh: {(minted as BadAuthStatus)?.error}");

        var afterRelaunch = (await laptop.Security.GetSessions(ct)).Values.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(afterRelaunch.Select(x => x.sessionId), Does.Contain(relaunched.SessionId),
                "the relaunched device is not listed under its new sid");
            Assert.That(afterRelaunch.Select(x => x.sessionId), Does.Not.Contain(phone.Session.SessionId),
                "the previous launch is still listed as a device of its own");
            Assert.That(afterRelaunch, Has.Length.EqualTo(2),
                $"a relaunch must not add a device: [{string.Join(", ", afterRelaunch.Select(x => x.sessionId))}]");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Signing_out_everywhere_reaches_devices_that_are_not_connected(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);
        var phone  = await SecondDeviceAsync(laptop, ct);

        // One device already in the database, one still only in the transit.
        await Flusher.RunFlushAsync();

        var tablet = await SecondDeviceAsync(laptop, ct);

        var revoked = await laptop.Security.RevokeAllSessions(ct);

        Assert.That(revoked, Is.InstanceOf<SuccessRevokeSession>(),
            $"signing out everywhere failed: {(revoked as FailedRevokeSession)?.error}");

        var after         = (await laptop.Security.GetSessions(ct)).Values.Select(x => x.sessionId).ToArray();
        var phoneRefresh  = await phone.Session.Identity.GetMyAuthorization("", phone.RefreshToken, ct);
        var tabletRefresh = await tablet.Session.Identity.GetMyAuthorization("", tablet.RefreshToken, ct);

        await Flusher.RunFlushAsync();

        await using var db = await NewDbAsync(ct);

        var rows = await db.UserSessions.Where(x => x.UserId == laptop.UserId).Select(x => x.PresenceSessionId).ToListAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(after, Is.EqualTo(new[] { laptop.SessionId }).AsCollection,
                "the devices screen after signing out everywhere must hold exactly the device it was pressed on");
            Assert.That((phoneRefresh as BadAuthStatus)?.error, Is.EqualTo(BadAuthKind.SESSION_EXPIRED),
                "a device that was in the database kept minting");
            Assert.That((tabletRefresh as BadAuthStatus)?.error, Is.EqualTo(BadAuthKind.SESSION_EXPIRED),
                "a device that was only in the transit kept minting");
            Assert.That(rows, Is.EqualTo(new Guid?[] { laptop.SessionId }).AsCollection,
                "signed-out devices kept their rows in the database");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Signing_in_again_on_the_same_launch_keeps_one_row(CancellationToken ct = default)
    {
        var laptop = await CreateSessionAsync(ct);

        var again = await laptop.Identity.Authorize(
            new UserCredentialsInput(laptop.Credentials.email, null, null, laptop.Credentials.password, null, null), ct);

        Assert.That(again, Is.InstanceOf<SuccessAuthorize>(),
            $"the second sign-in failed: {(again as FailedAuthorize)?.error}");

        var listed = (await laptop.Security.GetSessions(ct)).Values.ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(listed, Has.Length.EqualTo(1),
                $"a second sign-in of the same launch shows as a second device: [{string.Join(", ", listed.Select(x => x.sessionId))}]");
            Assert.That(listed[0].sessionId, Is.EqualTo(laptop.SessionId));
            Assert.That(listed[0].isCurrent, Is.True, "the caller's own launch is not marked current");
        });

        await Flusher.RunFlushAsync();

        await using var db = await NewDbAsync(ct);

        var rows = await db.UserSessions.Where(x => x.UserId == laptop.UserId).ToListAsync(ct);

        Assert.That(rows, Has.Count.EqualTo(1), "the superseded sign-in kept a row in the database");
    }

    [Test, CancelAfter(120_000)]
    public async Task Flush_reads_only_exact_batch_keys_and_preserves_bare_updates_and_tombstones(CancellationToken ct = default)
    {
        var first = await CreateSessionAsync(ct);
        var second = await CreateSessionAsync(ct);
        var firstCredential = Guid.NewGuid();
        var secondCredential = Guid.NewGuid();
        var deadCredential = Guid.NewGuid();
        var missingBareCredential = Guid.NewGuid();
        // Use whole seconds, so database timestamp precision does not affect the assertions.
        var created = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 3600);
        var seen = created.AddMinutes(10);
        var presence = Guid.NewGuid();

        UserSessionEntity Row(Guid user, Guid credential) => new()
        {
            UserId = user, CredentialSessionId = credential, CreatedAt = created,
            LastSeenAt = seen, MachineId = "original-machine", ClientName = "original-client",
        };

        await using var source = await NewDbAsync(ct);
        source.UserSessions.AddRange(
            Row(first.UserId, firstCredential), Row(first.UserId, secondCredential),
            Row(second.UserId, secondCredential), Row(second.UserId, firstCredential),
            Row(first.UserId, deadCredential));
        await source.SaveChangesAsync(ct);

        var observer = new SessionReadObserver();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>(
            (DbContextOptions<ApplicationDbContext>)source.GetService<IDbContextOptions>())
            .AddInterceptors(observer).Options;
        var factory = new ObservedSessionDbFactory(options,
            FactoryAsp.Services.GetRequiredService<IOptions<DatabaseRegionOptions>>());
        // Upsert only depends on the context factory and logger; the other collaborators belong
        // to the list/revocation paths and are deliberately outside this persistence test.
        var store = new SessionRegistryStore(factory, null!, null!, null!, NullLogger<SessionRegistryStore>.Instance);

        var written = await store.UpsertAsync([
            new() { UserId = first.UserId, CredentialSessionId = firstCredential, Bare = true,
                PresenceSessionId = presence, LastSeenAt = seen.AddMinutes(5) },
            new() { UserId = second.UserId, CredentialSessionId = secondCredential,
                CreatedAt = created.AddMinutes(30), LastSeenAt = seen.AddMinutes(-5), ClientName = "updated-client" },
            new() { UserId = first.UserId, CredentialSessionId = missingBareCredential, Bare = true, LastSeenAt = seen },
            new() { UserId = first.UserId, CredentialSessionId = deadCredential, Deleted = true },
        ], ct);

        source.ChangeTracker.Clear();
        var rows = await source.UserSessions.AsNoTracking()
            .Where(x => x.UserId == first.UserId || x.UserId == second.UserId).ToListAsync(ct);
        var updatedFirst = rows.Single(x => x.UserId == first.UserId && x.CredentialSessionId == firstCredential);
        var updatedSecond = rows.Single(x => x.UserId == second.UserId && x.CredentialSessionId == secondCredential);
        var unrelated = rows.Where(x => (x.UserId == first.UserId && x.CredentialSessionId == secondCredential)
            || (x.UserId == second.UserId && x.CredentialSessionId == firstCredential)).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(written, Is.EqualTo(4));
            Assert.That(observer.ReadCommands, Is.EqualTo(1), "the flush must not issue one read per session");
            Assert.That(observer.LoadedKeys, Is.EquivalentTo(new[] {
                new SessionRegistryKey(first.UserId, firstCredential),
                new SessionRegistryKey(second.UserId, secondCredential),
            }), "unrelated sessions or cross-product user/credential pairs were read");
            Assert.That(updatedFirst.PresenceSessionId, Is.EqualTo(presence));
            Assert.That(updatedFirst.LastSeenAt, Is.EqualTo(seen.AddMinutes(5)));
            Assert.That(updatedFirst.MachineId, Is.EqualTo("original-machine"));
            Assert.That(updatedSecond.ClientName, Is.EqualTo("updated-client"));
            Assert.That(updatedSecond.LastSeenAt, Is.EqualTo(seen), "an older touch moved last-seen backwards");
            Assert.That(updatedSecond.CreatedAt, Is.EqualTo(created), "an update replaced the original creation time");
            Assert.That(unrelated, Has.Length.EqualTo(2));
            Assert.That(unrelated.All(x => x.ClientName == "original-client" && x.LastSeenAt == seen), Is.True);
            Assert.That(rows.Any(x => x.CredentialSessionId == missingBareCredential), Is.False);
            Assert.That(rows.Any(x => x.CredentialSessionId == deadCredential), Is.False);
        });
    }

    private sealed class ObservedSessionDbFactory(
        DbContextOptions<ApplicationDbContext> options,
        IOptions<DatabaseRegionOptions> regionOptions) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(options, regionOptions);
    }

    private sealed class SessionReadObserver : DbCommandInterceptor, IMaterializationInterceptor
    {
        public List<SessionRegistryKey> LoadedKeys { get; } = [];
        public int ReadCommands { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                ReadCommands++;
            return ValueTask.FromResult(result);
        }

        public object InitializedInstance(MaterializationInterceptionData materializationData, object entity)
        {
            if (entity is UserSessionEntity row)
                LoadedKeys.Add(new(row.UserId, row.CredentialSessionId));
            return entity;
        }
    }

    private sealed record SecondDevice(TestUserSession Session, string RefreshToken, string MachineId);

    private async Task<SecondDevice> SecondDeviceAsync(TestUserSession account, CancellationToken ct)
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WsFactory);

        client.WithInterceptor(interceptor);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Authorize(
            new UserCredentialsInput(account.Credentials.email, null, null, account.Credentials.password, null, null),
            ct);

        if (result is not SuccessAuthorize authorized)
        {
            Assert.Fail($"Could not sign the account in on a second device: {(result as FailedAuthorize)!.error}");
            return null!;
        }

        Assert.That(authorized.refreshToken, Is.Not.Null.And.Not.Empty, "a sign-in on a machine minted no refresh token");

        interceptor.SetToken(authorized.token);

        var session = new TestUserSession(client, FactoryAsp.Services, account.Credentials, authorized.token, interceptor.SessionId);

        session.UserId = (await session.Users.GetMe(ct)).userId;

        Assert.That(session.UserId, Is.EqualTo(account.UserId),
            "the second client signed in as a different user, so it is not a second device of this account");

        return new SecondDevice(session, authorized.refreshToken!, interceptor.MachineId);
    }
}
