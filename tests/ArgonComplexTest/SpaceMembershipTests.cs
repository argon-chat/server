namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using Microsoft.EntityFrameworkCore;
using static SpaceGroupSupport;

/// <summary>
/// Getting into a space and being known there: invites, joins, departures, and what the space
/// answers about people who are — or are not — its members.
/// </summary>
/// <remarks>
/// <para>An invite code is the only door into a space, so the invite tests are mostly about the
/// ways a door should stay shut: an unreadable code, an expired one, one used up, one whose space is
/// gone, and — the one that had no guard at all — somebody minting or revoking codes for a space
/// they have no say in.</para>
///
/// <para>The rest pins the idempotence the join and leave paths promise (a second join is not a
/// second membership, a second departure is not a second announcement) and the placeholders the
/// profile lookups hand back for ids there is nothing to say about.</para>
/// </remarks>
[TestFixture]
public class SpaceMembershipTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private static async Task<List<Guid>> RosterAsync(TestUserSession viewer, Guid spaceId, CancellationToken ct)
        => (await viewer.Servers.GetSpaceSnapshot(spaceId, null, ct)).members!.Value.Values.Select(m => m.userId).ToList();

    private static async Task SetInviteAsync(Guid spaceId, DateTimeOffset? expireAt = null, int? maxUses = null, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);

        if (expireAt is { } at)
            await db.Invites.Where(i => i.SpaceId == spaceId).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpireAt, at), ct);
        if (maxUses is { } uses)
            await db.Invites.Where(i => i.SpaceId == spaceId).ExecuteUpdateAsync(s => s.SetProperty(i => i.MaxUses, uses), ct);
    }

    // ── Joining and leaving ─────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task JoinToSpace_Twice_IsOneMembershipAndOneUseOfTheInvite(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var guest   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Twice", ct);
        var invite  = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();

        var first  = await guest.Users.JoinToSpace(invite, ct);
        var second = await guest.Users.JoinToSpace(invite, ct);

        var roster = await RosterAsync(owner, spaceId, ct);
        var used   = (await owner.Servers.GetInviteCodes(spaceId, ct).Ok()).invites.Values.Single().used;

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.InstanceOf<SuccessJoin>());
            Assert.That(second, Is.InstanceOf<SuccessJoin>(), "a member following the link again is simply let in");
            Assert.That(roster.Count(id => id == guest.UserId), Is.EqualTo(1));
            Assert.That(used, Is.EqualTo(1UL), "a join that added nobody spent a use of the invite");
        });
    }

    /// <summary>
    /// A departure is announced once. The second removal finds no membership and says nothing — the
    /// property a resumed account erasure relies on.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task RemoveMember_Twice_AnnouncesTheDepartureOnce(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var leaver  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Departures", ct);
        await JoinAsync(owner, leaver, spaceId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(owner, ct);
        await watcher.SubscribeToSpace(spaceId, ct);
        var mark = watcher.Mark();

        await SpaceGrain(spaceId).RemoveMemberAsync(leaver.UserId);
        await watcher.WaitForAsync<LeavedFromServerUser>(e => e.userId == leaver.UserId, EventWait, mark, ct);

        var afterFirst = watcher.Mark();
        await SpaceGrain(spaceId).RemoveMemberAsync(leaver.UserId);

        await watcher.AssertNoneWithinAsync<LeavedFromServerUser>(e => e.userId == leaver.UserId, TimeSpan.FromSeconds(2),
            "a membership that was already gone was announced as leaving again", afterFirst, ct);

        Assert.That(await RosterAsync(owner, spaceId, ct), Does.Not.Contain(leaver.UserId));
    }

    // ── Profiles of people who are not (or no longer) there ─────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task PrefetchUser_ForAGuestOrAnUnknownId_AnswersAPlaceholder(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Placeholders", ct);

        var guestId   = GuestId();
        var unknownId = Guid.NewGuid();

        var guest   = await owner.Servers.PrefetchUser(spaceId, guestId, ct);
        var unknown = await owner.Servers.PrefetchUser(spaceId, unknownId, ct);
        var me      = await owner.Servers.PrefetchUser(spaceId, owner.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((guest.userId, guest.username, guest.displayName), Is.EqualTo((guestId, "guest", "Guest User")));
            Assert.That((unknown.userId, unknown.username), Is.EqualTo((unknownId, "unknown")));
            Assert.That(me.username, Is.EqualTo(owner.Credentials.username));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task PrefetchProfile_ForAGuestOrSomeoneWhoLeft_AnswersAPlaceholder(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var leaver  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Profiles", ct);
        await JoinAsync(owner, leaver, spaceId, ct);

        var whileIn = await owner.Servers.PrefetchProfile(spaceId, leaver.UserId, ct);
        await SpaceGrain(spaceId).RemoveMemberAsync(leaver.UserId);

        var afterLeaving = await owner.Servers.PrefetchProfile(spaceId, leaver.UserId, ct);
        var guestId      = GuestId();
        var guest        = await owner.Servers.PrefetchProfile(spaceId, guestId, ct);
        var guests       = await owner.Servers.PrefetchProfiles(spaceId, new IonArray<Guid>([guestId]), ct);

        Assert.Multiple(() =>
        {
            Assert.That(whileIn.bio, Is.Not.EqualTo("Deleted Account"));
            Assert.That(whileIn.archetypes.Values, Is.Not.Empty, "a member holds at least the default role");
            Assert.That(afterLeaving.bio, Is.EqualTo("Deleted Account"));
            Assert.That(afterLeaving.archetypes.Values, Is.Empty, "the roles held here left with the member");
            Assert.That((guest.userId, guest.bio), Is.EqualTo((guestId, "Guest User")));
            Assert.That(guests.Values.Select(p => p.bio), Is.EqualTo(new[] { "Guest User" }));
        });
    }

    /// <summary>The read side refuses the same outsider the roster does not list.</summary>
    [Test, CancelAfter(120_000)]
    public async Task The_space_snapshot_and_channel_list_are_for_members_only(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var outsider = await CreateSessionAsync(ct);
        var spaceId  = await CreateSpaceAsync(owner, "Members only", ct);
        await CreateChannelAsync(owner, spaceId, "inside", ct: ct);

        Assert.That((await owner.Servers.GetSpaceSnapshot(spaceId, null, ct)).channels!.Value.Values, Has.Count.EqualTo(1),
            "the same call from the owner should answer, or the refusals below prove nothing");

        Assert.Multiple(() =>
        {
            Assert.ThrowsAsync<IonRequestException>(() => outsider.Servers.GetSpaceSnapshot(spaceId, null, ct));
            Assert.ThrowsAsync<IonRequestException>(() => outsider.Channels.GetChannels(spaceId, Guid.Empty, ct));
        });
    }

    // ── Invite codes ────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task An_unreadable_code_leads_nowhere(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var guest   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Unreadable", ct);
        var invite  = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();

        var joined = await guest.Users.JoinToSpace(new ArgonContracts.InviteCode("!!!!!!!!!"), ct);

        // Revoking garbage is not an error and does not touch the codes that are there.
        await owner.Servers.RevokeInviteCode(spaceId, new ArgonContracts.InviteCode("!!!!!!!!!"), ct).Ok();
        var codes = await owner.Servers.GetInviteCodes(spaceId, ct).Ok();

        Assert.Multiple(() =>
        {
            Assert.That((joined as FailedJoin)?.error, Is.EqualTo(AcceptInviteError.NOT_FOUND));
            Assert.That(codes.invites.Values.Select(i => InviteCodeEntityData.RemoveSeparators(i.code.inviteCode)),
                Is.EqualTo(new[] { InviteCodeEntityData.RemoveSeparators(invite.inviteCode) }));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_expired_invite_lets_nobody_in(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var guest   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Expired", ct);
        var invite  = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();

        await SetInviteAsync(spaceId, expireAt: DateTimeOffset.UtcNow.AddMinutes(-1), ct: ct);

        var joined = await guest.Users.JoinToSpace(invite, ct);

        Assert.That((joined as FailedJoin)?.error, Is.EqualTo(AcceptInviteError.EXPIRED));
        Assert.That(await RosterAsync(owner, spaceId, ct), Does.Not.Contain(guest.UserId));
    }

    [Test, CancelAfter(120_000)]
    public async Task An_invite_stops_working_at_its_use_limit(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var first   = await CreateSessionAsync(ct);
        var second  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "One use", ct);
        var invite  = await owner.Servers.CreateInviteCode(spaceId, 60, 1, ct).Ok();

        var firstJoin  = await first.Users.JoinToSpace(invite, ct);
        var preview    = await second.Users.PreviewInvite(invite, ct);
        var secondJoin = await second.Users.JoinToSpace(invite, ct);
        var roster     = await RosterAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(firstJoin, Is.InstanceOf<SuccessJoin>());
            Assert.That((preview as FailedPreview)?.error, Is.EqualTo(AcceptInviteError.LIMIT_REACHED));
            Assert.That((secondJoin as FailedJoin)?.error, Is.EqualTo(AcceptInviteError.LIMIT_REACHED));
            Assert.That(roster, Does.Contain(first.UserId).And.Not.Contain(second.UserId));
        });
    }

    /// <summary>
    /// A deleted space keeps its invite rows — a deletion is a soft delete of the space row — so the
    /// invite has to be what notices the space is gone.
    /// </summary>
    /// <remarks>
    /// Without that, a preview of a live link to a deleted space failed with a server error, and a join
    /// through it wrote a membership into the deleted space before the lookup of the space it had just
    /// joined threw.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task An_invite_to_a_deleted_space_leads_nowhere(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var guest   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Deleted", ct);
        var invite  = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();

        await Grains.GetGrain<ISpaceDeletionGrain>(spaceId).DeleteNowAsync(owner.UserId);

        var preview = await guest.Users.PreviewInvite(invite, ct);
        var joined  = await guest.Users.JoinToSpace(invite, ct);

        await using var db = await NewDbAsync(ct);
        var rows = await db.UsersToServerRelations.IgnoreQueryFilters()
           .CountAsync(m => m.SpaceId == spaceId && m.UserId == guest.UserId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((preview as FailedPreview)?.error, Is.EqualTo(AcceptInviteError.NOT_FOUND));
            Assert.That((joined as FailedJoin)?.error, Is.EqualTo(AcceptInviteError.NOT_FOUND));
            Assert.That(rows, Is.Zero, "a membership was written into a deleted space");
        });

        // Deleting what is already deleted is nothing, not an error.
        Assert.DoesNotThrowAsync(() => SpaceGrain(spaceId).DeleteSpace());
    }

    /// <summary>
    /// Invite codes are a way into the space, so minting, listing and revoking them is space
    /// administration — the client shows the invites page to <c>ManageServer</c> only, and so must the
    /// server.
    /// </summary>
    /// <remarks>
    /// The grain checked nothing: any signed-in account that knew a space id — which every invite
    /// preview hands out — could list its live codes, mint fresh ones, or revoke the owner's.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task Invite_codes_are_managed_by_ManageServer_only(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var member   = await CreateSessionAsync(ct);
        var outsider = await CreateSessionAsync(ct);
        var spaceId  = await CreateSpaceAsync(owner, "Door keeping", ct);
        await JoinAsync(owner, member, spaceId, ct);

        var ownersCode = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();
        var before     = await CodesAsync(owner);

        foreach (var (who, name) in new[] { (outsider, "an outsider"), (member, "a plain member") })
        {
            await Assert.MultipleAsync(async () =>
            {
                Assert.That(await who.Servers.CreateInviteCode(spaceId, 60, 0, ct),
                    Is.EqualTo(new FailedCreateInviteCode(SpaceManageError.NO_PERMISSION)), $"{name} minted a code");
                Assert.That(await who.Servers.GetInviteCodes(spaceId, ct),
                    Is.EqualTo(new FailedGetInviteCodes(SpaceManageError.NO_PERMISSION)), $"{name} listed the codes");
                Assert.That(await who.Servers.RevokeInviteCode(spaceId, ownersCode, ct),
                    Is.EqualTo(new FailedSpaceManage(SpaceManageError.NO_PERMISSION)), $"{name} revoked a code");
            });
        }

        Assert.That(await CodesAsync(owner), Is.EquivalentTo(before), "a refused call changed the space's codes");

        await GrantAsync(owner, spaceId, member.UserId, ArgonEntitlement.ManageServer, ct);

        var membersCode = await member.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();
        await member.Servers.RevokeInviteCode(spaceId, ownersCode, ct).Ok();
        var afterGrant = await CodesAsync(member);

        Assert.That(afterGrant, Is.EquivalentTo(before
           .Where(c => c != InviteCodeEntityData.RemoveSeparators(ownersCode.inviteCode))
           .Append(InviteCodeEntityData.RemoveSeparators(membersCode.inviteCode))));

        async Task<List<string>> CodesAsync(TestUserSession who)
            => (await who.Servers.GetInviteCodes(spaceId, ct).Ok()).invites.Values
               .Select(i => InviteCodeEntityData.RemoveSeparators(i.code.inviteCode))
               .ToList();
    }
}
