namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;

/// <summary>
/// <c>ServerInteraction.GetMyEntitlements</c>, the rule that nobody manages a channel they cannot see,
/// and the <c>EntitlementsChanged</c> signal clients refetch on.
/// </summary>
[TestFixture]
public class MemberEntitlementsTests : TestBase
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task The_owner_may_do_everything_and_a_member_what_everyone_may(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "open", ChannelType.Voice, ct);

        var mine   = await owner.Servers.GetMyEntitlements(spaceId, ct);
        var theirs = await member.Servers.GetMyEntitlements(spaceId, ct);

        var ownerHere  = mine.channels.Values.Single(c => c.channelId == channelId).entitlements;
        var memberHere = theirs.channels.Values.Single(c => c.channelId == channelId).entitlements;

        Assert.Multiple(() =>
        {
            Assert.That(mine.space.HasFlag(ArgonEntitlement.ManageServer | ArgonEntitlement.DeafenMember), Is.True);
            Assert.That(ownerHere.HasFlag(ArgonEntitlement.ManageChannels | ArgonEntitlement.Connect), Is.True);
            Assert.That(memberHere.HasFlag(ArgonEntitlement.Connect | ArgonEntitlement.Speak | ArgonEntitlement.ViewChannel), Is.True);
            Assert.That(memberHere.HasFlag(ArgonEntitlement.ManageChannels), Is.False);
            Assert.That(theirs.space.HasFlag(ArgonEntitlement.ManageServer), Is.False);
        });
    }

    // Production "everyone" roles predate JoinToVoice and no role editor grants it.
    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task A_member_may_join_voice_through_an_everyone_role_without_JoinToVoice(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId  = await CreateChannelAsync(owner, spaceId, "lobby", ChannelType.Voice, ct);
        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(archetypes, spaceId, ct);

        await archetypes.UpdateArchetype(spaceId, everyone with { entitlement = everyone.entitlement & ~ArgonEntitlement.JoinToVoice }, ct).Ok();

        var theirs = await member.Servers.GetMyEntitlements(spaceId, ct);
        var here   = theirs.channels.Values.Single(c => c.channelId == channelId).entitlements;

        Assert.That(here.HasFlag(ArgonEntitlement.Connect | ArgonEntitlement.Speak), Is.True);
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Channel_overwrites_shape_what_the_member_is_told(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var locked = await CreateChannelAsync(owner, spaceId, "locked", ChannelType.Voice, ct);
        var hidden = await CreateChannelAsync(owner, spaceId, "hidden", ChannelType.Text, ct);

        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(archetypes, spaceId, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, locked, everyone.id,
            deny: ArgonEntitlement.Connect, allow: ArgonEntitlement.None, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, hidden, everyone.id,
            deny: ArgonEntitlement.ViewChannel, allow: ArgonEntitlement.None, ct);

        var theirs = await member.Servers.GetMyEntitlements(spaceId, ct);
        var lockedHere = theirs.channels.Values.Single(c => c.channelId == locked).entitlements;

        Assert.Multiple(() =>
        {
            Assert.That(lockedHere.HasFlag(ArgonEntitlement.ViewChannel), Is.True, "a locked room is still visible");
            Assert.That(lockedHere & (ArgonEntitlement.Connect | ArgonEntitlement.Speak | ArgonEntitlement.Stream),
                Is.EqualTo(ArgonEntitlement.None), "without Connect none of the voice rights are real");
            Assert.That(theirs.channels.Values.Select(c => c.channelId), Does.Not.Contain(hidden),
                "a channel the member cannot see is not described to them");
        });
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Managing_a_channel_needs_seeing_it(CancellationToken ct = default)
    {
        var (owner, moderator, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "staff-only", ChannelType.Text, ct);
        await GrantAsync(owner, spaceId, moderator, ArgonEntitlement.ManageChannels, ct);

        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(archetypes, spaceId, ct);
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            deny: ArgonEntitlement.ViewChannel, allow: ArgonEntitlement.None, ct);

        var duplicated = await moderator.Channels.DuplicateChannel(spaceId, channelId, ct);
        var deleted    = await moderator.Channels.DeleteChannel(spaceId, channelId, ct);
        var moved      = await moderator.Channels.MoveChannel(spaceId, channelId, null, null, null, ct);

        Assert.Multiple(() =>
        {
            Assert.That((duplicated as FailedDuplicateChannel)?.error, Is.EqualTo(DuplicateChannelError.INSUFFICIENT_PERMISSIONS));
            Assert.That(deleted, Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NO_PERMISSION)));
            Assert.That(moved, Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NO_PERMISSION)));
        });

        var visible = await CreateChannelAsync(owner, spaceId, "visible", ChannelType.Text, ct);
        Assert.That(await moderator.Channels.DuplicateChannel(spaceId, visible, ct), Is.InstanceOf<SuccessDuplicateChannel>(),
            "the same moderator manages a channel they can see");
    }

    [Test, CancelAfter(1000 * 60 * 3)]
    public async Task Members_are_told_to_refetch_when_grants_change(CancellationToken ct = default)
    {
        var (owner, member, spaceId) = await SpaceWithMemberAsync(ct);
        var channelId = await CreateChannelAsync(owner, spaceId, "signal", ChannelType.Text, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(member, ct);
        await watcher.SubscribeToSpace(spaceId, ct);

        var archetypes = ArchetypesOf(owner);
        var everyone   = await EveryoneAsync(archetypes, spaceId, ct);

        var mark = watcher.Mark();
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, channelId, everyone.id,
            deny: ArgonEntitlement.AttachFiles, allow: ArgonEntitlement.None, ct);
        var forEveryone = await watcher.WaitForAsync<EntitlementsChanged>(e => e.spaceId == spaceId, Settle, mark, ct);

        mark = watcher.Mark();
        await GrantAsync(owner, spaceId, member, ArgonEntitlement.ManageMessages, ct);
        var forOne = await watcher.WaitForAsync<EntitlementsChanged>(e => e.userId == member.UserId, Settle, mark, ct);

        var after = await member.Servers.GetMyEntitlements(spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(forEveryone.userId, Is.Null);
            Assert.That(forOne.spaceId, Is.EqualTo(spaceId));
            Assert.That(after.channels.Values.Single(c => c.channelId == channelId).entitlements.HasFlag(ArgonEntitlement.AttachFiles),
                Is.False, "the refetch reflects the overwrite");
            Assert.That(after.space.HasFlag(ArgonEntitlement.ManageMessages), Is.True, "the refetch reflects the new role");
        });
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(TestUserSession Owner, TestUserSession Member, Guid SpaceId)> SpaceWithMemberAsync(CancellationToken ct)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var created = await owner.Users.CreateSpace(new CreateServerRequest("Entitlements", "", string.Empty), ct);
        var spaceId = ((SuccessCreateSpace)created).space.spaceId;

        var code = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct).Ok();
        Assert.That(await member.Users.JoinToSpace(code, ct), Is.InstanceOf<SuccessJoin>());

        return (owner, member, spaceId);
    }

    private static async Task<Guid> CreateChannelAsync(TestUserSession owner, Guid spaceId, string name, ChannelType type,
        CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty, new CreateChannelRequest(spaceId, name, type, "", null), ct).Ok();
        var channels = await owner.Servers.GetChannels(spaceId, ct);
        return channels.Values.First(c => c.channel.name == name).channel.channelId;
    }

    private IArchetypeInteraction ArchetypesOf(TestUserSession session)
        => session.Client.ForService<IArchetypeInteraction>(FactoryAsp.Services);

    private static async Task<Archetype> EveryoneAsync(IArchetypeInteraction archetypes, Guid spaceId, CancellationToken ct)
        => (await archetypes.GetServerArchetypes(spaceId, ct)).Values.First(a => a.isDefault);

    private async Task GrantAsync(TestUserSession owner, Guid spaceId, TestUserSession member, ArgonEntitlement entitlement,
        CancellationToken ct)
    {
        var archetypes = ArchetypesOf(owner);
        var created    = await archetypes.CreateArchetype(spaceId, $"role-{Guid.NewGuid():N}"[..12], ct).Ok();
        await archetypes.UpdateArchetype(spaceId, created with { entitlement = entitlement }, ct).Ok();

        var members    = await owner.Servers.GetMembers(spaceId, ct);
        var membership = members.Values.First(m => m.member.userId == member.UserId).member.memberId;
        Assert.That(await archetypes.SetArchetypeToMember(spaceId, membership, created.id, true, ct), Is.True);
    }
}
