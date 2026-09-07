namespace ArgonComplexTest.Tests;

using AccountContracts;
using Argon.Api.Entities.Data;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Auth;
using Argon.Features.Storage;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using Argon.Grains.Persistence.States;
using Argon.Services;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Account;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Storage;

/// <summary>
/// What "delete my account" actually does — the state machine in front of it, and the erasure behind.
/// </summary>
/// <remarks>
/// <para>Account deletion is the one operation in the product that cannot be undone and that a
/// regulator will ask about. It is also the one with the widest blast radius: a single grain call
/// reaches the user row, the social graph, every space the person was in, the voice rooms they were
/// sitting in, the files they uploaded, their sessions, and the archive of their own data they may
/// have downloaded a day earlier. Nothing else in the codebase touches that many subsystems in one
/// turn, and nothing else has a failure mode that reads, in a support ticket, as "you told me my
/// account was gone and my email is still in your database".</para>
///
/// <para>So this fixture is built the way an auditor would build it: one richly-furnished account —
/// friends both ways, a pending request in each direction, a block, a DM with history, membership of
/// two spaces with messages of its own in each, a seat in a voice channel, uploaded files, a passkey,
/// a privacy rule, a saved gif, mute and auto-delete settings, a live socket with somebody watching —
/// is deleted exactly once in <see cref="BuildTheDeletedAccountAsync"/>, and every test below asks one
/// question of the wreckage. Building it once is not only about wall time: the interesting assertions
/// are about a single execution's consequences, and re-running the deletion per test would let a
/// difference between two runs masquerade as a difference between two subsystems.</para>
///
/// <para>Everything the fixture asserts is the <em>intended</em> contract — what a person who pressed
/// the button would expect to be true afterwards — never what the code currently does. Where the two
/// differ the test is red and carries <c>KnownPresenceBug</c> with a <c>remarks</c> naming the file
/// and method responsible. That is the deliverable: the red tests are the report.</para>
///
/// <para><b>Everything an observer could see is captured once, in the window the erasure opened</b>
/// (<see cref="Aftermath"/>), rather than looked up when each test runs. That is what makes the
/// event and roster assertions mean something: the roster of a space whose read cache is never
/// invalidated does eventually catch up when the entry expires two minutes later, so a test that
/// asked at an arbitrary moment would report a defect or not depending on how long NUnit had spent
/// on the tests before it. Anchored to the execution, "ten seconds after the account was erased the
/// space still listed it" is the same statement every run.</para>
///
/// <para><b>D13 (execution interrupted mid-flight) is addressed by seeding the grain's persisted
/// state.</b> There is no hook in the suite to deactivate a grain on demand, and
/// <c>ExecuteDeletionAsync</c> catches every exception, so an <c>Executing</c> activation cannot be
/// produced by driving the product. What can be produced is the state a crashed silo leaves behind:
/// <c>AccountDeletionGrainState { Status = Executing }</c> written straight into the
/// <c>account-deletion-store</c> before the grain is ever activated, which is byte-identical to what
/// the grain itself wrote at <c>AccountDeletionGrain.cs:374</c> immediately before the work began.
/// The activation that follows reads it and is exactly the activation a restarted silo would
/// have.</para>
/// </remarks>
[TestFixture]
public class AccountDeletionTests : TestBase
{
    /// <summary>The account that is deleted once, in one-time setup, and interrogated by most tests below.</summary>
    private DeletedAccount scene = null!;

    /// <summary>
    /// How long an intended reaction to an executed deletion is given before it counts as absent.
    /// </summary>
    /// <remarks>
    /// Every consequence in this fixture is fanned out inside the same grain turn that anonymised the
    /// row, so none of them has anything to wait for beyond a broadcast and a cache invalidation. Ten
    /// seconds is the suite's settle window; a roster that has not caught up by then is not slow.
    /// </remarks>
    private static TimeSpan Reaction => PresenceWaits.Settle;

    [OneTimeSetUp]
    public async Task BuildAndDeleteAsync()
        => scene = await BuildTheDeletedAccountAsync();

    [OneTimeTearDown]
    public async Task CloseSocketsAsync()
    {
        if (scene is null) return;

        await scene.Observer.DisposeAsync();
        await scene.Victim.DisposeAsync();
    }

    // ── D1: credentials ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The access token the deleted account was holding stops being honoured.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defect ACC-01, fixed).</b> An erasure signs the account out everywhere,
    /// and nothing else in the product can stand in for it: <c>ArgonTransactionInterceptor</c>
    /// authenticates from the signature alone, its one per-user database read
    /// (<c>ResolveLockdownSeverityAsync</c>) queries <c>db.Users</c> under the global soft-delete
    /// filter and reads the resulting null as an unlocked account, and <c>AnonymizeUserAsync</c>
    /// clears <c>LockdownReason</c> anyway. So the only thing between an erased account and seven more
    /// days of fully privileged API access is the revocation store, and
    /// <c>AccountDeletionGrain.InvalidateSessionsAsync</c> now writes it: the
    /// <c>SessionRevocation.FloorKey</c> watermark that ends every token issued before the erasure,
    /// plus a tombstone in the per-user revoked set for every live presence sid and every credential
    /// id recorded against it — the same writes <c>SecurityGrain.ChangePasswordAsync</c> and
    /// <c>EndSessionAsync</c> make for the strictly weaker events of a password change and a device
    /// sign-out.</para>
    ///
    /// <para>The floor is the one write in that step that is deliberately not best-effort: a floor
    /// that silently failed to land is an erasure that silently revoked nothing, so it is allowed to
    /// throw and fail the execution into <c>Failed</c>, which the grain now retries.</para>
    ///
    /// <para><b>Why the refusal is asserted as a refusal rather than by its code.</b> The gate throws
    /// <c>IonProtocolError("NO_AUTH", "Unauthorized")</c> and the server logs exactly that, but the
    /// <c>ion.runtime</c> transport answers a protocol error with an HTTP status the client re-reads
    /// as <c>UPSTREAM_ERROR: Bad Request</c> — so no client-side assertion can name the code, for this
    /// refusal or for any other in the product, and one that tried would be pinning the transport
    /// rather than the erasure. What the client can see is asserted instead, and two things keep it
    /// honest: the two calls are chosen because they never load the user row (<c>UserGrain.GetMe</c>
    /// throws for a deleted account, and that would read as "refused" while proving nothing), and a
    /// live control account makes the same two calls in the same breath, so a server refusing
    /// everybody cannot pass this.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_deleted_accounts_access_token_is_refused_on_every_authenticated_call(
        CancellationToken ct = default)
    {
        var friends = await OutcomeOf(() => scene.VictimSession.Friends.GetMyFriendships(50, 0, ct));
        var chats   = await OutcomeOf(() => scene.VictimSession.Chats.GetRecentChats(50, 0, ct));

        // The control: an account that was never erased, calling the same two methods through the
        // same client, right now.
        var alive        = await CreateSessionAsync(ct);
        var aliveFriends = await OutcomeOf(() => alive.Friends.GetMyFriendships(50, 0, ct));
        var aliveChats   = await OutcomeOf(() => alive.Chats.GetRecentChats(50, 0, ct));

        // And the write the refusal rests on, read straight out of the revocation store: the
        // sign-out-everywhere watermark is what ends every token minted before the erasure, including
        // the ones no session index could have named.
        var probe = await PresenceProbe.CreateAsync(ct);
        var floor = SessionRevocation.ParseFloor(
            await probe.Cache.StringGetAsync(SessionRevocation.FloorKey(scene.VictimId), ct));

        Assert.Multiple(() =>
        {
            Assert.That(aliveFriends, Is.EqualTo("accepted"),
                $"the control account's token was refused too, so nothing below means anything: '{aliveFriends}'");
            Assert.That(aliveChats, Is.EqualTo("accepted"),
                $"the control account's token was refused too, so nothing below means anything: '{aliveChats}'");

            Assert.That(friends, Is.Not.EqualTo("accepted"),
                $"an erased account's access token was still served: GetMyFriendships answered '{friends}'");
            Assert.That(chats, Is.Not.EqualTo("accepted"),
                $"an erased account's access token was still served: GetRecentChats answered '{chats}'");

            Assert.That(floor, Is.Not.Null,
                "the erasure wrote no sign-out-everywhere floor, so every credential the account was " +
                "holding — including the ten-year refresh token — is still honoured");
            Assert.That(floor, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow),
                "the floor is dated in the future, which ends nothing that exists today");
        });
    }

    /// <summary>
    /// The refresh token the deleted account was holding stops minting new access tokens.
    /// </summary>
    /// <remarks>
    /// The other half of a sign-out: an access token expires on its own, a refresh token is dated ten
    /// years out and re-mints on demand, so leaving it alive means the account is not signed out at
    /// all. Asserted through <c>IdentityInteraction.GetMyAuthorization</c>, which is the mint.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_deleted_accounts_refresh_token_no_longer_mints(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var creds        = GenerateCredentials();
        var registration = await GetIdentityService(scope.ServiceProvider).Registration(
            new NewUserCredentialsInput(creds.email, creds.username, creds.password, creds.displayName,
                creds.argreeTos, creds.birthDate, creds.argreeOptionalEmails, creds.captchaToken, "1.0", "1.0"), ct);

        Assert.That(registration, Is.InstanceOf<SuccessRegistration>(),
            $"could not register the account under test: {(registration as FailedRegistration)?.error}");

        var success = (SuccessRegistration)registration;

        Assert.That(success.refreshToken, Is.Not.Null.And.Not.Empty,
            "registration handed out no refresh token, so there is nothing to revoke");

        SetAuthToken(success.token);

        var userId = (await GetUserService(scope.ServiceProvider).GetMe(ct)).userId;

        // Minting works before the deletion — otherwise the assertion below would pass for a token
        // that never worked in the first place.
        var before = await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", success.refreshToken, ct);
        Assert.That(before, Is.InstanceOf<GoodAuthStatus>(),
            $"the refresh token did not mint even before the deletion: {before.GetType().Name}");

        await ScheduleAndExecuteAsync(userId, creds.password, ct);

        var after = await GetIdentityService(scope.ServiceProvider).GetMyAuthorization("", success.refreshToken, ct);

        Assert.That(after, Is.Not.InstanceOf<GoodAuthStatus>(),
            "the refresh token of an erased account still mints access tokens");
    }

    // ── D2: the live socket ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The deleted account's connected client is closed, and hears nothing more.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defect ACC-02, fixed).</b> A hub connection joins the group of every
    /// space its user belongs to at connect time and is never taken out of one server-side, so a
    /// client that simply stops talking keeps receiving other people's presence, roster and channel
    /// traffic for as long as it is left open — the per-call gate never fires because it makes no
    /// calls. The erasure therefore has to reach the transport, and
    /// <c>AccountDeletionGrain.InvalidateSessionsAsync</c> now does it the way the product's own
    /// sign-out does: <c>IUserSessionGrain.GoOfflineAsync</c> per live session, then the tombstones,
    /// then <c>ISessionRevocationBroadcaster.PublishAsync</c> — the <c>argon.session.revoked</c>
    /// signal every node that maps the hub listens to, which aborts the connections it holds.</para>
    ///
    /// <para>The observer's status change is generated after the erasure precisely so the assertion
    /// cannot pass for a server that broadcast nothing: it is the control, and it is asserted first.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_deleted_accounts_socket_is_closed_and_goes_quiet(CancellationToken ct = default)
    {
        var observerMark = scene.Observer.Mark();
        var victimMark   = scene.Victim.Mark();

        // Fresh traffic, generated now, long after the erasure: a status change fans out to the
        // space group, which is the group the erased connection was never taken out of. Presence
        // rather than a message because a space-wide broadcast is what a group membership decides,
        // and a message would also have to survive a channel subscription this client never made.
        await scene.Observer.Heartbeat(UserStatus.DoNotDisturb, ct);

        // The control, and the reason this test can say anything at all: if the space was told
        // nothing, "the erased client heard nothing" is true of a server that sent nothing.
        var broadcast = await scene.Observer.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == scene.ObserverSession.UserId && e.status == UserStatus.DoNotDisturb,
            Reaction, observerMark, ct);

        Assert.That(broadcast, Is.Not.Null,
            "the space was never told about the status change this test generates, so the assertion " +
            "below would pass for a server that broadcast nothing at all");

        var heard = scene.Victim.Records(victimMark);

        Assert.Multiple(() =>
        {
            Assert.That(scene.After.SocketClosed, Is.True,
                "the erased account's hub connection is still open; it is still in every space group it joined");
            Assert.That(heard, Is.Empty,
                "the erased account's client is still being fed other people's presence: " +
                scene.Victim.Dump(victimMark));
        });
    }

    // ── D3: the spaces ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every space the account was in is told it left, and stops listing it.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defects ACC-03 and ACC-02, fixed).</b> A roster is answered by
    /// <c>SpaceReadGrain</c> out of a cache whose distributed expiry is two minutes, so a membership
    /// removed without invalidating it is a member who is still there for everyone else — and with no
    /// event fired, a client that bootstrapped the space in that window keeps them for ever. The
    /// erasure therefore leaves each space through <c>ISpaceGrain.RemoveMemberAsync</c>, which owns
    /// the three halves as one thing: the soft-delete, <c>Invalidate()</c>, and the
    /// <c>LeavedFromServerUser</c> that <c>BotEventPublisher</c> maps to <c>MemberLeave</c>.</para>
    ///
    /// <para>The <c>Offline</c> broadcast is the other fix, and it comes from somewhere else: ending
    /// the sessions through <c>IUserSessionGrain.GoOfflineAsync</c> reaches
    /// <c>FinalizeOfflineAsync</c> and its <c>AggregateAndBroadcastStatusAsync</c>. That is why the
    /// session teardown is step 1 and the membership removal step 6 — the fan-out resolves the spaces
    /// through the very rows step 6 removes, so the other order announces nothing to nobody.</para>
    ///
    /// <para>Read from the window opened at the instant of the erasure (see <c>Aftermath</c>), because
    /// a roster with a two-minute backstop converges eventually whatever the code does; anchoring it
    /// is what makes "ten seconds after the account was erased" the same statement every run.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public Task The_spaces_are_told_the_member_left_and_stop_listing_them(CancellationToken ct = default)
    {
        // Read from the window the scenario opened at the instant of the erasure — see Aftermath.
        var after = scene.After;

        Assert.Multiple(() =>
        {
            Assert.That(after.Left, Is.Not.Null,
                "nobody in the space was told the erased account left it");
            Assert.That(after.WentOffline, Is.Not.Null,
                "the space was never told the erased account went offline, so it is still rendered online");
            Assert.That(after.Members, Does.Not.Contain(scene.VictimId),
                $"GetMembers still listed an account that no longer exists {Reaction.TotalSeconds:F0}s " +
                "after it was erased; the membership row was soft-deleted with no cache invalidation, " +
                "so the roster only catches up when SpaceReadGrain's own entry expires");
            Assert.That(after.Presence, Does.Not.Contain(scene.VictimId),
                "GetMemberPresence still lists an account that no longer exists");
        });

        return Task.CompletedTask;
    }

    // ── D4: voice ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The voice seat the account was sitting in is vacated.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defect ACC-04, fixed).</b> A user with no live session must not hold a
    /// voice seat — the invariant <c>UserSessionGrain.FinalizeOfflineAsync</c> states and
    /// <c>PresenceVoiceAndCountsTests</c> guards — and an erasure is the strongest possible instance
    /// of it. Deletion used to delete the presence keys underneath the session grain instead of
    /// calling it, so <c>FinalizeOfflineAsync</c> never ran and nothing reached
    /// <c>IUserGrain.LeaveAllVoiceAsync</c>; the seat was then unclearable, because that method
    /// resolves spaces through <c>ctx.Users</c> under the soft-delete filter and the membership rows,
    /// both of which the erasure had just removed, and a moderator kick only reaches the SFU.</para>
    ///
    /// <para>So the teardown goes through <c>IUserSessionGrain.GoOfflineAsync</c>, and it is step 1 of
    /// the execution for exactly that reason: any later and there is nothing left to resolve.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public Task The_voice_seat_is_vacated(CancellationToken ct = default)
    {
        Assert.That(scene.VoiceOccupantsBefore, Does.Contain(scene.VictimId),
            $"the account never took the voice seat this test is about (joined via {scene.VoiceJoinPath}), " +
            "so what follows would pass for the wrong reason");

        var after = scene.After;

        Assert.Multiple(() =>
        {
            Assert.That(after.Occupants, Does.Not.Contain(scene.VictimId),
                "the voice channel still seats an account that no longer exists");
            Assert.That(after.Vacated, Is.Not.Null,
                "nobody was told the erased account left the call");
        });

        return Task.CompletedTask;
    }

    // ── D5: the social graph ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Friends, pending requests and blocks in both directions are gone, and the DM peer sees a
    /// deleted account rather than a hole.
    /// </summary>
    /// <remarks>
    /// <para><b>Defect ACC-05 (fixed).</b> <c>DeletePrivateDataAsync</c> cleared <c>Friends</c> and
    /// <c>UserBlocklist</c> in both directions but never touched <c>FriendRequest</c>, so a pending
    /// request naming the erased account survived in both directions — the other party kept an
    /// actionable invitation from somebody who no longer exists, and accepting it wrote a friendship
    /// row against an anonymised id. It is deleted in both directions now, in the same shape as its
    /// neighbours.</para>
    ///
    /// <para><b>Defect ACC-06 (fixed elsewhere).</b> <c>LookupUser</c> — the call a chat window makes
    /// for a peer it shares no space with — used to route through a <c>FirstAsync</c> under the global
    /// soft-delete filter and throw instead of answering, so the peer could read a conversation whose
    /// other party could not be resolved at all. It now goes through
    /// <c>IUserGrain.GetIdentityIncludingDeleted</c> and answers the same "Deleted Account" tombstone
    /// <c>SpaceGrain.PrefetchUser</c> gives, which is what the two surfaces disagreeing about was.</para>
    ///
    /// <para>And <c>IdentityDirectoryGrain.GetUserBasicInfoAsync</c> queries without
    /// <c>IgnoreQueryFilters</c>, so <c>LookupUser</c> — the call a chat window makes for a peer it is
    /// not in a space with — answers "not found" for an anonymised row instead of the "Deleted
    /// Account" placeholder <c>SpaceGrain.GetMemberProfile</c> returns. The conversation history
    /// survives, correctly, but the client has nothing to render at the top of it.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_social_graph_forgets_the_deleted_account(CancellationToken ct = default)
    {
        var peerFriends  = (await scene.PeerSession.Friends.GetMyFriendships(50, 0, ct)).Values.Select(f => f.friendId).ToArray();
        var incomingKept = (await scene.TargetSession.Friends.GetMyFriendPendingList(50, 0, ct)).Values.Select(r => r.requesterId).ToArray();
        var outgoingKept = (await scene.RequesterSession.Friends.GetMyFriendOutgoingList(50, 0, ct)).Values.Select(r => r.targetId).ToArray();
        var blockedList  = (await scene.BlockedSession.Friends.GetBlockList(50, 0, ct)).Values.Select(b => b.blockedId).ToArray();

        var history = (await scene.PeerSession.Chats.QueryDirectMessages(scene.VictimId, null, 50, ct)).Values.Select(m => m.text).ToArray();

        // Captured rather than awaited into an assertion: LookupUser routes through
        // UserGrain.GetMe, whose SingleAsync runs under the soft-delete filter, so for an
        // anonymised row it throws out of the RPC instead of answering. An exception escaping here
        // would abort the test and hide every finding above it.
        string  lookupOutcome;
        string? lookupName = null;

        try
        {
            var lookup = await scene.PeerSession.Users.LookupUser(scene.VictimId, ct);

            lookupName    = (lookup as SuccessLookupUser)?.user.displayName;
            lookupOutcome = lookup switch
            {
                SuccessLookupUser ok  => $"resolved as '{ok.user.displayName}' / @{ok.user.username}",
                FailedLookupUser fail => $"refused with {fail.error}",
                _                     => lookup.GetType().Name
            };
        }
        catch (Exception e)
        {
            lookupOutcome = $"threw {e.GetType().Name}: {e.Message}";
        }

        Assert.Multiple(() =>
        {
            Assert.That(peerFriends, Does.Not.Contain(scene.VictimId),
                "a friend still has the erased account in their friend list");

            Assert.That(incomingKept, Does.Not.Contain(scene.VictimId),
                "a pending friend request FROM the erased account is still sitting in somebody's inbox");
            Assert.That(outgoingKept, Does.Not.Contain(scene.VictimId),
                "a pending friend request TO the erased account is still listed as outgoing");

            Assert.That(blockedList, Does.Not.Contain(scene.VictimId),
                "a block naming the erased account survives it");

            // The conversation is the peer's history as much as the deleted user's, so it stays —
            // what has to change is who it says wrote it.
            Assert.That(history, Does.Contain(DmFromVictim),
                "the peer lost their own conversation history when the other party was erased");

            Assert.That(lookupName, Is.EqualTo("Deleted Account"),
                "the peer's chat window has nothing to put at the top of a conversation it can still " +
                $"read: LookupUser {lookupOutcome}");
        });
    }

    // ── D6: the doors back in ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The account cannot sign in again, its username is held back, and its e-mail address is freed.
    /// </summary>
    /// <remarks>
    /// <para>The three halves of "the account is gone" that a person can check for themselves. The
    /// username reservation is deliberate — <c>ReserveUsernameAsync</c> writes a
    /// <c>UsernameReservedEntity</c> row — so somebody else cannot inherit the identity a community
    /// knew. The e-mail is deliberately <em>not</em> held: the row's address is rewritten to
    /// <c>deleted_{id}@void.local</c> and both normalised columns are computed by the database, so the
    /// real address becomes registrable again, which is what lets a person come back.</para>
    ///
    /// <para><b>Only the e-mail door is tried, and that is not a gap in the test.</b>
    /// <c>ArgonAuthorizationService.Authorize</c> (<c>src/Argon.Core/Features/Auth/ArgonAuthorizationService.cs:52</c>)
    /// matches on <c>NormalizedEmail == input.email.ToLowerInvariant()</c> and never reads
    /// <c>input.username</c> at all, so signing in by username is not a path this product has — and a
    /// caller who omits the e-mail gets a <c>NullReferenceException</c> out of the LINQ parameter and
    /// an <c>UPSTREAM_ERROR</c> back, for any account, deleted or not. That is a real defect and it is
    /// reported, but it is not this one, and asserting it here would colour a deletion test with a
    /// failure that has nothing to do with deletion.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Login_is_refused_the_username_is_reserved_and_the_email_is_freed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var identity = GetIdentityService(scope.ServiceProvider);

        var byEmail = await identity.Authorize(
            new UserCredentialsInput(scene.Credentials.email, null, null, scene.Credentials.password, null, null), ct);

        var reclaimUsername = await identity.Registration(
            NewCredentials(scene.Credentials.username, $"reclaim_{Guid.NewGuid():N}"[..20] + "@test.local"), ct);
        var reclaimEmail = await identity.Registration(
            NewCredentials($"reclaim_{Guid.NewGuid():N}"[..20], scene.Credentials.email), ct);

        Assert.Multiple(() =>
        {
            Assert.That(byEmail, Is.InstanceOf<FailedAuthorize>(),
                "an erased account still signs in with its e-mail address and password");

            Assert.That(reclaimUsername, Is.InstanceOf<FailedRegistration>(),
                "somebody else was handed the username of a deleted account");
            Assert.That((reclaimUsername as FailedRegistration)?.error, Is.EqualTo(RegistrationError.USERNAME_RESERVED),
                "the refusal has to say the name is reserved, or the sign-up form cannot explain itself");

            Assert.That(reclaimEmail, Is.InstanceOf<SuccessRegistration>(),
                "the deleted account's e-mail address was not freed, so the person cannot come back: " +
                $"{(reclaimEmail as FailedRegistration)?.error}");
        });
    }

    // ── D7: the tables ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every table holding personal data about the account is empty of it afterwards.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defects ACC-07 and ACC-05, fixed).</b> "Permanently remove your account
    /// and all data" is what the console says over the button that runs this, so every table keyed to
    /// the user id is empty of them afterwards — and <c>DeletePrivateDataAsync</c> is a hand-written
    /// list, which is precisely why it needs a census in front of it. It had already lost four tables
    /// to drift (they did not exist when the list was written), and the sharpest pair were
    /// <c>PendingEmailChanges</c> and <c>PendingPhoneChanges</c>: a plaintext address and phone number
    /// the person was half-way through switching to, with no TTL, no sweeper and a declared FK cascade
    /// that can never fire because the row is anonymised rather than removed — while the same
    /// execution is careful to null the <c>PhoneNumber</c> column eighty lines earlier.</para>
    ///
    /// <para><c>FriendRequest</c> is the one that was not merely residue: the friends lists do not
    /// filter deleted users, so the other party kept an actionable invitation from an account the
    /// product had told its owner was gone, and accepting it wrote a fresh friendship against the
    /// erased id — undoing part of the erasure from outside it.</para>
    ///
    /// <para><b>This census is the guard against the next table.</b> A row here is cheap and the list
    /// it checks is maintained by hand, so anything added to <c>DeletePrivateDataAsync</c> gets a
    /// count here, and anything deliberately kept is named in that method's remarks with its reason —
    /// today: reports and content violations (a record about the people who were harmed), device bans
    /// and device keys (keyed by machine, naming no user, so a banned device stays banned), coupon
    /// redemptions (single-use enforcement), and messages (somebody else's history).</para>
    ///
    /// <para>The census is taken in one pass and asserted in one <c>Assert.Multiple</c> on purpose:
    /// the useful output of this test is the complete list of survivors, not the first one.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Every_table_holding_personal_data_is_emptied(CancellationToken ct = default)
    {
        var census = await CensusAsync(scene.VictimId, ct);

        Assert.That(scene.CensusBefore.Total, Is.GreaterThan(0),
            "nothing was seeded, so an empty census afterwards would prove nothing: " + scene.CensusBefore);

        // Per-table premise for the five tables added with the fix. The total above cannot catch a
        // seed that silently did not land, and a count that was zero before and zero after is an
        // assertion that passes for the wrong reason.
        Assert.Multiple(() =>
        {
            var before = scene.CensusBefore;

            Assert.That(before.Ignores, Is.GreaterThan(0), "no ignore was seeded: " + before);
            Assert.That(before.InventoryItems, Is.GreaterThan(0), "no inventory item was seeded: " + before);
            Assert.That(before.UnreadInventoryItems, Is.GreaterThan(0), "no unread-inventory badge was seeded: " + before);
            Assert.That(before.TeamMemberships, Is.GreaterThan(0), "no team membership was seeded: " + before);
            Assert.That(before.TeamInvites, Is.GreaterThan(0), "no team invite was seeded: " + before);
        });

        Assert.Multiple(() =>
        {
            // Deleted today — the control group. If any of these regress, the test says so before the
            // interesting failures below drown it out.
            Assert.That(census.Friendships, Is.Zero, "friendships survive");
            Assert.That(census.Blocks, Is.Zero, "blocks survive");
            Assert.That(census.MuteSettings, Is.Zero, "mute settings survive");
            Assert.That(census.AutoDeleteSettings, Is.Zero, "the auto-delete setting survives");
            Assert.That(census.DeviceHistories, Is.Zero, "device history survives");
            Assert.That(census.Passkeys, Is.Zero, "passkeys survive");

            // Not deleted today. Each of these is a row of personal data about a person the product
            // has told that their account is erased.
            Assert.That(census.FriendRequests, Is.Zero,
                "pending friend requests naming the erased account survive it");
            Assert.That(census.PrivacyRules, Is.Zero,
                "the erased account's privacy rules survive it");
            Assert.That(census.SavedGifs, Is.Zero,
                "the erased account's saved gifs survive it");
            Assert.That(census.PendingEmailChanges, Is.Zero,
                "a plaintext e-mail address the erased account was switching to survives it — " +
                "the same deletion is careful to null the PhoneNumber column two lines earlier");
            Assert.That(census.PendingPhoneChanges, Is.Zero,
                "a plaintext phone number the erased account was switching to survives it");
            Assert.That(census.DeviceObservations, Is.Zero,
                "device fingerprints of the erased account survive it, while DeviceHistories are deleted");
            Assert.That(census.ChannelReadStates, Is.Zero,
                "the erased account's per-channel read positions survive it");
            Assert.That(census.NotificationCounters, Is.Zero,
                "the erased account's notification counters survive it");
            Assert.That(census.SystemNotifications, Is.Zero,
                "system notifications addressed to the erased account survive it");
            Assert.That(census.TrustScores, Is.Zero,
                "the erased account's trust score survives it; a score derived from reports is not a " +
                "record of one, and it is recomputed from scratch for whoever holds the id next");

            // Added with the fix, and here for the reason the remarks give: the deletion list is
            // maintained by hand, so every table it names is counted here or the next one to be
            // forgotten is forgotten silently.
            Assert.That(census.Ignores, Is.Zero,
                "an ignore naming the erased account survives it, in a table blocks are cleared from");
            Assert.That(census.InventoryItems, Is.Zero,
                "the erased account's inventory survives it, owned by an id that no longer names anyone");
            Assert.That(census.UnreadInventoryItems, Is.Zero,
                "unread-inventory badges addressed to the erased account survive it");
            Assert.That(census.TeamMemberships, Is.Zero,
                "the erased account is still a member of somebody else's developer team");
            Assert.That(census.TeamInvites, Is.Zero,
                "a developer-team invitation naming the erased account survives it");
        });
    }

    /// <summary>
    /// The user row is anonymised in place and the files it owned lose their reference.
    /// </summary>
    /// <remarks>
    /// The row stays — messages and reports point at it — so what has to be true is that nothing
    /// identifying is left on it. The avatar is the one field that is both personal data and a
    /// reference somebody else's storage quota depends on, so both halves are asserted: the column is
    /// cleared and the file's reference count came down.
    ///
    /// <para><b>The contract (defect ACC-08, grain half fixed).</b> Each file the account owns is
    /// released exactly once. Two places released the avatar — <c>AnonymizeUserAsync</c> by id, and
    /// <c>DecrementFileRefsAsync</c> by walking every non-deleted file the user owns, which normally
    /// includes it — so a file with one reference ended on -1. The targeted release is kept, because
    /// it is the only one that reaches an avatar the user does <em>not</em> own (<c>UserGrain.UpdateMe</c>
    /// assigns <c>AvatarFileId</c> straight from client input with no ownership check); it now fires
    /// only when the walk will not reach the same file. That makes the count right here without
    /// depending on <c>ReferenceCountService.DecrementAsync</c> clamping at zero — which is a separate
    /// fix, in the place the invariant lives, and would otherwise hide this one.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_row_is_anonymised_and_the_files_are_dereferenced(CancellationToken ct = default)
    {
        var row = await AccountSeed.ReadUserAsync(scene.VictimId, ct);

        Assert.That(row, Is.Not.Null, "the row is anonymised in place, never removed");

        await using var db = await AccountSeed.NewDbAsync(ct);

        var avatarRefs = await db.FileCounters.IgnoreQueryFilters()
           .Where(c => c.Id == scene.AvatarFileId).Select(c => c.RefCount).FirstOrDefaultAsync(ct);
        var uploadRefs = await db.FileCounters.IgnoreQueryFilters()
           .Where(c => c.Id == scene.UploadFileId).Select(c => c.RefCount).FirstOrDefaultAsync(ct);

        Assert.Multiple(() =>
        {
            Assert.That(row!.IsDeleted, Is.True);
            Assert.That(row.DisplayName, Is.EqualTo("Deleted Account"));
            Assert.That(row.Username, Does.Not.Contain(scene.Credentials.username), "the old username is still on the row");
            Assert.That(row.Email, Does.Not.Contain(scene.Credentials.email), "the old e-mail is still on the row");
            Assert.That(row.PhoneNumber, Is.Null);
            Assert.That(row.PasswordDigest, Is.Null);
            Assert.That(row.AvatarFileId, Is.Null, "the erased account still points at its avatar");

            // Zero, not "at most zero": the seeded count was one and exactly one thing referenced
            // the file. A negative count is a double release — AnonymizeUserAsync releases the
            // avatar by id, and DecrementFileRefsAsync then walks every file the user owns, which
            // includes the avatar — and a reference count that can go negative cannot be trusted to
            // decide whether an object is still needed.
            Assert.That(avatarRefs, Is.Zero,
                "the avatar file's reference count is wrong after the deletion; below zero means it " +
                "was released twice, once by AnonymizeUserAsync and again by DecrementFileRefsAsync");
            Assert.That(uploadRefs, Is.Zero,
                "an uploaded file kept its reference, so the bytes are held for an account that does not exist");
        });
    }

    /// <summary>
    /// Nothing keeps a plaintext copy of the identity after the deletion has finished.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defect ACC-09, fixed).</b> <c>AccountDeletionGrainState</c> holds
    /// <c>OriginalEmail</c>, <c>OriginalUsername</c> and <c>OriginalDisplayName</c> because the
    /// username reservation and the completion mail need them, and the terminal write used to leave
    /// them in place — so the Orleans grain-storage key <c>account-deletion-store</c> kept the full
    /// plaintext identity of every deleted account for ever, with no TTL, in a store no data-subject
    /// process knows to look in. Since <c>AnonymizeUserAsync</c> frees the address on the
    /// <c>Users</c> row, that key was the last surviving link from the user id to the person, which
    /// is exactly the association the erasure exists to sever. They are nulled in the same write that
    /// sets <c>Completed</c>, after the last step that reads them.</para>
    ///
    /// <para>Only on the completed branch: a <c>Failed</c> run keeps them, because the retry
    /// <c>CheckAndExecuteAsync</c> now performs still has a username to reserve and an address to
    /// write to.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_grains_own_state_keeps_no_copy_of_the_identity(CancellationToken ct = default)
    {
        var kept = await ReadDeletionStateAsync(scene.VictimId);

        Assert.Multiple(() =>
        {
            Assert.That(kept.Status, Is.EqualTo(AccountDeletionStatus.Completed),
                "the state read back does not describe the deletion this fixture ran");
            Assert.That(kept.OriginalEmail, Is.Null,
                $"the deletion grain still holds the erased account's e-mail address: '{kept.OriginalEmail}'");
            Assert.That(kept.OriginalUsername, Is.Null,
                $"the deletion grain still holds the erased account's username: '{kept.OriginalUsername}'");
            Assert.That(kept.OriginalDisplayName, Is.Null,
                $"the deletion grain still holds the erased account's display name: '{kept.OriginalDisplayName}'");
        });
    }

    // ── D8: the export archive ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// An archive of the account's data does not outlive the account.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defects ACC-10 / X5, fixed — a decision, taken).</b> An archive is
    /// <c>profile.json</c> with the person's e-mail, phone and date of birth, <c>devices.json</c> with
    /// up to a hundred IP addresses, and every message they wrote: the whole of what the erasure is
    /// about to remove, in one object, behind a presigned URL that needs no authentication. The
    /// product's model was that the archive expires on the store's clock rather than with the
    /// account — but nothing in this repository ever provisions the bucket lifecycle rule that model
    /// rests on, and no code path anywhere deletes a finished archive, so "expires" meant "the link
    /// stops working". The call is that erasure destroys the archive rather than waiting for it:
    /// <c>ExecuteDeletionAsync</c> cancels a running export and deletes everything under
    /// <c>exports/{userId}/</c>, at step 2, before the row is anonymised.</para>
    ///
    /// <para>By prefix rather than by the stored key, for the reason <c>UserDataExportGrain</c> cleans
    /// its intermediates that way: an archive whose key the export grain has already forgotten — which
    /// is what its expiry transition produces — is exactly the one nothing else will ever remove. The
    /// delete is allowed to fail the attempt rather than being swallowed: it used to be wrapped in a
    /// catch that logged and returned, which recorded step 2 as done, so a bucket that answered once
    /// with an error left the archive behind for ever. See
    /// <see cref="An_archive_purge_the_cursor_never_recorded_is_run_by_the_next_attempt"/>.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_completed_export_does_not_survive_the_account(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var requested = await session.Security.RequestDataExport(ct);
        Assert.That(requested, Is.InstanceOf<SuccessRequestDataExport>(),
            $"the export was refused: {(requested as FailedRequestDataExport)?.error}");

        var completed = await AccountConsoleHarness.WaitForExportAsync(session, DataExportStatusKind.COMPLETED, ct: ct);
        Assert.That(completed.status, Is.EqualTo(DataExportStatusKind.COMPLETED),
            $"the export stalled in {completed.status}, so there is no archive to outlive anything");

        var url = completed.downloadUrl!;

        var live = await ExportArchive.TryDownloadAsync(url, ct);
        Assert.That(live.Status, Is.EqualTo(System.Net.HttpStatusCode.OK),
            "the archive was not downloadable even before the deletion");

        await ScheduleAndExecuteAsync(session.UserId, session.Credentials.password, ct);

        var afterwards = await ExportArchive.TryDownloadAsync(url, ct);

        Assert.That(afterwards.Status, Is.Not.EqualTo(System.Net.HttpStatusCode.OK),
            "the erased account's complete personal-data archive is still downloadable, without " +
            "authentication, from the presigned url the deletion did not revoke");
    }

    /// <summary>
    /// An account already on its way out cannot start a new export of itself.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defect ACC-11, fixed in <c>UserDataExportGrain</c>).</b>
    /// <c>RequestExportAsync</c> had no deletion guard at all, so an export could be started for a
    /// scheduled account and — because the collection steps read the user row through the soft-delete
    /// filter and silently skip what they cannot find — could complete for one already erased,
    /// producing an archive with no profile.json in it and no mail to say it is ready. It now refuses
    /// while the deletion is <c>Scheduled</c>, <c>Executing</c> or <c>Completed</c>.</para>
    ///
    /// <para>Read together with <see cref="A_completed_export_does_not_survive_the_account"/>: that
    /// one destroys an archive the account already had, this one stops a new copy being made of
    /// everything it is about to lose. The pair is what closes the window in which an archive can
    /// outlive the account at all.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_export_cannot_be_started_for_an_account_under_deletion(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var scheduled = await console.RequestDeleteAccount(session.Credentials.password, ct);
        Assert.That(scheduled.success, Is.True, scheduled.error.ToString());

        var export = await console.RequestExportGDRP(ct);

        Assert.That(export, Is.Not.EqualTo(RequestExportGDRPStatus.Ok),
            "an account whose deletion is already scheduled was allowed to start a fresh export of " +
            "everything that is about to be erased");
    }

    // ── D9: the guards ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Each precondition refuses the request with the reason the console has to render.
    /// </summary>
    /// <remarks>
    /// One account walked through all four bars in the order the grain checks them, because the order
    /// is itself the contract: a locked account with the wrong password must hear about the password
    /// first, and a test that set up one bar at a time would never notice if two of them swapped.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Deletion_is_refused_while_a_guard_stands(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(session, "Guarded", ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var wrongPassword = await console.RequestDeleteAccount(session.Credentials.password + "x", ct);

        await AccountSeed.LockAsync(session.UserId, ct: ct);
        var locked = await console.RequestDeleteAccount(session.Credentials.password, ct);

        await AccountSeed.UnlockAsync(session.UserId, ct);
        await AccountSeed.SetUltimaAsync(session.UserId, true, ct);
        var subscribed = await console.RequestDeleteAccount(session.Credentials.password, ct);

        await AccountSeed.SetUltimaAsync(session.UserId, false, ct);
        var ownsSpace = await console.RequestDeleteAccount(session.Credentials.password, ct);

        // The space is gone for good — the state the "transfer or delete your spaces" instruction is
        // asking the person to reach. There is no ownership-transfer call on any surface, so this is
        // the only way to reach it, which is itself worth saying out loud.
        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Spaces.Where(s => s.Id == spaceId)
               .ExecuteUpdateAsync(set => set
                   .SetProperty(s => s.IsDeleted, true)
                   .SetProperty(s => s.DeletedAt, DateTimeOffset.UtcNow), ct);

        var afterwards = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.Multiple(() =>
        {
            Assert.That(wrongPassword.success, Is.False);
            Assert.That(wrongPassword.error, Is.EqualTo(DeleteAccountError.InvalidPassword));

            Assert.That(locked.success, Is.False);
            Assert.That(locked.error, Is.EqualTo(DeleteAccountError.AccountLocked));

            Assert.That(subscribed.success, Is.False);
            Assert.That(subscribed.error, Is.EqualTo(DeleteAccountError.HasActiveSubscription));

            Assert.That(ownsSpace.success, Is.False);
            Assert.That(ownsSpace.error, Is.EqualTo(DeleteAccountError.OwnsSpaces));

            Assert.That(afterwards.success, Is.True,
                $"the last space is gone and the request is still refused: {afterwards.error}");
        });
    }

    /// <summary>
    /// Having already asked for a space to be deleted does not bar the owner from deleting themselves.
    /// </summary>
    /// <remarks>
    /// <para><b>Observed.</b> <c>SpaceDeletionGrain</c> leaves <c>SpaceEntity.IsDeleted</c>
    /// false for the whole of its grace — seven days private, thirty community — and the account guard
    /// at <c>src/Argon.Api/Grains/AccountDeletionGrain.cs:125</c> is a plain "do you own any
    /// non-deleted space". So the sequence the console's own error text instructs — "please transfer
    /// or delete your spaces before deleting your account" — produces a thirty-day wait followed by a
    /// thirty-day account grace: a sixty-day floor on erasure, outside the one-month window a person
    /// is entitled to, with nothing telling them so and no ownership-transfer call anywhere in the
    /// product to shorten it. Either the guard has to ignore a space already on its way out, or the
    /// refusal has to carry the date so the console can explain the wait.</para>
    ///
    /// <para><b>Adjudicated a design question, not an agreed defect (campaign verdict
    /// <c>ACC-12</c>).</b> The review reproduced the mechanism above and then declined to call it a
    /// bug: the bar is real, but the reviewer called it the thing that prevents an unrecoverable orphaned
    /// space; what is actually wrong is the console copy promising a transfer the product does not
    /// have, and the absent payload on <c>OwnsSpaces</c> that would let the console say when the owner
    /// may try again.
    /// The test stays red and keeps <c>[Category("KnownPresenceBug")]</c> so the default run
    /// excludes it: it pins a decision the product still owes, and it goes green the day that
    /// decision is made and implemented.</para>
    /// </remarks>
    [Test, Category("KnownPresenceBug"), CancelAfter(120_000)]
    public async Task A_space_already_scheduled_for_deletion_does_not_bar_the_owner(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(session, "Leaving Anyway", ct);

        var requested = await session.Servers.RequestDeleteSpace(spaceId, ct);
        Assert.That(requested, Is.InstanceOf<SuccessRequestDeleteSpace>(),
            $"the space deletion was refused: {(requested as FailedRequestDeleteSpace)?.error}");

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var result = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.That(result.success, Is.True,
            $"the owner of a space that is already scheduled for deletion cannot delete their own " +
            $"account: {result.error}. The space's own grace has to elapse first, and the account's " +
            "grace only starts after that.");
    }

    /// <summary>
    /// Asking twice keeps the first answer, and says what it was.
    /// </summary>
    /// <remarks>
    /// <para><b>Observed.</b> The grain does the right thing — <c>RequestDeletionAsync</c>
    /// answers <c>AlreadyScheduled</c> and populates <c>ScheduledDeletionAt</c> from the existing
    /// state — and <c>AccountConsoleService.RequestDeleteAccount</c> throws it away: every failure
    /// branch returns <c>(false, error, null, null)</c>
    /// (<c>src/Argon.Api/Features/AccountConsole/AccountConsoleService.cs:36</c>). The console
    /// literally cannot render "already scheduled, for this date" even though the date came back to
    /// it, which is why a person who clicks the button twice sees an error with no information in
    /// it.</para>
    ///
    /// <para><b>Adjudicated a design question, not an agreed defect (campaign verdict
    /// <c>ACC-13</c>).</b> The review reproduced the mechanism above and then declined to call it a
    /// bug: the reviewer found no client that reads either timestamp on the refusal branch — the console
    /// learns the deadline from <c>GetMe</c> at page load and disables the button — so carrying it
    /// through is an enhancement that only helps if the frontend refetches too, not a correctness fix.
    /// The test stays red and keeps <c>[Category("KnownPresenceBug")]</c> so the default run
    /// excludes it: it pins a decision the product still owes, and it goes green the day that
    /// decision is made and implemented.</para>
    /// </remarks>
    [Test, Category("KnownPresenceBug"), CancelAfter(120_000)]
    public async Task A_second_request_keeps_and_reports_the_original_execution_date(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var first  = await console.RequestDeleteAccount(session.Credentials.password, ct);
        var second = await console.RequestDeleteAccount(session.Credentials.password, ct);
        var me     = await console.GetMe(ct);

        Assert.That(first.success, Is.True, first.error.ToString());

        Assert.Multiple(() =>
        {
            Assert.That(second.success, Is.False);
            Assert.That(second.error, Is.EqualTo(DeleteAccountError.AlreadyScheduled));
            Assert.That(me.deletionExecutionAt, Is.EqualTo(first.executionAt),
                "asking twice moved the deadline");
            Assert.That(second.executionAt, Is.EqualTo(first.executionAt),
                "the refusal drops the deadline the grain handed back, so the console has nothing to show");
        });

        // Left scheduled on purpose: the grain's own two-second timer will execute it, which is the
        // production path and costs this fixture nothing.
    }

    /// <summary>
    /// A request made after the account is already gone says so, rather than promising a deletion.
    /// </summary>
    /// <remarks>
    /// <para><b>Observed.</b> <c>RequestDeletionAsync</c> collapses <c>Executing</c> and
    /// <c>Completed</c> into <c>AlreadyScheduled</c>
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:74</c>), and the console maps that to
    /// "your account is already scheduled for deletion". For an account that has already been erased
    /// that sentence is false in a way that matters: it tells the person their data is still there and
    /// still cancellable. <c>DeleteAccountError</c> has no value for "already gone", though
    /// <c>CancelDeleteError</c> does.</para>
    ///
    /// <para><b>Adjudicated a design question, not an agreed defect (campaign verdict
    /// <c>ACC-14</c>).</b> The review reproduced the mechanism above and then declined to call it a
    /// bug: the reviewer called the collapsed enum copy-only — no banner, no Cancel button, no state change
    /// — and a finer refusal an additive contract decision (an <c>AlreadyDeleted</c> arm on both
    /// enums, regenerated with ionc), not a silent fix. The confusing screen behind it belongs to the
    /// missing post-deletion revocation.
    /// The test stays red and keeps <c>[Category("KnownPresenceBug")]</c> so the default run
    /// excludes it: it pins a decision the product still owes, and it goes green the day that
    /// decision is made and implemented.</para>
    /// </remarks>
    [Test, Category("KnownPresenceBug"), CancelAfter(120_000)]
    public async Task A_request_after_completion_says_the_account_is_already_gone(CancellationToken ct = default)
    {
        var (consoleScope, console) = AccountConsoleHarness.Console(
            scene.VictimId, scene.Credentials.displayName, scene.VictimSession.SessionId);

        await using var scope = consoleScope;

        var again = await console.RequestDeleteAccount(scene.Credentials.password, ct);
        var me    = await console.GetMe(ct);

        Assert.Multiple(() =>
        {
            Assert.That(again.success, Is.False);
            Assert.That(me.deletionStatus, Is.EqualTo(DeletionStatusKind.Completed),
                "the console cannot tell that the account it is looking at has already been deleted");
            Assert.That(again.error, Is.Not.EqualTo(DeleteAccountError.AlreadyScheduled),
                "an account that has already been erased is told its deletion is 'already scheduled', " +
                "which reads as 'your data is still here and you can still cancel'");
        });
    }

    // ── D10: cancel ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Cancelling is refused when there is nothing to cancel, and when it is too late.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task Cancel_is_refused_when_nothing_is_scheduled_and_when_it_is_too_late(CancellationToken ct = default)
    {
        var fresh = await CreateSessionAsync(ct);

        var (freshScope, freshConsole) = AccountConsoleHarness.Console(fresh);
        CancelDeleteResult nothingScheduled;
        await using (freshScope)
            nothingScheduled = await freshConsole.CancelDeleteAccount(ct);

        var (goneScope, goneConsole) = AccountConsoleHarness.Console(
            scene.VictimId, scene.Credentials.displayName, scene.VictimSession.SessionId);
        CancelDeleteResult alreadyGone;
        await using (goneScope)
            alreadyGone = await goneConsole.CancelDeleteAccount(ct);

        Assert.Multiple(() =>
        {
            Assert.That(nothingScheduled.success, Is.False);
            Assert.That(nothingScheduled.error, Is.EqualTo(CancelDeleteError.NotScheduled));

            Assert.That(alreadyGone.success, Is.False,
                "an account that has already been erased was told its deletion was cancelled");
            Assert.That(alreadyGone.error, Is.EqualTo(CancelDeleteError.AlreadyCompleted));
        });
    }

    // ── D10 + D11: cancel, reminders, and the second attempt ────────────────────────────────────

    /// <summary>
    /// Cancelling stops the reminders and the execution, and asking again starts the whole clock over.
    /// </summary>
    /// <remarks>
    /// <para>Three properties in one scenario because they are one scenario: a person is warned, thinks
    /// better of it, cancels, and later changes their mind again. What has to hold is that the
    /// cancellation is total — no further warning mail, and above all no execution once the original
    /// deadline passes — and that the second request is a fresh start rather than a resumption, so the
    /// warnings arrive again instead of being suppressed by the bookkeeping of the first attempt.</para>
    ///
    /// <para>The poll is driven by hand rather than left to the grain's timer, exactly as
    /// <see cref="AccountConsoleHarness.DriveDeletionUntilAsync"/> explains: both are running, and
    /// which one crosses a threshold first is a race that the counts below are immune to.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task Cancelling_stops_the_reminders_and_the_execution_and_a_second_request_starts_over(
        CancellationToken ct = default)
    {
        var thresholds = AccountTimings.Reminders;

        Assert.That(thresholds, Has.Count.GreaterThanOrEqualTo(2),
            "the host has fewer than two reminder thresholds, so the reset below is untestable");

        var session = await CreateSessionAsync(ct);
        var email   = session.Credentials.email;
        var grain   = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var first = await console.RequestDeleteAccount(session.Credentials.password, ct);
        Assert.That(first.success, Is.True, first.error.ToString());

        // Wait for exactly one reminder, then call it off — the point in the scenario where a person
        // gets the warning mail and decides they did not mean it.
        var warned = await Poll.ForValueAsync(
            async () =>
            {
                await grain.CheckAndExecuteAsync();
                return AccountTimings.Emails.Sent(email, EmailKinds.DeletionReminder);
            },
            sent => sent.Count >= 1, AccountTimings.GraceAndABit, TimeSpan.FromMilliseconds(100), ct);

        Assert.That(warned, Has.Count.EqualTo(1),
            $"the first reminder did not arrive alone on crossing {thresholds[0]}");

        var cancelled = await console.CancelDeleteAccount(ct);
        Assert.That(cancelled.success, Is.True, cancelled.error.ToString());

        var cancellationMail = await AccountTimings.Emails.WaitForAsync(
            email, EmailKinds.DeletionCancelled, AccountTimings.Slack, ct);

        // Ride out the original deadline with the poll running. A fixed wait, and the right kind:
        // what is being asserted is that nothing happens, and nothing has no edge to poll for.
        var deadline = DateTimeOffset.UtcNow + AccountTimings.GraceAndABit;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await grain.CheckAndExecuteAsync();
            await Task.Delay(200, ct);
        }

        var afterCancel = await console.GetMe(ct);
        var quiet       = AccountTimings.Emails.Sent(email, EmailKinds.DeletionReminder);
        var stillThere  = await AccountSeed.IsVisibleAsync(session.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(cancellationMail, Is.Not.Null, "no 'deletion cancelled' mail reached the sink");
            Assert.That(afterCancel.deletionStatus, Is.EqualTo(DeletionStatusKind.None));
            Assert.That(afterCancel.deletionExecutionAt, Is.Null,
                "the console still shows a deadline for a deletion that was called off");
            Assert.That(quiet, Has.Count.EqualTo(1),
                "a reminder was sent after the deletion had been cancelled");
            Assert.That(stillThere, Is.True,
                "the account was deleted anyway, past a deadline it had already cancelled");
        });

        // And the second attempt is a fresh start: both thresholds have to fire again.
        var second = await console.RequestDeleteAccount(session.Credentials.password, ct);
        Assert.That(second.success, Is.True, second.error.ToString());

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, AccountTimings.GraceAndABit + AccountTimings.ExecutionBudget, ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the re-requested deletion did not finish; status was {reached}");

        Assert.That(AccountTimings.Emails.Sent(email, EmailKinds.DeletionReminder),
            Has.Count.EqualTo(1 + thresholds.Count),
            "the second attempt did not warn the person again — the reminder bookkeeping of the " +
            "cancelled attempt is still suppressing them");
    }

    // ── D12: the failure path ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// A deletion that fails part-way leaves a working account and a request that can be made again.
    /// </summary>
    /// <remarks>
    /// <para><b>How the failure is made deterministic.</b> <c>AnonymizeUserAsync</c> rewrites the row's
    /// e-mail to <c>deleted_{userId}@void.local</c>, and <c>NormalizedEmail</c> is a computed column
    /// with a unique index on it (<c>src/Argon.Core/Entities/Data/UserEntity.cs:118</c>). Parking that
    /// exact address on another row makes step 2's <c>SaveChangesAsync</c> violate the index, which is
    /// the only step of the ten that a test can fault from outside without touching product code. It
    /// faults before the write commits, so this is the <em>gentlest</em> possible failure: the account
    /// is untouched. Every worse one — a fault in steps 3 to 8, which run after the identity has
    /// already been rewritten — leaves an account that cannot log in, cannot be retried, and holds all
    /// of its private rows; that ordering is defect D12/H21 and this test cannot reach it.</para>
    ///
    /// <para><b>What is asserted.</b> That a failure is recorded with a reason, that the console can
    /// see it, that the account still works, and that the person can ask again once the cause is gone.
    /// The last is the one at risk: <c>Failed</c> matches neither guard in
    /// <c>RequestDeletionAsync</c>, so the retry falls through to a <c>db.Users</c> lookup that the
    /// soft-delete filter hides for any failure that got as far as anonymising — permanent
    /// <c>InternalError</c> for those accounts, with the console's own banner
    /// (<c>MasterPage.vue:19</c>) rendering <c>Failed</c> as "nothing scheduled" and offering the
    /// button that will keep failing.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_deletion_that_fails_leaves_a_working_account_and_can_be_asked_for_again(
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var decoy   = await CreateSessionAsync(ct);

        var collision = $"deleted_{session.UserId}@void.local";

        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, collision), ct);

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var requested = await console.RequestDeleteAccount(session.Credentials.password, ct);
        Assert.That(requested.success, Is.True, requested.error.ToString());

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Failed,
            AccountTimings.GraceAndABit + AccountTimings.ExecutionBudget, ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Failed),
            $"the seeded collision did not fault the deletion; it ended in {reached}. " +
            "This test's premise is gone — nothing below is meaningful.");

        var status = await grain.GetDeletionStatusAsync();
        var me     = await console.GetMe(ct);

        // The account has to be usable: a deletion that failed is a deletion that did not happen.
        await using var loginScope = FactoryAsp.Services.CreateAsyncScope();
        var login = await GetIdentityService(loginScope.ServiceProvider).Authorize(
            new UserCredentialsInput(session.Credentials.email, null, null, session.Credentials.password, null, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(status.FailureReason, Is.Not.Null.And.Not.Empty,
                "the deletion failed without recording why");
            Assert.That(me.deletionStatus, Is.EqualTo(DeletionStatusKind.Failed),
                "the console cannot tell that the deletion failed");
            Assert.That(login, Is.InstanceOf<SuccessAuthorize>(),
                $"a failed deletion left the account unable to sign in: {(login as FailedAuthorize)?.error}");
        });

        // Clear the cause and ask again — the recovery a person would be told to attempt.
        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, decoy.Credentials.email), ct);

        var retried = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.That(retried.success, Is.True,
            $"a deletion that failed cannot be asked for again: {retried.error}. The account is left " +
            "in Failed for ever, and the console's banner renders Failed as 'nothing scheduled'.");
    }

    // ── D13: an interrupted execution ───────────────────────────────────────────────────────────

    /// <summary>
    /// An execution interrupted by a silo restart resumes, or at least stops holding the account hostage.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (defect ACC-15, fixed).</b> <c>ExecuteDeletionAsync</c> writes
    /// <c>Executing</c> before it does any work and anonymises the row as its third step, so losing
    /// the activation — a deploy, an eviction, an OOM — used to leave an account that could not sign
    /// in, could not be cancelled (<c>AlreadyExecuting</c>), could not be re-requested
    /// (<c>AlreadyScheduled</c>) and still held every private row the run had not reached, with the
    /// console rendering the same "deletion scheduled" banner it renders for a healthy schedule and no
    /// operator surface anywhere near the grain. It resumes now: every step records itself in
    /// <c>AccountDeletionGrainState.StepsDone</c>, <c>CheckAndExecuteAsync</c> re-enters the execution
    /// from that cursor for <c>Executing</c> as well as for a bounded number of <c>Failed</c> retries,
    /// and the poll is a durable Orleans reminder rather than a grain timer — because after step 3
    /// nobody can sign in to touch the grain, so a trigger that only exists while an activation does
    /// is a trigger that never fires again.</para>
    ///
    /// <para>The cursor is what makes resumption safe rather than a second kind of corruption:
    /// <c>DecrementFileRefsAsync</c> is not idempotent, and a second unconditional pass would release
    /// every owned file again.</para>
    ///
    /// <para><b>Why the state is seeded.</b> There is no hook in the suite to deactivate a grain
    /// mid-turn, and every exception inside the execution is caught and turned into <c>Failed</c>, so
    /// no sequence of product calls can produce a stranded <c>Executing</c>. What a crash leaves
    /// behind is a storage row, and that a test can write: this puts exactly the record the grain
    /// itself wrote at <c>:375</c> into <c>account-deletion-store</c> before the grain has ever been
    /// activated, so the activation that follows is the one a restarted silo would have had.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_interrupted_execution_does_not_strand_the_account(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var now     = DateTimeOffset.UtcNow;

        await WriteDeletionStateAsync(session.UserId, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Executing,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);
        var seeded = await grain.GetDeletionStatusAsync();

        Assert.That(seeded.Status, Is.EqualTo(AccountDeletionStatusKind.Executing),
            "the interrupted execution could not be seeded into the grain's own store, so this test " +
            "is not observing what it claims to");

        // The poll the timer would make, and the one a restarted silo would make on its next tick. A
        // resumption starts on the first of them, so the budget is a handful of polls rather than a
        // whole execution: waiting longer would only make the same absence cost more.
        var resumed = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, AccountTimings.GraceAndABit, ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(session);
        await using var scope = consoleScope;

        var cancelled = await console.CancelDeleteAccount(ct);
        var requested = await console.RequestDeleteAccount(session.Credentials.password, ct);

        Assert.That(
            resumed == AccountDeletionStatusKind.Completed || cancelled.success || requested.success,
            Is.True,
            $"an execution interrupted by a restart is stranded: it does not resume (status " +
            $"{resumed}), it cannot be cancelled ({cancelled.error}) and it cannot be re-requested " +
            $"({requested.error}). The account stays half-erased with no way out.");
    }

    // ── R2: the date of birth ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The date of birth goes with the rest of the identity, on both rows that can hold one.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract.</b> A birth date is personal data by the product's own reckoning:
    /// <c>UserDataExportGrain.CollectProfileAsync</c> writes it into <c>profile.json</c> as part of
    /// the Article 15 answer, and the admin console renders it. The erasure cleared eleven profile
    /// columns and both contact columns and left this one on both rows — and neither row ever goes
    /// away: <c>Users</c> is anonymised in place, and <c>UserProfiles</c> is neither anonymised nor
    /// soft-deleted, so it stays readable through the ordinary filtered query for ever.</para>
    ///
    /// <para><b>Why it was reachable and not merely present.</b> <c>CleanupConversationsAsync</c>
    /// removes the erased account's <c>UserConversations</c> row while the conversation itself
    /// survives by design, so a former DM peer keeps an anchor: <c>UserInteraction.LookupProfile</c>
    /// passes <c>SocialReach</c>, <c>UserGrain.GetMyProfile</c> reads the profile row, and
    /// <c>UserProfileEntity.Map</c> projects the birth date straight into the answer.
    /// <c>LookupUser</c> is clean — it returns the tombstone — but its sibling on the same gate was
    /// not.</para>
    ///
    /// <para><b>Why the profile column is seeded.</b> Registration puts the birth date on the
    /// <c>Users</c> row only, and nothing in the product writes the profile copy yet. Seeding it is
    /// the difference between asserting that the erasure clears the column and asserting that today's
    /// writers happen not to fill it — the column is projected into the client's
    /// <c>ArgonUserProfile</c> and into the export whoever fills it in, and an erasure that depends on
    /// a column staying empty is not an erasure.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task The_date_of_birth_does_not_survive_the_erasure(CancellationToken ct = default)
    {
        var       session   = await CreateSessionAsync(ct);
        DateOnly? seededDob = new DateOnly(1994, 3, 17);

        await using (var db = await AccountSeed.NewDbAsync(ct))
        {
            await db.Users.IgnoreQueryFilters()
               .Where(u => u.Id == session.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.DateOfBirth, seededDob), ct);

            await db.UserProfiles.IgnoreQueryFilters()
               .Where(profile => profile.UserId == session.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(profile => profile.DateOfBirth, seededDob), ct);
        }

        var before = await BirthDatesAsync(session.UserId, ct);

        Assert.That(before, Is.EqualTo((seededDob, seededDob)),
            "the birth date could not be seeded on both rows, so nothing below is observing what it claims to");

        await ScheduleAndExecuteAsync(session.UserId, session.Credentials.password, ct);

        var after = await BirthDatesAsync(session.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(after.User, Is.Null,
                "the erased account's date of birth is still on its Users row, beside a nulled phone " +
                "number and a freed e-mail address");
            Assert.That(after.Profile, Is.Null,
                "the erased account's date of birth is still on its profile row, which is neither " +
                "anonymised nor soft-deleted — a former DM peer's LookupProfile still answers with it");
        });
    }

    // ── R10 + R5: what spends the retry budget, and the way back from spending it ────────────────

    /// <summary>
    /// An execution that resumes after a lost activation costs the account none of its retry budget.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract.</b> The bound on <c>ExecuteDeletionAsync</c> exists to stop a run that
    /// cannot make progress from retrying for the rest of the deployment's life. It used to be raised
    /// on entry, which charged a resumption exactly what a crash costs — and a resumption is not a
    /// failure, it is the ACC-15 recovery working. A grace that elapses during a rolling deploy is
    /// enough: the reminder fires, the execution runs some steps, the silo drains, no exception is
    /// thrown anywhere, and the next activation resumes into the second node of the same rollout. Two
    /// of three attempts gone with nothing wrong. One ordinary transient database error then exhausted
    /// the bound, the poll was unregistered for good, and the account — anonymised at step 3, still
    /// holding everything steps 5 to 9 erase — was left with nothing that would ever come back to it.
    /// So attempts are spent by failing, and a step that records itself gives them back.</para>
    ///
    /// <para><b>How the interruption is produced.</b> The same way
    /// <see cref="An_interrupted_execution_does_not_strand_the_account"/> produces one, and for the
    /// same reason: no sequence of product calls can leave an activation half way through the
    /// execution, but a crashed silo leaves a storage row, and that a test can write. <c>StepsDone</c>
    /// carrying the first two steps is exactly what a run interrupted between step 2 and step 3
    /// persisted.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_resumption_after_a_lost_activation_costs_no_attempt(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var now     = DateTimeOffset.UtcNow;

        // Steps are stable integers on the state by design (AccountDeletionGrain.Step): 1 is the
        // session teardown and 2 the archive purge, so this is a run lost on its way into step 3.
        await WriteDeletionStateAsync(session.UserId, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Executing,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            StepsDone           = [1, 2],
            ExecutionAttempts   = 0,
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed,
            AccountTimings.GraceAndABit + AccountTimings.ExecutionBudget, ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the interrupted execution did not resume; status was {reached}");

        var status = await GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId).GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(status.ExecutionAttempts, Is.Zero,
                "a resumption that never failed spent an attempt from the retry budget; three deploy " +
                "restarts would exhaust it with no error anywhere and strand a half-erased account");
            Assert.That(status.Stranded, Is.False,
                "a completed erasure is reported as stranded");
        });
    }

    /// <summary>
    /// A deletion that keeps failing gives up visibly, and an operator can start it again.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract.</b> Giving up is right — an erasure the grain cannot finish must stop
    /// hammering the tables — but the state it settles into used to be invisible and terminal. The
    /// account holder cannot reach it once step 3 has run: there is no password digest left and the
    /// address is <c>deleted_{id}@void.local</c>, so no session can be minted. The operator queue
    /// erases its own record of it, because reconciliation retires an entry whose account is no longer
    /// proposed and an anonymised row is not proposed. Nothing else in the product reads
    /// <c>GetDeletionStatusAsync</c>. What was left of a half-erased account was a metric and a log
    /// line. So the status now carries the attempt count and a <c>Stranded</c> flag for an operator
    /// surface to find, and <c>ResumeAsync</c> is the call that starts it again.</para>
    ///
    /// <para><b>How the failure is made deterministic</b>, and it is the trick
    /// <see cref="A_deletion_that_fails_leaves_a_working_account_and_can_be_asked_for_again"/>
    /// documents: <c>NormalizedEmail</c> is a computed column with a unique index, so parking
    /// <c>deleted_{userId}@void.local</c> on another row makes the anonymising step violate it. It
    /// faults the same way every time and it faults before its own write commits, which is what lets
    /// the recovery half of this test finish the erasure for real once the collision is cleared.</para>
    ///
    /// <para>The count is asserted against the host's own <c>MaxExecutionAttempts</c> rather than
    /// against 3: the point is that the bound is reached by that many <em>failures</em>, whatever the
    /// bound is.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_deletion_that_gives_up_is_visible_and_can_be_resumed(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var decoy   = await CreateSessionAsync(ct);
        var bound   = AccountTimings.Deletion.MaxExecutionAttempts;

        var collision = $"deleted_{session.UserId}@void.local";

        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, collision), ct);

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);

        var requested = await grain.RequestDeletionAsync(session.Credentials.password);
        Assert.That(requested.Success, Is.True, requested.Error?.ToString());

        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var gaveUp = await Poll.ForValueAsync(
            async () =>
            {
                await grain.CheckAndExecuteAsync();
                return await grain.GetDeletionStatusAsync();
            },
            reported => reported.Stranded,
            AccountTimings.ExecutionBudget, AccountTimings.Slack / 4, ct);

        Assert.Multiple(() =>
        {
            Assert.That(gaveUp.Stranded, Is.True,
                $"the deletion never reported that it had given up; it is {gaveUp.Status} after " +
                $"{gaveUp.ExecutionAttempts} attempt(s) with reason '{gaveUp.FailureReason}'");
            Assert.That(gaveUp.Status, Is.EqualTo(AccountDeletionStatusKind.Failed));
            Assert.That(gaveUp.ExecutionAttempts, Is.EqualTo(bound),
                "the bound was not reached by exactly one attempt per failure");
            Assert.That(gaveUp.FailureReason, Is.Not.Null.And.Not.Empty,
                "a deletion gave up without recording why");
        });

        // And having given up, it stays given up: no further poll touches it.
        await grain.CheckAndExecuteAsync();
        await grain.CheckAndExecuteAsync();

        var settled = await grain.GetDeletionStatusAsync();

        Assert.That(settled.ExecutionAttempts, Is.EqualTo(bound),
            "a deletion that has exhausted its attempts is still being executed by the poll");

        // The operator's way back in: clear the cause, resume, and the erasure finishes from its cursor.
        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, decoy.Credentials.email), ct);

        var resumed = await grain.ResumeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(resumed.Stranded, Is.False,
                "a resumed deletion still reports itself as stranded, so an operator surface cannot " +
                "tell that anything happened");
            Assert.That(resumed.ExecutionAttempts, Is.Zero,
                "the resumption did not give the erasure its attempts back, so it gives up again at once");
        });

        var finished = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        Assert.That(finished, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the resumed deletion did not finish; status was {finished}");

        Assert.That(await AccountSeed.IsVisibleAsync(session.UserId, ct), Is.False,
            "the resumed deletion reported success without erasing the account");
    }

    // ── R11: what a cancellation may still call off ─────────────────────────────────────────────

    /// <summary>
    /// A failure that has already started erasing cannot be cancelled away.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract.</b> Cancelling is total by design: it clears the schedule, the reminder
    /// bookkeeping, the step cursor, the attempt count and the three <c>Original*</c> identity fields,
    /// and it disarms the poll. That is exactly right for a countdown nobody has acted on, and it is
    /// destruction for a run that threw part-way. Such an account is already anonymised, its
    /// credentials are already revoked and its export archive is already destroyed, while its friend
    /// requests, passkeys, device history, pending contact changes, payments and memberships are
    /// untouched. A console session outlives the erasure (<c>AccountConsoleAuthInterceptor</c>
    /// validates an Aegis token and never loads the user row), so the person could press "cancel",
    /// be told it worked, and leave the grain believing nothing had ever happened — cursor gone,
    /// identity gone, poll disarmed, and <c>RequestDeletionAsync</c> answering <c>AlreadyScheduled</c>
    /// for ever on the anonymised row. Nothing in the product could finish or undo that account
    /// afterwards.</para>
    ///
    /// <para>So a failure is cancellable only while it has touched nothing. Both halves of "has it
    /// begun" are asserted, because they answer differently: the cursor is the ordinary answer, and
    /// the row is the one that catches a record written by a build without a cursor, or a step whose
    /// database write committed before its state write did.</para>
    ///
    /// <para><b>Why the state is seeded.</b> Every exception inside the execution is caught, and the
    /// one failure a test can inject from outside — the e-mail collision — faults the anonymising step
    /// before its write commits, so no sequence of product calls produces a failure that got further.
    /// What such a run leaves behind is a storage row, and that a test can write, exactly as
    /// <see cref="An_interrupted_execution_does_not_strand_the_account"/> does. The attempt count is
    /// seeded at the bound so the grain does not re-arm its poll and race the assertions.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_failed_deletion_that_has_already_begun_erasing_cannot_be_cancelled_away(
        CancellationToken ct = default)
    {
        var byCursor = await CreateSessionAsync(ct);
        var byRow    = await CreateSessionAsync(ct);
        var bound    = AccountTimings.Deletion.MaxExecutionAttempts;
        var now      = DateTimeOffset.UtcNow;

        // (a) the cursor says three steps ran — sessions ended, archive destroyed, row anonymised.
        await WriteDeletionStateAsync(byCursor.UserId, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Failed,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            StepsDone           = [1, 2, 3],
            ExecutionAttempts   = bound,
            FailureReason       = "seeded: the run threw after the row was anonymised",
            OriginalEmail       = byCursor.Credentials.email,
            OriginalUsername    = byCursor.Credentials.username,
            OriginalDisplayName = byCursor.Credentials.displayName
        });

        // (b) no cursor at all, but the row is anonymised — a step whose commit outlived its state
        // write, or a record from a build that had no cursor.
        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.IgnoreQueryFilters()
               .Where(u => u.Id == byRow.UserId)
               .ExecuteUpdateAsync(set => set
                   .SetProperty(u => u.IsDeleted, true)
                   .SetProperty(u => u.DeletedAt, DateTimeOffset.UtcNow), ct);

        await WriteDeletionStateAsync(byRow.UserId, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Failed,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            StepsDone           = [],
            ExecutionAttempts   = bound,
            FailureReason       = "seeded: the run threw with nothing recorded",
            OriginalEmail       = byRow.Credentials.email,
            OriginalUsername    = byRow.Credentials.username,
            OriginalDisplayName = byRow.Credentials.displayName
        });

        var cursorGrain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(byCursor.UserId);
        var rowGrain    = GetGrainFactory().GetGrain<IAccountDeletionGrain>(byRow.UserId);

        var refusedByCursor = await cursorGrain.CancelDeletionAsync();
        var refusedByRow    = await rowGrain.CancelDeletionAsync();

        var afterCursor = await ReadDeletionStateAsync(byCursor.UserId);
        var afterRow    = await ReadDeletionStateAsync(byRow.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(refusedByCursor.Success, Is.False,
                "a half-executed erasure was cancelled: the account is anonymised, its remaining " +
                "private rows are untouched, and the cursor that says so has just been thrown away");
            Assert.That(refusedByCursor.Error, Is.EqualTo(AccountDeletionCancelError.AlreadyExecuting));

            Assert.That(refusedByRow.Success, Is.False,
                "an erasure whose anonymising step committed without recording itself was cancelled; " +
                "the account can no longer sign in and nothing will ever finish erasing it");
            Assert.That(refusedByRow.Error, Is.EqualTo(AccountDeletionCancelError.AlreadyExecuting));

            Assert.That(afterCursor.Status, Is.EqualTo(AccountDeletionStatus.Failed),
                "the refused cancellation reset the status anyway");
            Assert.That(afterCursor.StepsDone, Is.EquivalentTo(new[] { 1, 2, 3 }),
                "the refused cancellation threw away the cursor a resumption reads");
            Assert.That(afterCursor.OriginalUsername, Is.EqualTo(byCursor.Credentials.username),
                "the refused cancellation threw away the identity a resumption needs for the " +
                "username reservation and the completion mail");
            Assert.That(afterRow.Status, Is.EqualTo(AccountDeletionStatus.Failed));
        });
    }

    /// <summary>
    /// A failure that never touched anything is still the person's to call off.
    /// </summary>
    /// <remarks>
    /// The other side of
    /// <see cref="A_failed_deletion_that_has_already_begun_erasing_cannot_be_cancelled_away"/>, and
    /// the reason the refusal is written as "has the erasure begun" rather than as "is the status
    /// Failed". A run that threw before its first step — the reminder fired, the first database call
    /// timed out — has done nothing to the account, and the person who no longer wants to be deleted
    /// must not be told their only option is to let it finish. The reset is exactly right there, and
    /// this test is what keeps the guard from growing into a blanket refusal.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_failure_that_touched_nothing_can_still_be_called_off(CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var now     = DateTimeOffset.UtcNow;

        await WriteDeletionStateAsync(session.UserId, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Failed,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            StepsDone           = [],
            ExecutionAttempts   = AccountTimings.Deletion.MaxExecutionAttempts,
            FailureReason       = "seeded: the run threw before its first step",
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        var grain     = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);
        var cancelled = await grain.CancelDeletionAsync();
        var status    = await grain.GetDeletionStatusAsync();

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Success, Is.True,
                $"a deletion that failed before touching anything could not be called off: {cancelled.Error}");
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.None));
            Assert.That(status.Stranded, Is.False);
        });

        Assert.That(await AccountSeed.IsVisibleAsync(session.UserId, ct), Is.True,
            "the account is gone, so the failure this test seeded was not the harmless one it claims");
    }

    /// <summary>
    /// A failure that only ended the sessions and destroyed the archive is still the person's to
    /// call off.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (finding F1).</b> The refusal above is about irreversibility, and the
    /// first two steps of an execution are not it. Step 1 revokes credentials — a revocation floor and
    /// per-session tombstones, none of which touches a row — and step 2 destroys the export archive,
    /// which is a derived copy the person can ask for again. Neither changes anything about the
    /// account, so a run that got no further is a countdown that has done nothing, and cancelling it
    /// is exactly what cancelling is for.</para>
    ///
    /// <para><b>What refusing them cost.</b> The guard used to answer "has begun" for any recorded
    /// step at all, and the state below is not hypothetical: an export bucket that answers 503 leaves
    /// precisely <c>StepsDone = {1}</c> with <c>Failed</c> on an intact row, so the person signs in
    /// normally, opens the console, is told a deletion is under way, and is refused as
    /// <c>AlreadyExecuting</c> when they press cancel. Once the attempt bound was spent nothing polled
    /// the grain again, so that state was permanent, and the only escape was the accidental one — ask
    /// for the deletion again, which resets the cursor, and cancel <em>that</em>.</para>
    ///
    /// <para><b>The sign-out is not undone, and the assertion on the floor is deliberate.</b> A
    /// cancellation after step 1 leaves the account signed out everywhere: the floor is a watermark on
    /// issuance time, so a fresh login mints credentials above it and works, and lifting it would
    /// silently re-honour every other revocation the account has ever earned — a password change, a
    /// stolen-device sign-out. What the person gets back is their account; what they have to do is
    /// sign in again. This test pins both halves.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_failure_that_only_prepared_the_erasure_can_still_be_called_off(
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var cache   = FactoryAsp.Services.GetRequiredService<IArgonCacheDatabase>();
        var now     = DateTimeOffset.UtcNow;

        // What step 1 leaves behind, written the way InvalidateSessionsAsync writes it: the account's
        // live credentials are dead and nothing about the row has changed.
        await cache.StringSetAsync(
            SessionRevocation.FloorKey(session.UserId),
            now.ToUnixTimeSeconds().ToString(),
            SessionRevocation.Window, ct);

        await WriteDeletionStateAsync(session.UserId, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Failed,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            StepsDone           = [1, 2],
            ExecutionAttempts   = AccountTimings.Deletion.MaxExecutionAttempts,
            FailureReason       = "seeded: the export store answered 503 on step 2",
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        var grain     = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);
        var cancelled = await grain.CancelDeletionAsync();
        var status    = await grain.GetDeletionStatusAsync();
        var user      = await AccountSeed.ReadUserAsync(session.UserId, ct);
        var floor     = await cache.StringGetAsync(SessionRevocation.FloorKey(session.UserId), ct);

        Assert.That(user, Is.Not.Null,
            "the seeded failure erased the account, so it was not the reversible one this test is about");

        Assert.Multiple(() =>
        {
            Assert.That(cancelled.Success, Is.True,
                "a deletion that revoked the sessions and destroyed the archive — and erased nothing " +
                $"about the account — could not be called off: {cancelled.Error}. The row is intact, " +
                "the person is signed in, and the product is telling them their only option is to let " +
                "the deletion finish");
            Assert.That(status.Status, Is.EqualTo(AccountDeletionStatusKind.None));
            Assert.That(status.Stranded, Is.False);

            Assert.That(user!.Username, Is.EqualTo(session.Credentials.username),
                "the account this test claims is intact has already been anonymised");
            Assert.That(user.IsDeleted, Is.False);

            Assert.That(floor, Is.Not.Null,
                "the cancellation lifted the sign-out floor. It is a watermark over every credential " +
                "issued before it, not a bar on the account, so lifting it would re-honour the " +
                "password changes and stolen-device sign-outs it also covers; the person signs in again");
        });
    }

    // ── R3: the archive purge and the cursor ────────────────────────────────────────────────────

    /// <summary>
    /// An attempt that did not record the archive purge is followed by one that performs it.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract.</b> Step 2 destroys everything under <c>exports/{userId}/</c> — a
    /// finished archive, and the intermediates a failed export leaves behind, which are
    /// <c>profile.json</c> with an e-mail, a phone number and a birth date, and <c>devices.json</c>
    /// with up to a hundred IP addresses. It used to wrap that delete in a catch that logged and
    /// returned, and <c>RunStepAsync</c> records a step whenever the body returns: an export bucket
    /// that answered one request with an error marked the archives purged, the run continued to
    /// <c>Completed</c>, and no later resumption ever looked at step 2 again. The grain's own remarks
    /// claimed the retry that the catch was preventing. The delete is now allowed to fail the attempt,
    /// which leaves the step unrecorded — and this test is what that is worth: an attempt that did not
    /// record the purge is followed by one that carries it out.</para>
    ///
    /// <para><b>Why the failure itself is not injected.</b> There is no seam for
    /// <c>IExportS3Service</c> in the integration host, and the bucket behind it is shared by every
    /// fixture in the process, so making one call to object storage fail would mean breaking storage
    /// for whatever else is running. What is reproducible is the state such a failure now leaves —
    /// <c>Executing</c> with step 1 recorded and step 2 not — and that is what is seeded, the same
    /// technique <see cref="An_interrupted_execution_does_not_strand_the_account"/> uses. Under the
    /// old code that state was unreachable after a swallowed failure, which is the whole finding.</para>
    ///
    /// <para>The objects are written straight into the bucket rather than by running an export: an
    /// export would activate the deletion grain through <c>RequestExportAsync</c>'s status read, and
    /// the seeded record has to be in place before the grain is ever activated or the activation
    /// simply overwrites it.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task An_archive_purge_the_cursor_never_recorded_is_run_by_the_next_attempt(
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var storage = FactoryAsp.Services.GetRequiredService<IExportS3Service>();
        var prefix  = $"exports/{session.UserId}/";
        var exportId = Guid.CreateVersion7();

        await storage.PutObjectAsync($"{prefix}{exportId}/export-2026-01-01.zip",
            new MemoryStream("not really a zip, and that is not what is being asserted"u8.ToArray()), "application/zip", ct);

        await storage.PutObjectAsync($"{prefix}{exportId}/intermediate/profile.json",
            new MemoryStream("{\"Email\":\"someone@example.com\"}"u8.ToArray()), "application/json", ct);

        Assert.That(await storage.ListObjectsAsync(prefix, ct), Has.Count.EqualTo(2),
            "the archive could not be seeded into the export bucket, so there is nothing here for a " +
            "purge to miss");

        var now = DateTimeOffset.UtcNow;

        // Exactly what a purge that threw now leaves: the sessions step recorded, the export step not.
        await WriteDeletionStateAsync(session.UserId, new AccountDeletionGrainState
        {
            Status              = AccountDeletionStatus.Executing,
            ScheduledAt         = now - AccountTimings.Grace,
            ExecutionAt         = now - AccountTimings.Slack,
            StepsDone           = [1],
            OriginalEmail       = session.Credentials.email,
            OriginalUsername    = session.Credentials.username,
            OriginalDisplayName = session.Credentials.displayName
        });

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed,
            AccountTimings.GraceAndABit + AccountTimings.ExecutionBudget, ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the resumed execution did not finish; status was {reached}");

        var left  = await storage.ListObjectsAsync(prefix, ct);
        var state = await ReadDeletionStateAsync(session.UserId);

        Assert.Multiple(() =>
        {
            Assert.That(left, Is.Empty,
                "the erased account's export archive is still in the bucket: the attempt that skipped " +
                "the purge recorded it as done, so no later one ever came back to it");
            Assert.That(state.StepsDone, Does.Contain(2),
                "the purge ran but was not recorded, so a later resumption would run it again");
        });
    }

    // ── R22: a departure the space committed and never announced ────────────────────────────────

    /// <summary>
    /// A departure whose membership row was already removed is announced by the next attempt.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (finding F4, R22's residual half).</b> <c>SpaceGrain.RemoveMemberAsync</c>
    /// does three things in order — commit the soft-delete, invalidate the cached roster, fire
    /// <c>LeavedFromServerUser</c> — so a bus or cache outage in the middle leaves a space whose row is
    /// gone and whose members were never told. Every rule that makes the ordinary retry correct then
    /// works against that space: <c>RemoveMembershipsAsync</c> selects memberships
    /// <c>where !IsDeleted</c> and no longer sees it, and <c>RemoveMemberAsync</c> is deliberately
    /// silent once the row is gone. So the retry announced nothing, recorded step 6, and the run went
    /// on to <c>Completed</c> — no bot ever got its <c>MemberLeave</c>, and every client holding that
    /// space kept the erased member until it happened to bootstrap again. That is ACC-03 surviving on
    /// the error path, permanently, for one space out of however many an outage caught.</para>
    ///
    /// <para><b>What the fix has to be, and therefore what this asserts.</b> The debt cannot be
    /// rediscovered from the database — the row that would name it is precisely the one that is gone —
    /// so it is written into <c>AccountDeletionGrainState.PendingDepartureAnnouncements</c> when the
    /// loop sees a failure whose row committed, and replayed through
    /// <c>ISpaceGrain.AnnounceMemberLeftAsync</c> before the next attempt's walk. The observer in this
    /// test is a member of the space with a live socket, exactly like the client that would otherwise
    /// keep the erased member on its roster for ever.</para>
    ///
    /// <para><b>Why the half-failure is seeded rather than caused.</b> Making the space grain commit
    /// and then throw needs the message bus or the distributed cache to fail for one call, and both
    /// are shared with every other fixture in this process. What is reproducible is the state such a
    /// failure leaves — a soft-deleted membership, an owed announcement, and a cursor that has not
    /// reached step 6 — and that is what is written, the same technique
    /// <see cref="An_interrupted_execution_does_not_strand_the_account"/> and
    /// <see cref="An_archive_purge_the_cursor_never_recorded_is_run_by_the_next_attempt"/> use. Note
    /// what makes the assertion sharp: the walk finds nothing to do for this space, so the event can
    /// only come from the replay.</para>
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task A_departure_the_space_committed_without_announcing_is_replayed_by_the_next_attempt(
        CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var victim = await CreateSessionAsync(ct);
        var space  = await CreateSpaceAsync(owner, "R22 residual", ct);

        await JoinSpaceAsync(owner, victim, space, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(owner, ct);
        await watcher.SubscribeToSpace(space, ct);

        // Exactly what SpaceGrain.RemoveMemberAsync leaves when its ExecuteUpdateAsync commits and the
        // invalidation or the Fire that follows it throws.
        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.UsersToServerRelations
               .Where(m => m.SpaceId == space && m.UserId == victim.UserId)
               .ExecuteUpdateAsync(set => set
                   .SetProperty(m => m.IsDeleted, true)
                   .SetProperty(m => m.DeletedAt, DateTimeOffset.UtcNow), ct);

        var mark = watcher.Mark();
        var now  = DateTimeOffset.UtcNow;

        await WriteDeletionStateAsync(victim.UserId, new AccountDeletionGrainState
        {
            Status                        = AccountDeletionStatus.Executing,
            ScheduledAt                   = now - AccountTimings.Grace,
            ExecutionAt                   = now - AccountTimings.Slack,
            StepsDone                     = [1, 2],
            PendingDepartureAnnouncements = [space],
            OriginalEmail                 = victim.Credentials.email,
            OriginalUsername              = victim.Credentials.username,
            OriginalDisplayName           = victim.Credentials.displayName
        });

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            victim.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        var announced = await watcher.FirstWithinAsync<LeavedFromServerUser>(
            e => e.userId == victim.UserId && e.spaceId == space, Reaction, mark, ct);

        var state   = await ReadDeletionStateAsync(victim.UserId);
        var roster  = (await owner.Servers.GetMembers(space, ct)).Values.Select(m => m.member.userId).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
                $"the resumed execution did not finish; status was {reached} with reason " +
                $"'{state.FailureReason}'");

            Assert.That(announced, Is.Not.Null,
                "the space whose membership row was removed without an announcement was never told. " +
                "No bot got its MemberLeave and every client holding this space keeps an erased " +
                "member until it happens to bootstrap the space again");

            Assert.That(state.PendingDepartureAnnouncements, Is.Empty,
                "the announcement was replayed but the debt was not cleared, so every later attempt " +
                "fans the same departure out again");

            Assert.That(state.StepsDone, Does.Contain(6),
                "the membership step did not record itself, so the erasure cannot have got past it");

            Assert.That(roster, Does.Not.Contain(victim.UserId),
                "the roster still lists the erased account");
        });
    }

    // ── R5: an erasure nobody proposed, and where it becomes visible ────────────────────────────

    /// <summary>
    /// An erasure the account holder asked for, which gives up half way, reaches the operator queue.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract (finding F5, R5's residual half).</b> A run that spends
    /// <c>MaxExecutionAttempts</c> unregisters its own poll — right, because an erasure the grain
    /// cannot finish must stop hammering the tables — and leaves an account that is anonymised and
    /// still holding its memberships, passkeys, device history and pending contact changes. Somebody
    /// has to be able to find it, and nobody could: the person cannot sign in (step 3 took their
    /// password digest and rewrote their address), the inactivity scan will never propose a
    /// soft-deleted row, and <c>IAccountDeletionQueueGrain.ListStrandedAsync</c> — the one page built
    /// for this — read entries the queue itself had approved. Every deletion on a deployment where the
    /// sweep is off is therefore invisible, which is nearly all of them; the counter and a log line
    /// were the whole record.</para>
    ///
    /// <para>So the deletion grain reports in when it disarms itself, and the queue makes an entry
    /// where it has none. The account below is never proposed by any scan and never approved by any
    /// operator — it asks for its own deletion, exactly as a person does from the console — which is
    /// what makes the assertion mean what it says.</para>
    ///
    /// <para>The failure is made deterministic the way its neighbours make it:
    /// <c>NormalizedEmail</c> is a computed column with a unique index, so parking
    /// <c>deleted_{userId}@void.local</c> on another row makes the anonymising step violate it, every
    /// time. The collision is cleared and the erasure finished at the end, because the queue is one
    /// cluster-wide activation shared with every other fixture and a test must not leave a permanent
    /// entry — or a half-erased account — behind it.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_self_requested_erasure_that_gives_up_reaches_the_operator_queue(
        CancellationToken ct = default)
    {
        var session = await CreateSessionAsync(ct);
        var decoy   = await CreateSessionAsync(ct);
        var bound   = AccountTimings.Deletion.MaxExecutionAttempts;

        var collision = $"deleted_{session.UserId}@void.local";

        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, collision), ct);

        var grain = GetGrainFactory().GetGrain<IAccountDeletionGrain>(session.UserId);

        var requested = await grain.RequestDeletionAsync(session.Credentials.password);
        Assert.That(requested.Success, Is.True, requested.Error?.ToString());

        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var gaveUp = await Poll.ForValueAsync(
            async () =>
            {
                await grain.CheckAndExecuteAsync();
                return await grain.GetDeletionStatusAsync();
            },
            reported => reported.Stranded,
            AccountTimings.ExecutionBudget, AccountTimings.Slack / 4, ct);

        Assert.That(gaveUp.Stranded, Is.True,
            $"the deletion never gave up, so there is nothing to report; it is {gaveUp.Status} after " +
            $"{gaveUp.ExecutionAttempts} attempt(s) with reason '{gaveUp.FailureReason}'");

        // No scan and no reconciliation in between, on purpose: the queue has never heard of this
        // account and the only thing that can have told it is the deletion grain itself.
        var listed = await StrandedEntryAsync(session.UserId, ct);

        Assert.That(listed, Is.Not.Null,
            "an erasure the account holder asked for gave up half way through their account and no " +
            "operator surface lists it; the person cannot sign in to ask, and nothing else names the id");

        Assert.Multiple(() =>
        {
            Assert.That(listed!.State, Is.EqualTo(QueuedAccountDeletionState.Stranded),
                "the entry is on the stranded page in a state that says the erasure is still going");
            Assert.That(listed.Reason, Is.EqualTo(AccountDeletionQueueReasons.StrandedErasure),
                "the entry claims an inactivity reason for an account no scan ever measured");
            Assert.That(listed.ExecutionAttempts, Is.EqualTo(bound),
                "the attempt count an operator reads is not the one the deletion actually spent");
            Assert.That(listed.FailureReason, Is.Not.Null.And.Not.Empty,
                "an operator has to be told what stopped it before deciding to run it again");
            Assert.That(listed.StrandedSince, Is.Not.Null,
                "the stranded page sorts by how long an erasure has been half-finished, and this row " +
                "does not say");
        });

        // Clear the cause, finish the erasure, and leave neither a half-erased account nor a
        // permanent entry in a queue every other fixture shares.
        await using (var db = await AccountSeed.NewDbAsync(ct))
            await db.Users.Where(u => u.Id == decoy.UserId)
               .ExecuteUpdateAsync(set => set.SetProperty(u => u.Email, decoy.Credentials.email), ct);

        await grain.ResumeAsync();

        var finished = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

        Assert.That(finished, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the resumed erasure did not finish; it is {finished}");
    }

    /// <summary>The queue's stranded page, paged until <paramref name="userId"/> is found.</summary>
    /// <remarks>
    /// Paged rather than read as one list because the queue is one activation for the whole cluster
    /// and every fixture in this process shares it: the page this account is on depends on what else
    /// is stranded at the time, which is not something a test may assume.
    /// </remarks>
    private static async Task<QueuedAccountDeletion?> StrandedEntryAsync(Guid userId, CancellationToken ct)
    {
        var queue = ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IGrainFactory>()
           .GetGrain<IAccountDeletionQueueGrain>(IAccountDeletionQueueGrain.SingletonId);

        const int size = 200;

        for (var offset = 0; !ct.IsCancellationRequested; offset += size)
        {
            var page = await queue.ListStrandedAsync(offset, size);

            if (page.Entries.FirstOrDefault(entry => entry.UserId == userId) is { } found)
                return found;

            if (page.Entries.Count < size || offset + size >= page.TotalCount)
                return null;
        }

        return null;
    }

    // ── the scenario ────────────────────────────────────────────────────────────────────────────

    /// <summary>The text of the message the deleted account sent its DM peer.</summary>
    private const string DmFromVictim = "the message a deleted account wrote";

    /// <summary>
    /// Builds a fully-furnished account, deletes it through the console, and records everything an
    /// observer could see while it happened.
    /// </summary>
    private async Task<DeletedAccount> BuildTheDeletedAccountAsync()
    {
        var ct = CancellationToken.None;

        // The victim owns nothing: OwnsSpaces is an absolute bar, so the spaces belong to the
        // observer and the victim is a member of them.
        var victim    = await CreateSessionAsync(ct);
        var observer  = await CreateSessionAsync(ct);
        var peer      = await CreateSessionAsync(ct);
        var requester = await CreateSessionAsync(ct);
        var target    = await CreateSessionAsync(ct);
        var blocked   = await CreateSessionAsync(ct);

        var spaceA    = await CreateSpaceAsync(observer, "Deletion A", ct);
        var spaceB    = await CreateSpaceAsync(observer, "Deletion B", ct);
        var channelA  = await CreateTextChannelAsync(observer, spaceA, "a-room", ct);
        var channelB  = await CreateTextChannelAsync(observer, spaceB, "b-room", ct);
        var voiceRoom = await CreateVoiceChannelAsync(observer, spaceA, "a-voice", ct);

        await JoinSpaceAsync(observer, victim, spaceA, ct);
        await JoinSpaceAsync(observer, victim, spaceB, ct);

        await victim.Channels.SendMessage(spaceA, channelA, "victim in A", new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(), null, ct);
        await victim.Channels.SendMessage(spaceB, channelB, "victim in B", new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(), null, ct);

        // Social graph: a friendship both ways, a pending request in each direction, an outgoing block.
        await victim.Friends.SendFriendRequest(peer.Credentials.username, ct);
        await peer.Friends.AcceptFriendRequest(victim.UserId, ct);
        await requester.Friends.SendFriendRequest(victim.Credentials.username, ct);
        await victim.Friends.SendFriendRequest(target.Credentials.username, ct);
        await victim.Friends.BlockUser(blocked.UserId, ct);

        // A conversation with history on both sides.
        await victim.Chats.SendDirectMessage(peer.UserId, DmFromVictim, new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(), null, ct);
        await peer.Chats.SendDirectMessage(victim.UserId, "and the peer's reply", new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(), null, ct);

        // Settings the product does expose.
        await victim.Users.MuteTarget(spaceA, MuteTargetKind.Space, MuteLevelType.All, true,
            DateTime.UtcNow.AddHours(1), ct);
        await victim.Security.SetAutoDeletePeriod(12, ct);
        await victim.Privacy.SetPrivacyRule("stream.draw", PrivacyRuleMode.NOBODY, null,
            new IonArray<Guid>([]), new IonArray<Guid>([]), ct);

        var avatarFileId = Guid.CreateVersion7();
        var uploadFileId = Guid.CreateVersion7();

        await SeedPersonalDataAsync(victim, peer, avatarFileId, uploadFileId, ct);

        var censusBefore = await CensusAsync(victim.UserId, ct);

        var observerClient = await RealtimeClient.ConnectAsync(observer, ct);
        await observerClient.SubscribeToSpace(spaceA, ct);
        await observerClient.SubscribeToSpace(spaceB, ct);

        var victimClient = await RealtimeClient.ConnectAsync(victim, ct);

        var voiceJoinPath = await JoinVoiceAsync(victim, spaceA, voiceRoom, ct);
        var occupants     = await Poll.ForValueAsync(
            () => VoiceOccupantsAsync(observer, spaceA, voiceRoom, ct),
            users => users.Contains(victim.UserId), PresenceWaits.Settle, ct: ct);

        var deletionMark = observerClient.Mark();

        var (consoleScope, console) = AccountConsoleHarness.Console(victim);

        DeleteAccountResult requested;
        await using (consoleScope)
            requested = await console.RequestDeleteAccount(victim.Credentials.password, ct);

        if (!requested.success)
            throw new InvalidOperationException(
                $"the scenario account could not be scheduled for deletion: {requested.error}");

        // Nothing to poll for until the deadline passes, so the deadline is waited out.
        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            victim.UserId, AccountDeletionStatusKind.Completed, ct: ct);

        if (reached != AccountDeletionStatusKind.Completed)
            throw new InvalidOperationException(
                $"the scenario deletion did not finish: status {reached}, reason " +
                $"{(await GetGrainFactory().GetGrain<IAccountDeletionGrain>(victim.UserId).GetDeletionStatusAsync()).FailureReason}");

        // One window, opened at the moment of the erasure, shared by every test that asks what the
        // execution did. Anchoring the observation here rather than inside each test is what makes
        // those tests deterministic: a roster that converges only when a two-minute read cache
        // expires would otherwise pass or fail depending on how long NUnit spent on the tests that
        // happened to run first, which is the difference between a finding and a coin toss.
        var closedTask = victimClient.WaitForCloseAsync(Reaction, ct);

        var leftTask = observerClient.FirstWithinAsync<LeavedFromServerUser>(
            e => e.userId == victim.UserId, Reaction, deletionMark, ct);

        var offlineTask = observerClient.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == victim.UserId && e.status == UserStatus.Offline, Reaction, deletionMark, ct);

        var vacatedTask = observerClient.FirstWithinAsync<LeavedFromChannelUser>(
            e => e.userId == victim.UserId && e.channelId == voiceRoom, Reaction, deletionMark, ct);

        var rosterTask = Poll.ForValueAsync(
            async () => (
                Members:  (await observer.Servers.GetMembers(spaceA, ct))
                          .Values.Select(m => m.member.userId).ToArray(),
                Presence: (await observer.Servers.GetMemberPresence(spaceA, ct))
                          .Values.Select(pr => pr.userId).ToArray()),
            seen => !seen.Members.Contains(victim.UserId) && !seen.Presence.Contains(victim.UserId),
            Reaction, ct: ct);

        var seatTask = Poll.ForValueAsync(
            () => VoiceOccupantsAsync(observer, spaceA, voiceRoom, ct),
            users => !users.Contains(victim.UserId), Reaction, ct: ct);

        var aftermath = new Aftermath(
            SocketClosed: await closedTask,
            Left:         await leftTask,
            WentOffline:  await offlineTask,
            Vacated:      await vacatedTask,
            Members:      (await rosterTask).Members,
            Presence:     (await rosterTask).Presence,
            Occupants:    await seatTask);

        return new DeletedAccount(
            victim, observer, peer, requester, target, blocked,
            spaceA, spaceB, channelA, voiceRoom,
            observerClient, victimClient, deletionMark,
            occupants, voiceJoinPath, censusBefore, avatarFileId, uploadFileId, aftermath);
    }

    /// <summary>What an observer, the roster and the voice room said in the window after the erasure.</summary>
    private sealed record Aftermath(
        bool            SocketClosed,
        RecordedEvent?  Left,
        RecordedEvent?  WentOffline,
        RecordedEvent?  Vacated,
        Guid[]          Members,
        Guid[]          Presence,
        List<Guid>      Occupants);

    /// <summary>
    /// Writes one row into every personal-data table the product has no call for.
    /// </summary>
    /// <remarks>
    /// The repository's rule is that <c>DbContext</c> belongs in grains, and everything above that an
    /// API can produce is produced through the API. What is left is state with no surface at all — a
    /// passkey that needs a real authenticator, a pending e-mail change that needs a verification
    /// round trip, a device observation written by the login path, a trust score written by the report
    /// pipeline. Seeding them is the alternative to not testing whether the erasure reaches them, and
    /// each row here is the smallest one that makes the table non-empty for this user.
    /// </remarks>
    private static async Task SeedPersonalDataAsync(
        TestUserSession victim, TestUserSession peer, Guid avatarFileId, Guid uploadFileId, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var now = DateTimeOffset.UtcNow;

        db.SavedGifs.Add(new SavedGifEntity
        {
            Id = Guid.CreateVersion7(), UserId = victim.UserId, FileId = Guid.CreateVersion7(),
            Slug = $"gif-{Guid.NewGuid():N}"[..20], Width = 1, Height = 1, AddedAt = now,
            CreatedAt = now, UpdatedAt = now
        });

        db.PendingEmailChanges.Add(new PendingEmailChangeEntity
        {
            Id = Guid.CreateVersion7(), UserId = victim.UserId,
            NewEmail = $"moving_{Guid.NewGuid():N}"[..20] + "@test.local",
            CodeHash = "hash", CodeSalt = "salt", ExpiresAt = now.AddHours(1),
            CreatedAt = now, UpdatedAt = now
        });

        db.PendingPhoneChanges.Add(new PendingPhoneChangeEntity
        {
            Id = Guid.CreateVersion7(), UserId = victim.UserId, NewPhone = "+15550100",
            CodeHash = "hash", CodeSalt = "salt", ExpiresAt = now.AddHours(1),
            CreatedAt = now, UpdatedAt = now
        });

        db.DeviceObservations.Add(new DeviceObservationEntity
        {
            Id = Guid.CreateVersion7(), UserId = victim.UserId, DeviceId = Guid.CreateVersion7(),
            Components = "1;mg:abc,su:def", FirstSeenAt = now, LastSeenAt = now, Logins = 3,
            CreatedAt = now, UpdatedAt = now
        });

        db.Passkeys.Add(new UserPasskeyEntity
        {
            Id = Guid.CreateVersion7(), UserId = victim.UserId, Name = "seeded key",
            CredentialId = [1, 2, 3], PublicKey = [4, 5, 6], SignCount = 1, IsCompleted = true,
            CreatedAt = now, UpdatedAt = now
        });

        db.ChannelReadStates.Add(new ChannelReadStateEntity
        {
            UserId = victim.UserId, ChannelId = Guid.CreateVersion7(),
            LastReadMessageId = 42, MentionCount = 1, UpdatedAt = now
        });

        db.NotificationCounters.Add(new NotificationCounterEntity
        {
            UserId = victim.UserId, CounterType = NotificationCounterType.UnreadDirectMessages,
            Count = 3, UpdatedAt = now
        });

        db.SystemNotifications.Add(new SystemNotificationEntity
        {
            Id = Guid.CreateVersion7(), UserId = victim.UserId,
            Type = SystemNotificationType.SystemAnnouncement, Title = "seeded", Body = "seeded",
            CreatedAt = now
        });

        db.UserTrustScores.Add(new UserTrustScoreEntity
        {
            UserId = victim.UserId, TrustScore = 900, LastRecalculatedAt = now,
            CreatedAt = now, UpdatedAt = now
        });

        // An ignore rather than a block: the two tables are the same shape and only one of them was
        // ever cleared, which is the drift this census exists to catch.
        db.UserIgnorelist.Add(new UserIgnoreEntity
        {
            UserId = victim.UserId, IgnoredId = peer.UserId, CreatedAt = now
        });

        // Inventory, and the unread badge that hangs off it by a cascading key.
        var inventoryItemId = Guid.CreateVersion7();

        db.Items.Add(new ArgonItemEntity
        {
            Id = inventoryItemId, OwnerId = victim.UserId, TemplateId = "seeded.item",
            IsUsable = false, IsGiftable = false, IsAffectBadge = false, IsReference = false,
            CreatedAt = now, UpdatedAt = now
        });

        db.UnreadInventoryItems.Add(new ArgonItemNotificationEntity
        {
            OwnerUserId = victim.UserId, InventoryItemId = inventoryItemId,
            TemplateId = "seeded.item", CreatedAt = now
        });

        // Membership of somebody else's developer team, and an invitation to it. The team belongs to
        // the peer on purpose: a team the account OWNS is soft-deleted with its bots by a different
        // step, and what this pins is the rows that name the account inside a team that survives it.
        var teamId = Guid.CreateVersion7();

        db.TeamEntities.Add(new DevTeamEntity
        {
            TeamId = teamId, OwnerId = peer.UserId, Name = "Seeded team",
            CreatedAt = now, UpdatedAt = now
        });

        db.MemberTeamEntities.Add(new DevTeamMemberEntity
        {
            TeamId = teamId, UserId = victim.UserId, JoinedAt = now.UtcDateTime,
            IsPending = false, IsOwner = false
        });

        db.TeamInvites.Add(new DevTeamMemberInvite
        {
            TeamId = teamId, FromUserId = peer.UserId, ToUserId = victim.UserId,
            CreatedAt = now.UtcDateTime, ExpireAt = now.AddDays(7)
        });

        // Two files: the avatar the row points at, and an ordinary upload. Both carry a reference
        // count, which is the thing the deletion is supposed to bring down.
        foreach (var (fileId, purpose) in new[] { (avatarFileId, FilePurpose.Avatar), (uploadFileId, FilePurpose.ChannelAttachment) })
        {
            db.Files.Add(new FileEntity
            {
                Id = fileId, OwnerId = victim.UserId, Purpose = purpose,
                S3Key = $"seeded/{fileId:N}", BucketName = "seeded", FileSize = 3,
                ContentType = "image/png", FileName = "seeded.png", Finalized = true,
                CreatedAt = now, UpdatedAt = now
            });
            db.FileCounters.Add(new FileCounterEntity
            {
                Id = fileId, RefCount = 1, CreatedAt = now, UpdatedAt = now
            });
        }

        await db.SaveChangesAsync(ct);

        await AccountSeed.BackdateLastLoginAsync(victim.UserId, now.AddDays(-1), ct: ct);

        await db.Users.Where(u => u.Id == victim.UserId)
           .ExecuteUpdateAsync(set => set
               .SetProperty(u => u.AvatarFileId, avatarFileId.ToString())
               .SetProperty(u => u.PhoneNumber, "+15550199"), ct);
    }

    /// <summary>Every personal-data table this fixture knows about, counted for one user in one pass.</summary>
    private static async Task<PersonalDataCensus> CensusAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        // IgnoreQueryFilters throughout: several of these tables carry the global soft-delete filter,
        // and a filtered count cannot tell "the row is gone" from "the row is still there, hidden".
        return new PersonalDataCensus(
            Friendships:          await db.Friends.IgnoreQueryFilters()
                                     .CountAsync(f => f.UserId == userId || f.FriendId == userId, ct),
            Blocks:               await db.UserBlocklist.IgnoreQueryFilters()
                                     .CountAsync(b => b.UserId == userId || b.BlockedId == userId, ct),
            MuteSettings:         await db.MuteSettings.IgnoreQueryFilters().CountAsync(m => m.UserId == userId, ct),
            AutoDeleteSettings:   await db.AutoDeleteSettings.IgnoreQueryFilters().CountAsync(a => a.UserId == userId, ct),
            DeviceHistories:      await db.DeviceHistories.IgnoreQueryFilters().CountAsync(d => d.UserId == userId, ct),
            Passkeys:             await db.Passkeys.IgnoreQueryFilters().CountAsync(p => p.UserId == userId, ct),
            FriendRequests:       await db.FriendRequest.IgnoreQueryFilters()
                                     .CountAsync(r => r.RequesterId == userId || r.TargetId == userId, ct),
            PrivacyRules:         await db.PrivacyRules.IgnoreQueryFilters().CountAsync(r => r.UserId == userId, ct),
            SavedGifs:            await db.SavedGifs.IgnoreQueryFilters().CountAsync(g => g.UserId == userId, ct),
            PendingEmailChanges:  await db.PendingEmailChanges.IgnoreQueryFilters().CountAsync(p => p.UserId == userId, ct),
            PendingPhoneChanges:  await db.PendingPhoneChanges.IgnoreQueryFilters().CountAsync(p => p.UserId == userId, ct),
            DeviceObservations:   await db.DeviceObservations.IgnoreQueryFilters().CountAsync(o => o.UserId == userId, ct),
            ChannelReadStates:    await db.ChannelReadStates.IgnoreQueryFilters().CountAsync(s => s.UserId == userId, ct),
            NotificationCounters: await db.NotificationCounters.IgnoreQueryFilters().CountAsync(c => c.UserId == userId, ct),
            SystemNotifications:  await db.SystemNotifications.IgnoreQueryFilters().CountAsync(n => n.UserId == userId, ct),
            TrustScores:          await db.UserTrustScores.IgnoreQueryFilters().CountAsync(s => s.UserId == userId, ct),
            Ignores:              await db.UserIgnorelist.IgnoreQueryFilters()
                                     .CountAsync(i => i.UserId == userId || i.IgnoredId == userId, ct),
            InventoryItems:       await db.Items.IgnoreQueryFilters().CountAsync(i => i.OwnerId == userId, ct),
            UnreadInventoryItems: await db.UnreadInventoryItems.IgnoreQueryFilters()
                                     .CountAsync(n => n.OwnerUserId == userId, ct),
            TeamMemberships:      await db.MemberTeamEntities.IgnoreQueryFilters().CountAsync(m => m.UserId == userId, ct),
            TeamInvites:          await db.TeamInvites.IgnoreQueryFilters()
                                     .CountAsync(i => i.FromUserId == userId || i.ToUserId == userId, ct));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What an Ion call answered, as a string a failure message can carry.
    /// </summary>
    /// <remarks>
    /// A refusal from <c>ArgonTransactionInterceptor</c> arrives as a protocol error rather than a
    /// return value, and the interesting failure here is the call <em>succeeding</em>, so the outcome
    /// is reported rather than asserted on directly: "accepted" and "threw because the user row is
    /// gone" are different findings and a bare <c>Throws.Exception</c> would conflate them.
    /// </remarks>
    private static async Task<string> OutcomeOf<T>(Func<Task<T>> call)
    {
        try
        {
            await call();
            return "accepted";
        }
        catch (Exception e)
        {
            return $"{e.GetType().Name}: {e.Message}";
        }
    }

    /// <summary>Schedules a deletion on the grain, rides out the grace, and drives it to Completed.</summary>
    private async Task ScheduleAndExecuteAsync(Guid userId, string password, CancellationToken ct)
    {
        var grain     = GetGrainFactory().GetGrain<IAccountDeletionGrain>(userId);
        var requested = await grain.RequestDeletionAsync(password);

        Assert.That(requested.Success, Is.True,
            $"the account under test could not be scheduled for deletion: {requested.Error}");

        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            userId, AccountDeletionStatusKind.Completed, ct: ct);

        Assert.That(reached, Is.EqualTo(AccountDeletionStatusKind.Completed),
            $"the deletion did not finish; status was {reached}");
    }

    /// <summary>
    /// The birth date as each of the two rows that can hold one has it.
    /// </summary>
    /// <remarks>
    /// <c>IgnoreQueryFilters</c> on both, for the reason <c>AccountSeed.ReadUserAsync</c> gives: the
    /// global soft-delete filter hides the anonymised user row, and a filtered read would answer null
    /// for every column of it — which would make this assertion pass without the erasure having
    /// cleared anything at all.
    /// </remarks>
    private static async Task<(DateOnly? User, DateOnly? Profile)> BirthDatesAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await AccountSeed.NewDbAsync(ct);

        var user = await db.Users
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(u => u.Id == userId)
           .Select(u => u.DateOfBirth)
           .FirstOrDefaultAsync(ct);

        var profile = await db.UserProfiles
           .IgnoreQueryFilters()
           .AsNoTracking()
           .Where(p => p.UserId == userId)
           .Select(p => p.DateOfBirth)
           .FirstOrDefaultAsync(ct);

        return (user, profile);
    }

    /// <summary>The deletion grain's persisted state, read straight out of its own store.</summary>
    private static async Task<AccountDeletionGrainState> ReadDeletionStateAsync(Guid userId)
    {
        var state = new GrainState<AccountDeletionGrainState>(new AccountDeletionGrainState());

        await DeletionStore().ReadStateAsync(DeletionStateName, DeletionGrainId(userId), state);

        return state.State ?? new AccountDeletionGrainState();
    }

    /// <summary>Writes the deletion grain's persisted state, as a crashed silo would have left it.</summary>
    private static async Task WriteDeletionStateAsync(Guid userId, AccountDeletionGrainState seed)
    {
        var state = new GrainState<AccountDeletionGrainState>(new AccountDeletionGrainState());

        // Read first for the ETag: the store refuses a blind write over an existing record, and a
        // fresh grain simply has none.
        await DeletionStore().ReadStateAsync(DeletionStateName, DeletionGrainId(userId), state);

        state.State = seed;

        await DeletionStore().WriteStateAsync(DeletionStateName, DeletionGrainId(userId), state);
    }

    private const string DeletionStateName = "account-deletion-store";

    private static IGrainStorage DeletionStore()
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredKeyedService<IGrainStorage>(ProviderConstants.DEFAULT_STORAGE_PROVIDER_NAME);

    private static GrainId DeletionGrainId(Guid userId)
        => ArgonTestEnvironment.Instance.Host.Services
           .GetRequiredService<IGrainFactory>()
           .GetGrain<IAccountDeletionGrain>(userId)
           .GetGrainId();

    private static NewUserCredentialsInput NewCredentials(string username, string email)
    {
        var seed = GenerateCredentials();

        return new NewUserCredentialsInput(email, username, seed.password, seed.displayName,
            true, seed.birthDate, true, seed.captchaToken, "1.0", "1.0");
    }

    private static async Task<Guid> CreateSpaceAsync(TestUserSession owner, string name, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(new CreateServerRequest(name, "Account deletion fixture", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
            throw new InvalidOperationException($"could not create the space '{name}': {(result as FailedCreateSpace)?.error}");

        return success.space.spaceId;
    }

    private static Task<Guid> CreateTextChannelAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
        => CreateChannelAsync(owner, spaceId, name, ChannelType.Text, ct);

    private static Task<Guid> CreateVoiceChannelAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
        => CreateChannelAsync(owner, spaceId, name, ChannelType.Voice, ct);

    private static async Task<Guid> CreateChannelAsync(
        TestUserSession owner, Guid spaceId, string name, ChannelType kind, CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, kind, "Account deletion fixture", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
            throw new InvalidOperationException($"could not find the channel '{name}' just created in {spaceId}");

        return created.channel.channelId;
    }

    private static async Task JoinSpaceAsync(TestUserSession owner, TestUserSession guest, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await guest.Users.JoinToSpace(code, ct);

        if (joined is not SuccessJoin)
            throw new InvalidOperationException($"the guest could not join {spaceId}: {(joined as FailedJoin)?.error}");
    }

    /// <summary>
    /// Enters a voice channel the way the desktop client does, and says which road it took.
    /// </summary>
    /// <remarks>
    /// <c>Interlink</c> reaches <c>ChannelGrain.Join</c>, which registers the occupant and fires
    /// <c>JoinedToChannelUser</c> before minting an SFU token; the mint is a local JWT signature, so no
    /// SFU has to be reachable. The fallback exists so a broken RPC cannot quietly turn the voice
    /// assertions into a no-op — it exercises the same grain and the report says so.
    /// </remarks>
    private async Task<string> JoinVoiceAsync(TestUserSession user, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        try
        {
            var result = await user.Channels.Interlink(spaceId, channelId, ct);

            if (result is SuccessJoinVoice)
                return "ion Interlink";

            throw new InvalidOperationException($"Interlink refused the voice join: {(result as FailedJoinVoice)?.error}");
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            RequestContext.Set("$caller_user_id", user.UserId);
            try
            {
                var joined = await GetGrainFactory().GetGrain<IChannelGrain>(channelId).Join();

                if (!joined.IsSuccess)
                    throw new InvalidOperationException($"ChannelGrain.Join refused: {joined.Error}");

                return $"IChannelGrain.Join (Interlink threw: {e.GetType().Name}: {e.Message})";
            }
            finally
            {
                RequestContext.Clear();
            }
        }
    }

    private static async Task<List<Guid>> VoiceOccupantsAsync(
        TestUserSession reader, Guid spaceId, Guid channelId, CancellationToken ct)
    {
        var channels = await reader.Servers.GetChannels(spaceId, ct);
        var channel  = channels.Values.FirstOrDefault(c => c.channel.channelId == channelId);

        return channel is null ? [] : channel.users.Values.Select(u => u.userId).ToList();
    }

    /// <summary>Row counts for one user across every personal-data table this fixture seeds or drives.</summary>
    private sealed record PersonalDataCensus(
        int Friendships,
        int Blocks,
        int MuteSettings,
        int AutoDeleteSettings,
        int DeviceHistories,
        int Passkeys,
        int FriendRequests,
        int PrivacyRules,
        int SavedGifs,
        int PendingEmailChanges,
        int PendingPhoneChanges,
        int DeviceObservations,
        int ChannelReadStates,
        int NotificationCounters,
        int SystemNotifications,
        int TrustScores,
        int Ignores,
        int InventoryItems,
        int UnreadInventoryItems,
        int TeamMemberships,
        int TeamInvites)
    {
        public int Total
            => Friendships + Blocks + MuteSettings + AutoDeleteSettings + DeviceHistories + Passkeys
             + FriendRequests + PrivacyRules + SavedGifs + PendingEmailChanges + PendingPhoneChanges
             + DeviceObservations + ChannelReadStates + NotificationCounters + SystemNotifications
             + TrustScores + Ignores + InventoryItems + UnreadInventoryItems + TeamMemberships
             + TeamInvites;
    }

    /// <summary>The account this fixture deleted, and everything that was watching when it happened.</summary>
    private sealed record DeletedAccount(
        TestUserSession VictimSession,
        TestUserSession ObserverSession,
        TestUserSession PeerSession,
        TestUserSession RequesterSession,
        TestUserSession TargetSession,
        TestUserSession BlockedSession,
        Guid            SpaceA,
        Guid            SpaceB,
        Guid            ChannelA,
        Guid            VoiceChannel,
        RealtimeClient  Observer,
        RealtimeClient  Victim,
        int             DeletionMark,
        List<Guid>      VoiceOccupantsBefore,
        string          VoiceJoinPath,
        PersonalDataCensus CensusBefore,
        Guid            AvatarFileId,
        Guid            UploadFileId,
        Aftermath       After)
    {
        public Guid VictimId => VictimSession.UserId;

        public NewUserCredentialsInputForTest Credentials => VictimSession.Credentials;
    }
}
