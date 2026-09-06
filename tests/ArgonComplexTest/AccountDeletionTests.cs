namespace ArgonComplexTest.Tests;

using AccountContracts;
using Argon.Core.Entities.Data;
using Argon.Entities;
using Argon.Features.Storage;
using Argon.Features.Testing;
using Argon.Grains.Interfaces;
using Argon.Grains.Persistence.States;
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
    /// <para><b>Defect (confirmed).</b> Nothing in <c>AccountDeletionGrain.ExecuteDeletionAsync</c>
    /// revokes credentials. Step 1 is <c>InvalidateSessionsAsync</c>
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:441</c>), and all it does is delete presence
    /// keys from Redis: no <c>SessionRevocation.FloorKey</c>, no entry in the per-user revoked set, no
    /// tombstone for the credential the token was minted under. <c>ArgonTransactionInterceptor</c>
    /// authenticates from the signature alone and its one per-user database read,
    /// <c>ResolveLockdownSeverityAsync</c>, queries <c>db.Users</c> under the global soft-delete
    /// filter and coalesces the resulting null to <c>LockdownReason.NONE</c> — so an erased account
    /// reads as an unlocked one. <c>AnonymizeUserAsync</c> makes it worse by clearing
    /// <c>LockdownReason</c> at <c>:495</c>, wiping the only column that could have refused the
    /// request.</para>
    ///
    /// <para>The consequence is not academic: the token lives out its full remaining lifetime with
    /// full API rights, and the calls that happen to route through <c>UserGrain.GetMe()</c> throw
    /// instead of being refused, so the surface a deleted account sees is a mixture of successes and
    /// internal errors rather than a clean sign-out. The machinery to do it properly is already in
    /// the codebase and used by <c>SecurityGrain.EndSessionAsync</c>.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_deleted_accounts_access_token_is_refused_on_every_authenticated_call(
        CancellationToken ct = default)
    {
        // Deliberately a call that does not load the user row: UserGrain.GetMe throws for a deleted
        // account, and an exception from a missing row would read as "refused" while proving nothing
        // about authentication. The friends list is answered from a table keyed on the user id, so it
        // succeeds iff the token was accepted.
        var friends = await OutcomeOf(() => scene.VictimSession.Friends.GetMyFriendships(50, 0, ct));
        var chats   = await OutcomeOf(() => scene.VictimSession.Chats.GetRecentChats(50, 0, ct));

        Assert.Multiple(() =>
        {
            Assert.That(friends, Does.Contain("NO_AUTH"),
                $"an erased account's access token was still served: GetMyFriendships answered '{friends}'");
            Assert.That(chats, Does.Contain("NO_AUTH"),
                $"an erased account's access token was still served: GetRecentChats answered '{chats}'");
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
    /// <para><b>Defect (confirmed).</b> <c>InvalidateSessionsAsync</c> talks to
    /// <c>IUserPresenceService</c> directly and never goes near <c>UserSessionGrain</c>,
    /// <c>HubConnectionRegistry</c> or the <c>argon.session.revoked</c> signal, so the SignalR
    /// connection an erased account is holding stays open and subscribed to every space group it
    /// joined. The desktop client of a deleted account keeps receiving other people's messages and
    /// presence until the user closes it.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_deleted_accounts_socket_is_closed_and_goes_quiet(CancellationToken ct = default)
    {
        var closed = await scene.Victim.WaitForCloseAsync(Reaction, ct);

        // A fixed window on purpose: what is asserted is an absence, and an absence has no edge to
        // poll for. The observer keeps talking in the meantime — see the traffic generated below —
        // so the window is not empty of things the connection could have been told.
        await scene.ObserverSession.Channels.SendMessage(
            scene.SpaceA, scene.ChannelA, "traffic after the erasure", new IonArray<IMessageEntity>([]),
            Random.Shared.NextInt64(), null, ct);

        var heard = scene.Victim.Records(scene.VictimMark);

        Assert.Multiple(() =>
        {
            Assert.That(closed, Is.True,
                "the erased account's hub connection is still open; it is still in every space group it joined");
            Assert.That(heard, Is.Empty,
                $"the erased account's client is still being fed events: {scene.Victim.Dump(scene.VictimMark)}");
        });
    }

    // ── D3: the spaces ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every space the account was in is told it left, and stops listing it.
    /// </summary>
    /// <remarks>
    /// <para><b>Defect (confirmed).</b> <c>SoftDeleteMembershipsAsync</c>
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:625</c>) is a bare <c>ExecuteUpdateAsync</c>
    /// on its own <c>DbContext</c>: no <c>SpaceGrain.Invalidate()</c>, no <c>LeavedFromServerUser</c>,
    /// no <c>UserChangedStatus(Offline)</c>, no bot <c>MemberLeave</c>. Every other membership
    /// mutation in <c>SpaceGrain</c> calls <c>Invalidate()</c> — twelve call sites — and this one does
    /// not, so <c>SpaceReadGrain</c> keeps serving the erased member out of a snapshot whose
    /// distributed expiry is two minutes. What every other member of the space sees is a person who is
    /// still in the roster, still shown online, for minutes after they were erased.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_spaces_are_told_the_member_left_and_stop_listing_them(CancellationToken ct = default)
    {
        var left = await scene.Observer.FirstWithinAsync<LeavedFromServerUser>(
            e => e.userId == scene.VictimId, Reaction, scene.DeletionMark, ct);

        var wentOffline = await scene.Observer.FirstWithinAsync<UserChangedStatus>(
            e => e.userId == scene.VictimId && e.status == UserStatus.Offline, Reaction, scene.DeletionMark, ct);

        var roster = await Poll.ForValueAsync(
            async () => (
                Members:  (await scene.ObserverSession.Servers.GetMembers(scene.SpaceA, ct))
                          .Values.Select(m => m.member.userId).ToArray(),
                Presence: (await scene.ObserverSession.Servers.GetMemberPresence(scene.SpaceA, ct))
                          .Values.Select(p => p.userId).ToArray()),
            seen => !seen.Members.Contains(scene.VictimId) && !seen.Presence.Contains(scene.VictimId),
            Reaction, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(left, Is.Not.Null,
                "nobody in the space was told the erased account left it");
            Assert.That(wentOffline, Is.Not.Null,
                "the space was never told the erased account went offline, so it is still rendered online");
            Assert.That(roster.Members, Does.Not.Contain(scene.VictimId),
                "GetMembers still lists an account that no longer exists");
            Assert.That(roster.Presence, Does.Not.Contain(scene.VictimId),
                "GetMemberPresence still lists an account that no longer exists");
        });
    }

    // ── D4: voice ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The voice seat the account was sitting in is vacated.
    /// </summary>
    /// <remarks>
    /// <para><b>Defect (confirmed).</b> A user whose last session ends normally is taken out of every
    /// voice channel by <c>UserSessionGrain.FinalizeOfflineAsync</c> calling
    /// <c>IUserGrain.LeaveAllVoiceAsync()</c> — the fix <c>PresenceVoiceAndCountsTests</c> guards.
    /// Deletion never goes through the session grain: <c>InvalidateSessionsAsync</c> deletes the
    /// presence keys underneath it, so <c>FinalizeOfflineAsync</c> never runs and nothing calls
    /// <c>IChannelGrain.Leave</c>. The name stays in the room, for everyone, until the channel
    /// activation is collected — a ghost in a call belonging to an account that has been erased.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_voice_seat_is_vacated(CancellationToken ct = default)
    {
        Assert.That(scene.VoiceOccupantsBefore, Does.Contain(scene.VictimId),
            $"the account never took the voice seat this test is about (joined via {scene.VoiceJoinPath}), " +
            "so what follows would pass for the wrong reason");

        var vacated = await scene.Observer.FirstWithinAsync<LeavedFromChannelUser>(
            e => e.userId == scene.VictimId && e.channelId == scene.VoiceChannel,
            Reaction, scene.DeletionMark, ct);

        var occupants = await Poll.ForValueAsync(
            () => VoiceOccupantsAsync(scene.ObserverSession, scene.SpaceA, scene.VoiceChannel, ct),
            users => !users.Contains(scene.VictimId), Reaction, ct: ct);

        Assert.Multiple(() =>
        {
            Assert.That(occupants, Does.Not.Contain(scene.VictimId),
                "the voice channel still seats an account that no longer exists");
            Assert.That(vacated, Is.Not.Null,
                "nobody was told the erased account left the call");
        });
    }

    // ── D5: the social graph ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Friends, pending requests and blocks in both directions are gone, and the DM peer sees a
    /// deleted account rather than a hole.
    /// </summary>
    /// <remarks>
    /// <para><b>Defect (confirmed), two of them.</b> <c>DeletePrivateDataAsync</c> clears
    /// <c>Friends</c> and <c>UserBlocklist</c> in both directions but never touches
    /// <c>FriendRequest</c> (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:561</c>), so a pending
    /// request naming the erased account survives in both directions — the other party keeps an
    /// actionable invitation from somebody who no longer exists, and accepting it writes a friendship
    /// row against an anonymised id.</para>
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

        var lookup  = await scene.PeerSession.Users.LookupUser(scene.VictimId, ct);
        var history = (await scene.PeerSession.Chats.QueryDirectMessages(scene.VictimId, null, 50, ct)).Values.Select(m => m.text).ToArray();

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

            Assert.That(lookup, Is.InstanceOf<SuccessLookupUser>(),
                $"the peer's chat window cannot resolve the other party at all: " +
                $"{(lookup as FailedLookupUser)?.error.ToString() ?? "no error"}");
            Assert.That((lookup as SuccessLookupUser)?.user.displayName, Is.EqualTo("Deleted Account"),
                "a deleted peer must render as a deleted account, not as their old name");
        });
    }

    // ── D6: the doors back in ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The account cannot sign in again, its username is held back, and its e-mail address is freed.
    /// </summary>
    /// <remarks>
    /// The three halves of "the account is gone" that a person can check for themselves. The username
    /// reservation is deliberate — <c>ReserveUsernameAsync</c> writes a <c>UsernameReservedEntity</c>
    /// row — so somebody else cannot inherit the identity a community knew. The e-mail is deliberately
    /// <em>not</em> held: the row's address is rewritten to <c>deleted_{id}@void.local</c> and both
    /// normalised columns are computed by the database, so the real address becomes registrable again,
    /// which is what lets a person come back.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Login_is_refused_the_username_is_reserved_and_the_email_is_freed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var identity = GetIdentityService(scope.ServiceProvider);

        var byUsername = await identity.Authorize(
            new UserCredentialsInput(null, null, scene.Credentials.username, scene.Credentials.password, null, null), ct);
        var byEmail = await identity.Authorize(
            new UserCredentialsInput(scene.Credentials.email, null, null, scene.Credentials.password, null, null), ct);

        var reclaimUsername = await identity.Registration(
            NewCredentials(scene.Credentials.username, $"reclaim_{Guid.NewGuid():N}"[..20] + "@test.local"), ct);
        var reclaimEmail = await identity.Registration(
            NewCredentials($"reclaim_{Guid.NewGuid():N}"[..20], scene.Credentials.email), ct);

        Assert.Multiple(() =>
        {
            Assert.That(byUsername, Is.InstanceOf<FailedAuthorize>(),
                "an erased account still signs in with its username and password");
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
    /// <para><b>Defect (confirmed).</b> <c>DeletePrivateDataAsync</c>
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:561</c>) names eleven tables and misses the
    /// rest. What survives, joined to a user id the product tells the person has been erased:
    /// <c>FriendRequest</c> (both directions, naming the other party), <c>PrivacyRules</c>,
    /// <c>SavedGifs</c>, <c>PendingEmailChanges</c> and <c>PendingPhoneChanges</c> — a plaintext
    /// address or phone number the user was mid-way through switching to, which is worse than the
    /// <c>PhoneNumber</c> column the deletion is careful to null — <c>DeviceObservations</c> (device
    /// fingerprints, while <c>DeviceHistories</c> next door <em>is</em> deleted, so the erasure is
    /// inconsistent inside one subject area), <c>ChannelReadStates</c>, <c>NotificationCounters</c>,
    /// <c>SystemNotifications</c> and <c>UserTrustScores</c>. None of them carries <c>IsDeleted</c>,
    /// so the global soft-delete filter does not even hide them.</para>
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
                "the erased account's trust score survives it; if this row is a deliberate abuse hold " +
                "it needs to say so somewhere, because nothing in the deletion path mentions it");
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

            Assert.That(avatarRefs, Is.Zero,
                "the avatar file kept its reference, so the bytes are held for an account that does not exist");
            Assert.That(uploadRefs, Is.Zero,
                "an uploaded file kept its reference, so the bytes are held for an account that does not exist");
        });
    }

    /// <summary>
    /// Nothing keeps a plaintext copy of the identity after the deletion has finished.
    /// </summary>
    /// <remarks>
    /// <para><b>Defect (confirmed).</b> <c>AccountDeletionGrainState</c> holds
    /// <c>OriginalEmail</c>, <c>OriginalUsername</c> and <c>OriginalDisplayName</c> so the completion
    /// mail has somewhere to go, and the <c>Completed</c> branch
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:412</c>) writes only <c>Status</c> and
    /// <c>CompletedAt</c> — only <c>CancelDeletionAsync</c> clears the three. The grain-storage row
    /// for <c>account-deletion-store</c> therefore keeps the full plaintext identity of every deleted
    /// account for ever, in a store no data-subject process knows to look in. They cannot be cleared
    /// before step 9, which needs them; they can be cleared at step 10, which does not.</para>
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
    /// <para><b>Defect (confirmed).</b> <c>ExecuteDeletionAsync</c> has ten steps and none of them
    /// mentions S3 or <c>IUserDataExportGrain</c>. A person who exports their data and then deletes
    /// their account leaves <c>exports/{userId}/{exportId}/export-*.zip</c> — profile.json with their
    /// e-mail, phone and date of birth, devices.json with up to a hundred IP addresses, every message
    /// they wrote — in the export bucket indefinitely, reachable without any authentication for the
    /// full remaining life of the presigned URL. Erasure that leaves a downloadable copy of everything
    /// behind is not erasure.</para>
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
    /// <para><b>Defect (confirmed).</b> <c>UserDataExportGrain.RequestExportAsync</c> has no deletion
    /// guard at all, so an export can be started for a scheduled account and — because the collection
    /// steps read the user row through the soft-delete filter and silently skip what they cannot find
    /// — can complete for one already erased, producing an archive with no profile.json in it and no
    /// mail to say it is ready. Either answer is defensible; starting a fresh copy of everything the
    /// account is about to have erased is not.</para>
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
    /// <para><b>Defect (confirmed).</b> <c>SpaceDeletionGrain</c> leaves <c>SpaceEntity.IsDeleted</c>
    /// false for the whole of its grace — seven days private, thirty community — and the account guard
    /// at <c>src/Argon.Api/Grains/AccountDeletionGrain.cs:125</c> is a plain "do you own any
    /// non-deleted space". So the sequence the console's own error text instructs — "please transfer
    /// or delete your spaces before deleting your account" — produces a thirty-day wait followed by a
    /// thirty-day account grace: a sixty-day floor on erasure, outside the one-month window a person
    /// is entitled to, with nothing telling them so and no ownership-transfer call anywhere in the
    /// product to shorten it. Either the guard has to ignore a space already on its way out, or the
    /// refusal has to carry the date so the console can explain the wait.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
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
    /// <para><b>Defect (confirmed).</b> The grain does the right thing — <c>RequestDeletionAsync</c>
    /// answers <c>AlreadyScheduled</c> and populates <c>ScheduledDeletionAt</c> from the existing
    /// state — and <c>AccountConsoleService.RequestDeleteAccount</c> throws it away: every failure
    /// branch returns <c>(false, error, null, null)</c>
    /// (<c>src/Argon.Api/Features/AccountConsole/AccountConsoleService.cs:36</c>). The console
    /// literally cannot render "already scheduled, for this date" even though the date came back to
    /// it, which is why a person who clicks the button twice sees an error with no information in
    /// it.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
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
    /// <para><b>Defect (confirmed).</b> <c>RequestDeletionAsync</c> collapses <c>Executing</c> and
    /// <c>Completed</c> into <c>AlreadyScheduled</c>
    /// (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:74</c>), and the console maps that to
    /// "your account is already scheduled for deletion". For an account that has already been erased
    /// that sentence is false in a way that matters: it tells the person their data is still there and
    /// still cancellable. <c>DeleteAccountError</c> has no value for "already gone", though
    /// <c>CancelDeleteError</c> does.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
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
    /// <para><b>Defect (confirmed).</b> <c>ExecuteDeletionAsync</c> writes <c>Executing</c> before
    /// doing any work (<c>src/Argon.Api/Grains/AccountDeletionGrain.cs:374</c>) and
    /// <c>OnActivateAsync</c> re-arms the check timer only for <c>Scheduled</c>
    /// (<c>:45</c>), so a crash anywhere in steps 1–10 leaves the state permanently
    /// <c>Executing</c>: <c>CheckAndExecuteAsync</c> early-returns, <c>CancelDeletionAsync</c> answers
    /// <c>AlreadyExecuting</c>, and <c>RequestDeletionAsync</c> answers <c>AlreadyScheduled</c>. The
    /// account sits half-erased for ever with no path out of it in this grain, and the console renders
    /// the same "deletion scheduled" banner it renders for a healthy schedule.</para>
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

        // The poll the timer would make, and the one a restarted silo would make on its next tick.
        var resumed = await AccountConsoleHarness.DriveDeletionUntilAsync(
            session.UserId, AccountDeletionStatusKind.Completed, AccountTimings.ExecutionBudget, ct);

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

        await SeedPersonalDataAsync(victim, avatarFileId, uploadFileId, ct);

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
        var victimMark   = victimClient.Mark();

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

        return new DeletedAccount(
            victim, observer, peer, requester, target, blocked,
            spaceA, spaceB, channelA, voiceRoom,
            observerClient, victimClient, deletionMark, victimMark,
            occupants, voiceJoinPath, censusBefore, avatarFileId, uploadFileId);
    }

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
        TestUserSession victim, Guid avatarFileId, Guid uploadFileId, CancellationToken ct)
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
            TrustScores:          await db.UserTrustScores.IgnoreQueryFilters().CountAsync(s => s.UserId == userId, ct));
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
        int TrustScores)
    {
        public int Total
            => Friendships + Blocks + MuteSettings + AutoDeleteSettings + DeviceHistories + Passkeys
             + FriendRequests + PrivacyRules + SavedGifs + PendingEmailChanges + PendingPhoneChanges
             + DeviceObservations + ChannelReadStates + NotificationCounters + SystemNotifications
             + TrustScores;
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
        int             VictimMark,
        List<Guid>      VoiceOccupantsBefore,
        string          VoiceJoinPath,
        PersonalDataCensus CensusBefore,
        Guid            AvatarFileId,
        Guid            UploadFileId)
    {
        public Guid VictimId => VictimSession.UserId;

        public NewUserCredentialsInputForTest Credentials => VictimSession.Credentials;
    }
}
