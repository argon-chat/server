namespace ArgonComplexTest.Tests;

using Argon.Grains.Interfaces;
using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Who may change roles and channel overwrites — the refusals <see cref="ArchetypeTests"/> does not
/// reach, and the boundary between two spaces that one person administers one of and merely belongs
/// to the other.
/// </summary>
/// <remarks>
/// Calls go through <c>IArchetypeInteraction</c> as the member would, so the refusal is checked as
/// the client receives it. Only what has no Ion method calls <c>IEntitlementGrain</c> with the caller
/// set in the request context.
/// </remarks>
[TestFixture]
public class ArchetypePermissionTests : TestBase
{
    private IArchetypeInteraction Roles(TestUserSession session)
        => session.Client.ForService<IArchetypeInteraction>(FactoryAsp.Services);

    private IEntitlementGrain Entitlements(Guid spaceId) => GetGrainFactory().GetGrain<IEntitlementGrain>(spaceId);

    private static async Task<T> AsCaller<T>(Guid userId, Func<Task<T>> call)
    {
        RequestContext.Set("$caller_user_id", userId);

        try
        {
            return await call();
        }
        finally
        {
            RequestContext.Clear();
        }
    }

    private async Task<Guid> SpaceOfAsync(TestUserSession owner, CancellationToken ct)
    {
        var created = await owner.Users.CreateSpace(new CreateServerRequest("Roles", "", ""), ct);

        Assert.That(created, Is.InstanceOf<SuccessCreateSpace>());

        return ((SuccessCreateSpace)created).space.spaceId;
    }

    private static async Task JoinAsync(TestUserSession owner, TestUserSession joiner, Guid spaceId, CancellationToken ct)
    {
        var code = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);

        Assert.That(await joiner.Users.JoinToSpace(code, ct), Is.InstanceOf<SuccessJoin>());
    }

    private static async Task<Guid> MemberIdAsync(TestUserSession reader, Guid spaceId, Guid userId, CancellationToken ct)
        => (await reader.Servers.GetMembers(spaceId, ct)).Values.Single(m => m.member.userId == userId).member.memberId;

    /// <summary>A role the owner creates and gives exactly <paramref name="entitlement"/>.</summary>
    private async Task<Archetype> RoleAsync(TestUserSession owner, Guid spaceId, string name, ArgonEntitlement entitlement, CancellationToken ct)
    {
        var created = await Roles(owner).CreateArchetype(spaceId, name, ct);

        return await Roles(owner).UpdateArchetype(spaceId, created with { entitlement = entitlement }, ct);
    }

    private async Task GrantAsync(TestUserSession owner, Guid spaceId, Guid memberId, Archetype role, CancellationToken ct)
        => Assert.That(await Roles(owner).SetArchetypeToMember(spaceId, memberId, role.id, true, ct), Is.True);

    private async Task<Archetype> ReadRoleAsync(TestUserSession reader, Guid spaceId, Guid roleId, CancellationToken ct)
        => (await Roles(reader).GetServerArchetypes(spaceId, ct)).Values.Single(a => a.id == roleId);

    // ── A member without the right ───────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_member_without_the_right_sees_the_roles_but_not_who_holds_them(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await SpaceOfAsync(owner, ct);

        await JoinAsync(owner, member, spaceId, ct);

        var seenByOwner  = (await Roles(owner).GetDetailedServerArchetypes(spaceId, ct)).Values;
        var seenByMember = (await Roles(member).GetDetailedServerArchetypes(spaceId, ct)).Values;

        Assert.Multiple(() =>
        {
            Assert.That(seenByMember.Select(g => g.archetype.id), Is.EquivalentTo(seenByOwner.Select(g => g.archetype.id)));
            Assert.That(seenByOwner.SelectMany(g => g.members.Values), Is.Not.Empty);
            Assert.That(seenByMember.SelectMany(g => g.members.Values), Is.Empty, "who holds which role is the managers' to see");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_member_without_the_right_cannot_create_reorder_or_regrant_roles(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var member  = await CreateSessionAsync(ct);
        var spaceId = await SpaceOfAsync(owner, ct);

        await JoinAsync(owner, member, spaceId, ct);

        var plain    = await RoleAsync(owner, spaceId, "plain", ArgonEntitlementKit.Base, ct);
        var memberId = await MemberIdAsync(owner, spaceId, member.UserId, ct);
        var all      = (await Roles(member).GetServerArchetypes(spaceId, ct)).Values.Select(a => a.id).ToArray();

        AssertRefused(() => Roles(member).CreateArchetype(spaceId, "mine", ct), "NO_PERMISSION");

        var reorder = await Roles(member).ReorderArchetypes(spaceId, new IonArray<Guid>(all), ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((reorder as FailedReorderArchetypes)?.error, Is.EqualTo(ArchetypeError.NO_PERMISSION));
            Assert.That(await Roles(member).SetArchetypeToMember(spaceId, memberId, plain.id, true, ct), Is.False);
            Assert.That((await Roles(owner).GetServerArchetypes(spaceId, ct)).Values.Select(a => a.name), Does.Not.Contain("mine"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_member_without_the_right_cannot_touch_channel_overwrites(CancellationToken ct = default)
    {
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"guarded-{Guid.NewGuid():N}"[..20], ct);
        var roles     = IonClient.ForService<IArchetypeInteraction>(FactoryAsp.Services);
        var everyone  = (await roles.GetServerArchetypes(spaceId, ct)).Values.Single(a => a.isDefault);
        var existing  = await roles.UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            ArgonEntitlement.SendMessages, ArgonEntitlement.None, ct);

        var member = await CreateSessionAsync(ct);
        var code   = await GetServerService().CreateInviteCode(spaceId, 60, 0, ct);
        Assert.That(await member.Users.JoinToSpace(code, ct), Is.InstanceOf<SuccessJoin>());

        var memberId = await MemberIdAsync(member, spaceId, member.UserId, ct);

        var upsert = await Roles(member).UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            ArgonEntitlement.None, ArgonEntitlement.SendMessages, ct);
        var removed = await Roles(member).DeleteEntitlementForChannel(spaceId, channelId, existing!.id, ct);
        var memberOverwrite = await AsCaller(member.UserId, () => Entitlements(spaceId)
           .UpsertMemberEntitlementForChannel(channelId, memberId, ArgonEntitlement.None, ArgonEntitlement.ManageChannels));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(upsert, Is.Null);
            Assert.That(removed, Is.False);
            Assert.That(memberOverwrite, Is.Null);

            var overwrites = (await roles.GetChannelEntitlementOverwrites(spaceId, channelId, ct)).Values;
            Assert.That(overwrites.Select(o => (o.id, o.deny, o.allow)),
                Is.EqualTo(new[] { (existing.id, ArgonEntitlement.SendMessages, ArgonEntitlement.None) }));
        });
    }

    // ── Editing roles ────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Updating_a_role_is_refused_to_a_stranger_and_for_a_role_the_space_does_not_have(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var spaceId  = await SpaceOfAsync(owner, ct);
        var role     = await Roles(owner).CreateArchetype(spaceId, "kept", ct);

        AssertUpdateRefused(() => Roles(stranger).UpdateArchetype(spaceId, role with { name = "taken" }, ct));
        AssertUpdateRefused(() => Roles(owner).UpdateArchetype(spaceId, role with { id = Guid.NewGuid() }, ct));
        AssertUpdateRefused(() => Roles(owner).UpdateArchetype(spaceId, role with { name = " " }, ct));

        Assert.That((await ReadRoleAsync(owner, spaceId, role.id, ct)).name, Is.EqualTo("kept"));
    }

    // The settings screen's debounced save landing after the role was deleted.
    [Test, CancelAfter(120_000)]
    public async Task Updating_a_deleted_role_is_refused_rather_than_failing(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await SpaceOfAsync(owner, ct);
        var role    = await Roles(owner).CreateArchetype(spaceId, "doomed", ct);

        Assert.That(await Roles(owner).DeleteArchetype(spaceId, role.id, ct), Is.InstanceOf<SuccessDeleteArchetype>());

        AssertUpdateRefused(() => Roles(owner).UpdateArchetype(spaceId, role with { name = "renamed" }, ct));
    }

    // Checks the code: a server crash also arrives as IonRequestException, just with INTERNAL_ERROR.
    private static void AssertRefused(Func<Task> call, string code)
    {
        var refused = Assert.ThrowsAsync<IonRequestException>(async () => await call());
        Assert.That(refused?.Error.code, Is.EqualTo(code), $"{refused?.Error}");
    }

    private static void AssertUpdateRefused(Func<Task<Archetype>> call) => AssertRefused(call, "ARCHETYPE_UPDATE_REFUSED");

    [Test, CancelAfter(120_000)]
    public async Task A_moderator_edits_roles_beneath_them_and_nothing_above(CancellationToken ct = default)
    {
        var owner     = await CreateSessionAsync(ct);
        var moderator = await CreateSessionAsync(ct);
        var spaceId   = await SpaceOfAsync(owner, ct);

        await JoinAsync(owner, moderator, spaceId, ct);

        var mods   = await RoleAsync(owner, spaceId, "mods", ArgonEntitlement.ManageArchetype | ArgonEntitlementKit.Base, ct);
        var admins = await RoleAsync(owner, spaceId, "admins", ArgonEntitlement.ManageServer, ct);
        var plain  = await RoleAsync(owner, spaceId, "plain", ArgonEntitlementKit.Base, ct);

        var moderatorId = await MemberIdAsync(owner, spaceId, moderator.UserId, ct);
        await GrantAsync(owner, spaceId, moderatorId, mods, ct);

        // Handing out a right the moderator does not hold.
        AssertUpdateRefused(() => Roles(moderator).UpdateArchetype(spaceId, plain with { entitlement = plain.entitlement | ArgonEntitlement.ManageServer }, ct));

        // Renaming a role that outranks them.
        AssertUpdateRefused(() => Roles(moderator).UpdateArchetype(spaceId, admins with { name = "demoted" }, ct));

        // Granting a role that outranks them, and deleting one.
        var grantedAdmins = await Roles(moderator).SetArchetypeToMember(spaceId, moderatorId, admins.id, true, ct);
        var deletedAdmins = await Roles(moderator).DeleteArchetype(spaceId, admins.id, ct);

        // And what is theirs to change.
        var renamedPlain = await Roles(moderator).UpdateArchetype(spaceId, plain with { name = "regulars", colour = unchecked((int)0xFF3366CC) }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(grantedAdmins, Is.False);
            Assert.That((deletedAdmins as FailedDeleteArchetype)?.error, Is.EqualTo(ArchetypeError.NO_PERMISSION));

            Assert.That(renamedPlain.colour, Is.EqualTo(unchecked((int)0xFF3366CC)));

            var plainNow  = await ReadRoleAsync(owner, spaceId, plain.id, ct);
            var adminsNow = await ReadRoleAsync(owner, spaceId, admins.id, ct);

            Assert.That(plainNow.entitlement, Is.EqualTo(ArgonEntitlementKit.Base));
            Assert.That(plainNow.name, Is.EqualTo("regulars"));
            Assert.That(adminsNow.name, Is.EqualTo("admins"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_server_manager_may_raise_a_role_past_their_own_rights(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var manager = await CreateSessionAsync(ct);
        var spaceId = await SpaceOfAsync(owner, ct);

        await JoinAsync(owner, manager, spaceId, ct);

        var managers = await RoleAsync(owner, spaceId, "managers", ArgonEntitlement.ManageServer | ArgonEntitlement.ManageArchetype, ct);
        var plain    = await RoleAsync(owner, spaceId, "plain", ArgonEntitlementKit.Base, ct);

        await GrantAsync(owner, spaceId, await MemberIdAsync(owner, spaceId, manager.UserId, ct), managers, ct);

        var raised = ArgonEntitlementKit.Base | ArgonEntitlement.ManageBots | ArgonEntitlement.ManageEvents;
        var result = await Roles(manager).UpdateArchetype(spaceId, plain with { entitlement = raised }, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(result.entitlement, Is.EqualTo(raised));
            Assert.That((await ReadRoleAsync(owner, spaceId, plain.id, ct)).entitlement, Is.EqualTo(raised));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task The_owner_role_cannot_be_deleted(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await SpaceOfAsync(owner, ct);

        var ownerRole = (await Roles(owner).GetServerArchetypes(spaceId, ct)).Values.Single(a => a.isLocked);
        var result    = await Roles(owner).DeleteArchetype(spaceId, ownerRole.id, ct);

        Assert.That((result as FailedDeleteArchetype)?.error, Is.EqualTo(ArchetypeError.IS_LOCKED));
        Assert.That((await Roles(owner).GetServerArchetypes(spaceId, ct)).Values.Select(a => a.id), Does.Contain(ownerRole.id));
    }

    // ── Granting roles ───────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Granting_is_refused_to_strangers_and_for_unknown_roles_and_repeating_a_grant_is_harmless(CancellationToken ct = default)
    {
        var owner    = await CreateSessionAsync(ct);
        var stranger = await CreateSessionAsync(ct);
        var spaceId  = await SpaceOfAsync(owner, ct);
        var ownerId  = await MemberIdAsync(owner, spaceId, owner.UserId, ct);
        var plain    = await Roles(owner).CreateArchetype(spaceId, "plain", ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await Roles(stranger).SetArchetypeToMember(spaceId, ownerId, plain.id, true, ct), Is.False);
            Assert.That(await Roles(owner).SetArchetypeToMember(spaceId, ownerId, Guid.NewGuid(), true, ct), Is.False);

            Assert.That(await Roles(owner).SetArchetypeToMember(spaceId, ownerId, plain.id, true, ct), Is.True);
            Assert.That(await Roles(owner).SetArchetypeToMember(spaceId, ownerId, plain.id, true, ct), Is.True, "a second grant of a held role is not an error");
        });

        var holders = (await Roles(owner).GetDetailedServerArchetypes(spaceId, ct)).Values.Single(g => g.archetype.id == plain.id).members.Values;

        Assert.That(holders, Is.EqualTo(new[] { ownerId }));
    }

    // ── Member overwrites ────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task A_member_overwrite_is_written_once_and_then_changed_in_place(CancellationToken ct = default)
    {
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var ownerId   = (await GetUserService().GetMe(ct)).userId;
        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"personal-{Guid.NewGuid():N}"[..20], ct);
        var roles     = IonClient.ForService<IArchetypeInteraction>(FactoryAsp.Services);

        var member = await CreateSessionAsync(ct);
        var code   = await GetServerService().CreateInviteCode(spaceId, 60, 0, ct);
        Assert.That(await member.Users.JoinToSpace(code, ct), Is.InstanceOf<SuccessJoin>());

        var memberId = await MemberIdAsync(member, spaceId, member.UserId, ct);
        var grain    = Entitlements(spaceId);

        var first = await AsCaller(ownerId, () => grain.UpsertMemberEntitlementForChannel(channelId, memberId,
            ArgonEntitlement.SendMessages, ArgonEntitlement.None));
        var second = await AsCaller(ownerId, () => grain.UpsertMemberEntitlementForChannel(channelId, memberId,
            ArgonEntitlement.None, ArgonEntitlement.AttachFiles));

        var overwrites = (await roles.GetChannelEntitlementOverwrites(spaceId, channelId, ct)).Values;

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.Null);
            Assert.That(first!.serverMemberId, Is.EqualTo(memberId));
            Assert.That(first.archetypeId, Is.Null);

            Assert.That(second!.id, Is.EqualTo(first.id), "the second upsert wrote a new overwrite");
            Assert.That(overwrites.Select(o => (o.id, o.serverMemberId, o.deny, o.allow)),
                Is.EqualTo(new[] { (first.id, (Guid?)memberId, ArgonEntitlement.None, ArgonEntitlement.AttachFiles) }));
        });
    }

    // ── Between two spaces ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>SetArchetypeToMember</c> checked that the role belongs to the space it was called for, but
    /// never that the member did. A member id is a row in <em>any</em> space, and a member's rights in
    /// a space are every role on their row there — so the owner of one space could hang its owner role
    /// (administrator, every right) on their own membership of somebody else's space and run it.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_role_of_one_space_cannot_be_hung_on_a_membership_of_another(CancellationToken ct = default)
    {
        var attacker = await CreateSessionAsync(ct);
        var victim   = await CreateSessionAsync(ct);

        var ownSpace    = await SpaceOfAsync(attacker, ct);
        var victimSpace = await SpaceOfAsync(victim, ct);

        await JoinAsync(victim, attacker, victimSpace, ct);

        var ownerRole          = (await Roles(attacker).GetServerArchetypes(ownSpace, ct)).Values.Single(a => a.isLocked);
        var membershipElsewhere = await MemberIdAsync(attacker, victimSpace, attacker.UserId, ct);

        var granted = await Roles(attacker).SetArchetypeToMember(ownSpace, membershipElsewhere, ownerRole.id, true, ct);

        Assert.That(granted, Is.False, "a role was granted to a member of another space");

        var all     = (await Roles(attacker).GetServerArchetypes(victimSpace, ct)).Values.Select(a => a.id).Reverse().ToArray();
        var reorder = await Roles(attacker).ReorderArchetypes(victimSpace, new IonArray<Guid>(all), ct);

        Assert.That((reorder as FailedReorderArchetypes)?.error, Is.EqualTo(ArchetypeError.NO_PERMISSION),
            "the attacker runs the other space's roles");
    }

    /// <summary>
    /// The channel overwrite calls checked the caller's rights in the space they were called for and
    /// then wrote to whatever channel id they were given — so a space owner could hide, open or strip
    /// the overwrites of a channel in any other space by naming it.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_channel_of_another_space_cannot_be_overwritten_through_ones_own(CancellationToken ct = default)
    {
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var victimSpace   = await CreateSpaceAndGetIdAsync(ct);
        var victimChannel = await CreateTextChannelAsync(victimSpace, $"victim-{Guid.NewGuid():N}"[..20], ct);
        var victimRoles   = IonClient.ForService<IArchetypeInteraction>(FactoryAsp.Services);
        var everyone      = (await victimRoles.GetServerArchetypes(victimSpace, ct)).Values.Single(a => a.isDefault);
        var legit         = await victimRoles.UpsertArchetypeEntitlementForChannel(victimSpace, victimChannel, everyone.id,
            ArgonEntitlement.None, ArgonEntitlement.ViewChannel, ct);

        var attacker = await CreateSessionAsync(ct);
        var code     = await GetServerService().CreateInviteCode(victimSpace, 60, 0, ct);
        Assert.That(await attacker.Users.JoinToSpace(code, ct), Is.InstanceOf<SuccessJoin>());

        var ownSpace         = await SpaceOfAsync(attacker, ct);
        var victimMembership = await MemberIdAsync(attacker, victimSpace, attacker.UserId, ct);

        var hidden = await Roles(attacker).UpsertArchetypeEntitlementForChannel(ownSpace, victimChannel, everyone.id,
            ArgonEntitlement.ViewChannel, ArgonEntitlement.None, ct);
        var stripped = await Roles(attacker).DeleteEntitlementForChannel(ownSpace, victimChannel, legit!.id, ct);
        var personal = await AsCaller(attacker.UserId, () => Entitlements(ownSpace).UpsertMemberEntitlementForChannel(
            victimChannel, victimMembership, ArgonEntitlement.None, ArgonEntitlementKit.Administrator));

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(hidden, Is.Null, "an overwrite was written on another space's channel");
            Assert.That(stripped, Is.False, "an overwrite was removed from another space's channel");
            Assert.That(personal, Is.Null, "a member overwrite was written on another space's channel");

            var overwrites = (await victimRoles.GetChannelEntitlementOverwrites(victimSpace, victimChannel, ct)).Values;
            Assert.That(overwrites.Select(o => (o.id, o.deny, o.allow)),
                Is.EqualTo(new[] { (legit.id, ArgonEntitlement.None, ArgonEntitlement.ViewChannel) }));
        });
    }
}
