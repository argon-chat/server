namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Sockets;
using AccountContracts;
using Argon.Api.Grains.Interfaces;
using Argon.Features.Storage;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The three things the account campaign found on the edges of deletion rather than inside it.
/// </summary>
/// <remarks>
/// <para>Each of these was reached through an account-lifecycle scenario and none of them belongs to
/// the lifecycle: a peer identity that stops resolving once its owner is erased (ACC-06), a file
/// reference released twice by one deletion pass (ACC-08), and a sign-in that answers 500 when the
/// caller omits a field the schema publishes as optional (ACC-16). They share a shape — a defensive
/// read that was written as an optimistic one — and they share nothing else, so they are gathered
/// here instead of being scattered through fixtures whose subject they are not. A deletion test that
/// also asserted on the login door would be red for two unrelated reasons at once, and the reader
/// would not be able to tell which.</para>
///
/// <para>The deleted account below is built and erased once, in one-time setup, for the same reason
/// <c>AccountDeletionTests</c> does it: an execution is a wide, one-way operation and re-running it
/// per test would let a difference between two runs look like a difference between two contracts.
/// The account here is deliberately thin — a DM peer and nothing else — because what these tests ask
/// about is the identity that survives, not the wreckage around it.</para>
/// </remarks>
[TestFixture]
public class AccountPeripheralTests : TestBase
{
    /// <summary>A one-pixel PNG — small, and a real image, which matters to anything that sniffs.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    /// <summary>The surviving half of the conversation: the account that was not deleted.</summary>
    private TestUserSession peer = null!;

    /// <summary>Somebody with no relationship to the erased account at all.</summary>
    private TestUserSession stranger = null!;

    private Guid   victimId;
    private string victimUsername = null!;

    [OneTimeSetUp]
    public async Task DeleteAnAccountWithADmPeerAsync()
    {
        var ct = CancellationToken.None;

        var victim = await CreateSessionAsync(ct);

        peer     = await CreateSessionAsync(ct);
        stranger = await CreateSessionAsync(ct);

        victimId       = victim.UserId;
        victimUsername = victim.Credentials.username;

        // The only anchor between the two, and the one that matters: a conversation. Deletion removes
        // the erased account's own UserConversations rows and leaves the peer's, so the peer keeps a
        // chat it can still open — which is exactly the situation ACC-06 is about.
        await victim.Chats.SendDirectMessage(peer.UserId, "a message from an account about to go",
            new IonArray<IMessageEntity>([]), Random.Shared.NextInt64(), null, ct);

        var (consoleScope, console) = AccountConsoleHarness.Console(victim);

        DeleteAccountResult requested;
        await using (consoleScope)
            requested = await console.RequestDeleteAccount(victim.Credentials.password, ct);

        if (!requested.success)
            throw new InvalidOperationException(
                $"the peripheral fixture could not schedule its account for deletion: {requested.error}");

        // Nothing to poll for until the deadline passes, so the deadline is waited out.
        await Task.Delay(AccountTimings.GraceAndABit, ct);

        var reached = await AccountConsoleHarness.DriveDeletionUntilAsync(
            victimId, AccountDeletionStatusKind.Completed, ct: ct);

        if (reached != AccountDeletionStatusKind.Completed)
            throw new InvalidOperationException(
                $"the peripheral fixture's deletion did not finish: status {reached}");
    }

    // ── ACC-06: the identity that has to survive its owner ───────────────────────────────────────

    /// <summary>
    /// A DM peer of a deleted account resolves to the tombstone the deletion wrote.
    /// </summary>
    /// <remarks>
    /// <para>The contract this now guards: <c>UserInteraction.LookupUser</c> answers for an account
    /// that has been erased, and answers with the placeholder identity — display name
    /// "Deleted Account", the reserved <c>deleted_…</c> username, no avatar and
    /// <c>UserFlag.DELETED</c> raised — rather than throwing. It is the only route a client has to a
    /// user id its local cache does not hold, which is the ordinary state of a DM-only peer on a fresh
    /// install, so a conversation that still reads must still have somebody at the top of it.</para>
    ///
    /// <para>It used to route through <c>UserGrain.GetMe</c>, a self-read under the global
    /// <c>!IsDeleted</c> filter using <c>FirstAsync</c>, so an anonymised row was not "missing" — it
    /// threw, and the caller got <c>UPSTREAM_ERROR</c> on every render, because the client does not
    /// remember a thrown request as an answer. It now routes through
    /// <c>IUserGrain.GetIdentityIncludingDeleted</c>, the filter-ignoring read
    /// <c>SpaceGrain.PrefetchUser</c> has always used, so the two ways of looking at the same erased
    /// person finally agree.</para>
    ///
    /// <para>The flag is asserted alongside the name because it is the half a client can act on: the
    /// name is a string somebody could legitimately choose, and <c>UserFlag.DELETED</c> is what tells
    /// a chat header to stop offering a profile card for a person who no longer has one.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_deleted_peer_still_resolves_to_the_tombstone_identity(CancellationToken ct = default)
    {
        var lookup = await peer.Users.LookupUser(victimId, ct);

        Assert.That(lookup, Is.InstanceOf<SuccessLookupUser>(),
            "the peer's chat window has nothing to put at the top of a conversation it can still read: "
          + $"LookupUser answered {(lookup as FailedLookupUser)?.error.ToString() ?? lookup.GetType().Name}");

        var user = ((SuccessLookupUser)lookup).user;

        Assert.Multiple(() =>
        {
            Assert.That(user.userId, Is.EqualTo(victimId));
            Assert.That(user.displayName, Is.EqualTo("Deleted Account"),
                "the placeholder the deletion wrote is what the client renders in place of a name");
            Assert.That(user.username, Does.StartWith("deleted_"),
                "the erased account's username is not handed back to anyone who asks");
            Assert.That(user.username, Does.Not.Contain(victimUsername),
                "the old username is still travelling on the identity DTO");
            Assert.That(user.avatarFileId, Is.Null,
                "a deleted account still points at a picture of the person it used to be");
            Assert.That(user.flags.HasFlag(UserFlag.DELETED), Is.True,
                "nothing on the DTO tells the client this identity is a tombstone rather than a person");
        });
    }

    /// <summary>
    /// Resolving a deleted account still needs standing; the tombstone is not a hole in the gate.
    /// </summary>
    /// <remarks>
    /// The risk the ACC-06 fix carries, pinned so it cannot be taken later. <c>LookupUser</c> now
    /// answers for rows the soft-delete filter used to hide, and the only thing between a bare user id
    /// and the whole directory is <c>SocialReach.CanReachAsync</c> — so a caller with no shared space,
    /// no friendship, no request and no conversation must still be refused, and refused with
    /// <c>NO_ANCHOR</c> rather than with anything that distinguishes "erased" from "never existed".
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_stranger_is_still_refused_the_deleted_accounts_identity(CancellationToken ct = default)
    {
        var lookup = await stranger.Users.LookupUser(victimId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(lookup, Is.InstanceOf<FailedLookupUser>(),
                "a deleted account is reachable by id alone, which is a directory walk with extra steps");
            Assert.That((lookup as FailedLookupUser)?.error, Is.EqualTo(LookupError.NO_ANCHOR),
                "the refusal has to be the same one a living stranger gets, or it says who is deleted");
        });
    }

    /// <summary>
    /// The two filtered reads keep refusing a deleted account, which is what several callers rely on.
    /// </summary>
    /// <remarks>
    /// <para>The other half of the ACC-06 contract, and the reason the fix is a new method rather than
    /// a widened one. <c>GetMe</c> is a self-read — the surface behind <c>UserInteraction.GetMe</c>,
    /// the OIDC userinfo handler and the bot self endpoint — and an erased account must not be handed
    /// its own row back. <c>GetAsArgonUser</c>'s throw is load-bearing in a different way: the Xsolla
    /// webhook, Ultima gifting and <c>BotUserCache</c> all use it as an existence check, and a gift
    /// that lands on a deleted account is money with nowhere to go.</para>
    ///
    /// <para>So this asserts that the new read did not become the only read. If someone later
    /// "simplifies" the three into one, this is the test that says which behaviours were being paid
    /// for.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public void The_filtered_reads_still_refuse_a_deleted_account()
    {
        var grain = GetGrainFactory().GetGrain<IUserGrain>(victimId);

        Assert.Multiple(() =>
        {
            Assert.That(async () => await grain.GetMe(), Throws.Exception,
                "GetMe answered for a deleted account, so an erased user can still read their own row");
            Assert.That(async () => await grain.GetAsArgonUser(), Throws.Exception,
                "GetAsArgonUser answered for a deleted account, so every caller using it as an "
              + "existence check — Xsolla, Ultima gifting, the bot user cache — now believes one exists");
        });
    }

    // ── ACC-08: a reference count that stays a count ─────────────────────────────────────────────

    /// <summary>
    /// Releasing a file more times than it was referenced leaves the count at zero, never below it.
    /// </summary>
    /// <remarks>
    /// <para>The contract: <c>IFileStorageGrain.DecrementRefAsync</c> is saturating. A file finalised
    /// at one reference and released twice ends at zero, and the second release is logged rather than
    /// silently written through.</para>
    ///
    /// <para>Defect ACC-08 reached this through account deletion, which releases the avatar by id in
    /// <c>AnonymizeUserAsync</c> and then walks every file the account owns — the avatar included — in
    /// <c>DecrementFileRefsAsync</c>, so a seeded avatar ended a deletion at minus one. That double
    /// release is deliberate belt-and-braces (the targeted one reaches an avatar the account does not
    /// own, the walk reaches everything else) and is not what this test is about. What it is about is
    /// the storage invariant underneath: nothing today reads the count except
    /// <c>FileGcService</c>'s <c>RefCount &lt;= 0</c> sweep, so zero and minus one are the same state
    /// <em>only for as long as no file ever legitimately holds two references</em>. The day one does —
    /// an increment when an avatar or attachment is assigned, which is the natural fix for
    /// <c>UserGrain.UpdateMe</c> accepting an avatar id it never counted — an over-release starts
    /// collecting files that are still in use, and the ownership-free
    /// <c>POST /api/files/{id}/decrement</c> endpoint puts that within anyone's reach.</para>
    ///
    /// <para>The file is uploaded through the product's own path rather than seeded, so the counter
    /// under test is the one <c>FinalizeUploadAsync</c> creates and not a fixture's idea of it.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task Releasing_a_file_twice_leaves_its_reference_count_at_zero(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var owner  = await CreateSessionAsync(ct);
        var ticket = await owner.Users.BeginUploadAvatar(ct);

        Assert.That(ticket, Is.InstanceOf<SuccessUploadFile>(),
            $"the server would not sign an upload: {(ticket as FailedUploadFile)?.error}");

        await UploadAsync((SuccessUploadFile)ticket, Png);
        await owner.Users.CompleteUploadAvatar(((SuccessUploadFile)ticket).blobId, ct);

        var me = await owner.Users.GetMe(ct);

        Assert.That(me.avatarFileId, Is.Not.Null.And.Not.Empty,
            "the upload finalised and the account has no avatar, so there is no counter to release");

        var fileId  = Guid.Parse(me.avatarFileId!);
        var files   = GetGrainFactory().GetGrain<IFileStorageGrain>(owner.UserId);
        // Scoped, so it comes out of the scope rather than the root provider.
        var counter = scope.ServiceProvider.GetRequiredService<IReferenceCountService>();

        Assert.That(await counter.GetRefCountAsync(fileId, ct), Is.EqualTo(1),
            "a finalised upload starts at exactly one reference");

        await files.DecrementRefAsync(fileId, ct);

        var afterFirst = await counter.GetRefCountAsync(fileId, ct);

        await files.DecrementRefAsync(fileId, ct);

        var afterSecond = await counter.GetRefCountAsync(fileId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(afterFirst, Is.EqualTo(0),
                "the one reference the upload created was not released");

            // Zero, not "at most zero": the release is saturating, so a count is always a count of
            // something. A negative one is a released reference nobody held, and it means the number
            // can no longer answer whether the bytes are still needed.
            Assert.That(afterSecond, Is.EqualTo(0),
                "a second release drove the reference count below zero");
        });
    }

    // ── R1: an avatar id is a claim about a file, and it has to be checked ───────────────────────

    /// <summary>
    /// A profile edit may point at a file the account owns, and at nothing else.
    /// </summary>
    /// <remarks>
    /// <para><b>The contract.</b> <c>UserEditInput.avatarId</c> is a file id the client sends, and
    /// <c>UserGrain.UpdateProfileAsync</c> used to assign it to <c>Users.AvatarFileId</c> unread. Two
    /// things follow from that, and the second is why this is filed with the security findings.
    /// A profile pointing at somebody else's upload is broken the moment that person deletes it —
    /// and, worse, an erasure releases the avatar file by id
    /// (<c>AccountDeletionGrain.AnonymizeUserAsync</c>), so an account could name a stranger's file
    /// and then have its own deletion drop that file's last reference. The comment in the erasure that
    /// preserves the targeted release says exactly this: it is the branch that "reaches an avatar the
    /// user does not own". This closes it at the source.</para>
    ///
    /// <para>Both halves are asserted, because a guard that refuses everybody would pass the first
    /// one: an account setting its <em>own</em> finalised avatar through the same call still
    /// succeeds. That is the ordinary path a client takes after an upload, and breaking it would be a
    /// worse defect than the one being fixed.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task An_avatar_that_belongs_to_another_account_is_refused(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var claimt = await CreateSessionAsync(ct);

        var ticket = await owner.Users.BeginUploadAvatar(ct);

        Assert.That(ticket, Is.InstanceOf<SuccessUploadFile>(),
            $"the server would not sign an upload: {(ticket as FailedUploadFile)?.error}");

        await UploadAsync((SuccessUploadFile)ticket, Png);
        await owner.Users.CompleteUploadAvatar(((SuccessUploadFile)ticket).blobId, ct);

        var ownersAvatar = (await owner.Users.GetMe(ct)).avatarFileId;

        Assert.That(ownersAvatar, Is.Not.Null.And.Not.Empty,
            "the upload finalised and the owner has no avatar, so there is no id to point at");

        var stolen = await claimt.Users.UpdateMe(
            new UserEditInput(null, ownersAvatar, null, null, null, null, null, null, null, null, null), ct);

        var claimant = await claimt.Users.GetMe(ct);

        // The owner re-asserting its own avatar: the same call, the same id, and it has to work.
        var kept = await owner.Users.UpdateMe(
            new UserEditInput(null, ownersAvatar, null, null, null, null, null, null, null, null, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(stolen, Is.InstanceOf<FailedUpdateMe>(),
                "an account set its avatar to a file another account owns, so its own deletion will "
              + "release a stranger's file and that stranger can delete this account's avatar");
            Assert.That(claimant.avatarFileId, Is.Not.EqualTo(ownersAvatar),
                "the refusal did not stop the write");

            Assert.That(kept, Is.InstanceOf<SuccessUpdateMe>(),
                $"the owner can no longer set its own avatar: {(kept as FailedUpdateMe)?.error}");
        });
    }

    // ── ACC-16: an optional field is not a crash ─────────────────────────────────────────────────

    /// <summary>
    /// Signing in without an e-mail address is refused, not answered with an internal error.
    /// </summary>
    /// <remarks>
    /// <para>The contract: <c>email</c> is optional on <c>UserCredentialsInput</c> — the schema
    /// declares it nullable and the formatter reads it as such — so omitting it is a request the
    /// product published as legal, and the answer to it is <c>BAD_CREDENTIALS</c>, the same answer an
    /// address nobody holds gets. A blank one is the same case and gets the same answer.</para>
    ///
    /// <para>Defect ACC-16 was a regression rather than an old shape: commit 97d08aa6 moved the lookup
    /// onto the normalised column for case-insensitivity and dereferenced <c>input.email</c> inside
    /// the LINQ expression tree, so a null became a <c>NullReferenceException</c> on an
    /// unauthenticated path — <c>UPSTREAM_ERROR</c> here, <c>500 server_error</c> on the OAuth
    /// endpoint — with no attempt recorded, and, because
    /// <c>IdentityInteraction.Authorize</c> skips the per-e-mail throttle when there is no e-mail to
    /// key it on, cheap to repeat.</para>
    ///
    /// <para><b>The username is passed and is expected to be ignored.</b> Sign-in by username has
    /// never existed in this product and neither client offers it; adding one would widen the
    /// account-enumeration surface and is a product decision, not a bug fix. What is asserted is only
    /// that the ignored fields do not crash the door — a caller who fills in the wrong ones is wrong,
    /// not fatal.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task Signing_in_without_an_email_is_refused_rather_than_answered_with_a_500(
        CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var account  = await CreateSessionAsync(ct);
        var identity = GetIdentityService(scope.ServiceProvider);

        // The credentials are real and correct — only the field naming the account is missing, so a
        // refusal here is about the missing e-mail and nothing else.
        var missing = await identity.Authorize(
            new UserCredentialsInput(null, null, account.Credentials.username, account.Credentials.password,
                null, null), ct);

        var blank = await identity.Authorize(
            new UserCredentialsInput("   ", null, account.Credentials.username, account.Credentials.password,
                null, null), ct);

        Assert.Multiple(() =>
        {
            Assert.That(missing, Is.InstanceOf<FailedAuthorize>(),
                "a sign-in with no e-mail address was not refused");
            Assert.That((missing as FailedAuthorize)?.error, Is.EqualTo(AuthorizationError.BAD_CREDENTIALS),
                "a missing e-mail has to read as an e-mail nobody holds, not as a distinct outcome");

            Assert.That(blank, Is.InstanceOf<FailedAuthorize>(),
                "a sign-in with a blank e-mail address was not refused");
            Assert.That((blank as FailedAuthorize)?.error, Is.EqualTo(AuthorizationError.BAD_CREDENTIALS));
        });
    }

    /// <summary>
    /// The OIDC door answers the same way when the e-mail is missing.
    /// </summary>
    /// <remarks>
    /// <para>The second half of ACC-16, and a separate site rather than a shared one:
    /// <c>ExternalAuthorize</c> is its own copy of the lookup, reached from
    /// <c>AuthController.AuthorizeOAuth</c>, where an unhandled exception is caught and turned into
    /// <c>500 server_error</c> — a browser sitting on the Aegis sign-in form being told the server
    /// broke rather than that the form is incomplete.</para>
    ///
    /// <para>Driven through <c>IAuthorizationGrain</c>, which is the seam the controller itself calls,
    /// rather than over HTTP: the OAuth endpoint needs a registered application and a browser session
    /// before it reaches the credential check, and none of that is what is being asserted here.</para>
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task The_oidc_door_also_refuses_a_missing_email_rather_than_crashing(
        CancellationToken ct = default)
    {
        var account = await CreateSessionAsync(ct);

        var result = await GetGrainFactory().GetGrain<IAuthorizationGrain>(Guid.NewGuid())
           .ExternalAuthorize(new UserCredentialsInput(
                null, null, account.Credentials.username, account.Credentials.password, null, null));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False,
                "an OAuth sign-in with no e-mail address was accepted");
            Assert.That(result.Error, Is.EqualTo(AuthorizationError.BAD_CREDENTIALS),
                "the OIDC door has to refuse a missing e-mail the way the Ion one does");
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends the bytes exactly as the signature demands, straight to the store.
    /// </summary>
    /// <remarks>
    /// The same trick <c>MediaUploadTests</c> uses and for the same reason: the server never sees the
    /// bytes, and <c>argon-test.localhost</c> is a name no resolver answers for while the URL has to
    /// keep it because the host is signed into the request. Overriding the connection rather than the
    /// URL leaves the signature — and therefore the upload being tested — untouched.
    /// </remarks>
    private static async Task UploadAsync(SuccessUploadFile ticket, byte[] payload)
    {
        var port = int.Parse(ArgonTestEnvironment.Instance.S3Endpoint.Split(':')[1]);

        using var client = new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                await socket.ConnectAsync(IPAddress.Loopback, port, token);

                return new NetworkStream(socket, ownsSocket: true);
            }
        });

        using var content = new ByteArrayContent(payload);

        content.Headers.TryAddWithoutValidation("Content-Type", "image/png");

        foreach (var field in ticket.formFields)
            content.Headers.TryAddWithoutValidation(field.key, field.value);

        using var response = await client.PutAsync(ticket.uploadUrl, content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"the object store refused the presigned upload: {await response.Content.ReadAsStringAsync()}");
    }
}
