namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ion.runtime;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Space administration and channel/message operations — <c>SpaceGrain</c> and <c>ChannelGrain</c>
/// are the two largest grains in the server and were the two least covered.
/// </summary>
[TestFixture]
public class SpaceAndChannelTests : TestBase
{
    private static IonArray<IMessageEntity> NoEntities => new([]);

    private async Task<Guid> NewSpaceAsync(CancellationToken ct)
    {
        SetAuthToken(await RegisterAndGetTokenAsync(ct));
        return await CreateSpaceAndGetIdAsync(ct);
    }

    // ── Space metadata ──────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task UpdateSpaceInfo_PersistsNameAndDescription(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        await GetServerService(scope.ServiceProvider).UpdateSpaceInfo(spaceId, "Renamed Space", "New description", ct);

        var spaces = await GetUserService(scope.ServiceProvider).GetSpaces(ct);
        var space  = spaces.Values.First(s => s.spaceId == spaceId);

        Assert.Multiple(() =>
        {
            Assert.That(space.name, Is.EqualTo("Renamed Space"));
            Assert.That(space.description, Is.EqualTo("New description"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetSpaceStats_ReportsTheSeededChannelsAndMembers(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var stats = await GetServerService(scope.ServiceProvider).GetSpaceStats(spaceId, ct);

        Assert.That(stats, Is.Not.Null);
    }

    [Test, CancelAfter(120_000)]
    public async Task SetBoostStripHidden_IsAccepted(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        Assert.DoesNotThrowAsync(async () =>
        {
            await GetServerService(scope.ServiceProvider).SetBoostStripHidden(spaceId, true, ct);
            await GetServerService(scope.ServiceProvider).SetBoostStripHidden(spaceId, false, ct);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task GetMemberAndPrefetch_ResolveTheCreator(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);
        var me      = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var member  = await GetServerService(scope.ServiceProvider).GetMember(spaceId, me.userId, ct);
        var user    = await GetServerService(scope.ServiceProvider).PrefetchUser(spaceId, me.userId, ct);
        var profile = await GetServerService(scope.ServiceProvider).PrefetchProfile(spaceId, me.userId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(member.member.userId, Is.EqualTo(me.userId));
            Assert.That(user.userId, Is.EqualTo(me.userId));
            Assert.That(profile.userId, Is.EqualTo(me.userId));
        });
    }

    // ── Invites ─────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task InviteCode_CreateListPreviewAndRevoke(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);

        var invite = await GetServerService(scope.ServiceProvider).CreateInviteCode(spaceId, 60, 10, ct);
        Assert.That(invite.inviteCode, Is.Not.Empty);

        // CreateInviteCode returns the raw nine characters; the listing hands back the dashed
        // display form users actually copy. Compare on the canonical, separator-free value.
        var codes = await GetServerService(scope.ServiceProvider).GetInviteCodes(spaceId, ct);
        Assert.That(
            codes.invites.Values.Select(i => Argon.Entities.InviteCodeEntityData.RemoveSeparators(i.code.inviteCode)),
            Does.Contain(Argon.Entities.InviteCodeEntityData.RemoveSeparators(invite.inviteCode)));

        var preview = await GetUserService(scope.ServiceProvider).PreviewInvite(invite, ct);
        Assert.That(preview, Is.InstanceOf<SuccessPreview>());

        await GetServerService(scope.ServiceProvider).RevokeInviteCode(spaceId, invite, ct);

        var afterRevoke = await GetServerService(scope.ServiceProvider).GetInviteCodes(spaceId, ct);
        Assert.That(
            afterRevoke.invites.Values.Select(i => Argon.Entities.InviteCodeEntityData.RemoveSeparators(i.code.inviteCode)),
            Does.Not.Contain(Argon.Entities.InviteCodeEntityData.RemoveSeparators(invite.inviteCode)));
    }

    [Test, CancelAfter(120_000)]
    public async Task PreviewInvite_WithAGarbageCode_Fails(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var preview = await GetUserService(scope.ServiceProvider).PreviewInvite(new InviteCode("!!!!!!!!!"), ct);

        Assert.That(preview, Is.InstanceOf<FailedPreview>());
    }

    [Test, CancelAfter(120_000)]
    public async Task JoinToSpace_WithARevokedInvite_Fails(CancellationToken ct = default)
    {
        await using var ownerScope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId = await NewSpaceAsync(ct);
        var invite  = await GetServerService(ownerScope.ServiceProvider).CreateInviteCode(spaceId, 60, 10, ct);
        await GetServerService(ownerScope.ServiceProvider).RevokeInviteCode(spaceId, invite, ct);

        var joiner = await CreateSessionAsync(ct);
        var result = await joiner.Users.JoinToSpace(invite, ct);

        Assert.That(result, Is.InstanceOf<FailedJoin>());
    }

    // ── Channels and channel groups ─────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task CreateAndDeleteChannel(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var name      = $"temp-{Guid.NewGuid():N}"[..20];
        var channelId = await CreateTextChannelAsync(spaceId, name, ct);

        var channels = await GetServerService(scope.ServiceProvider).GetChannels(spaceId, ct);
        Assert.That(channels.Values.Select(c => c.channel.channelId), Does.Contain(channelId));

        await GetChannelService(scope.ServiceProvider).DeleteChannel(spaceId, channelId, ct);

        var afterDelete = await GetServerService(scope.ServiceProvider).GetChannels(spaceId, ct);
        Assert.That(afterDelete.Values.Select(c => c.channel.channelId), Does.Not.Contain(channelId));
    }

    [Test, CancelAfter(120_000)]
    public async Task ChannelGroup_CreateUpdateAndDelete(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"grouped-{Guid.NewGuid():N}"[..20], ct);

        var groupName = $"group-{Guid.NewGuid():N}"[..18];
        await GetChannelService(scope.ServiceProvider).CreateChannelGroup(spaceId, channelId, groupName, "a group", ct);

        var groups = await GetServerService(scope.ServiceProvider).GetChannelGroups(spaceId, ct);
        var group  = groups.Values.FirstOrDefault(g => g.name == groupName);
        Assert.That(group, Is.Not.Null);

        await GetChannelService(scope.ServiceProvider)
           .UpdateChannelGroup(spaceId, channelId, group!.groupId, "renamed-group", "updated", ct);

        var afterUpdate = await GetServerService(scope.ServiceProvider).GetChannelGroups(spaceId, ct);
        Assert.That(afterUpdate.Values.Select(g => g.name), Does.Contain("renamed-group"));

        // deleteChannels: false must keep the channels and only drop the grouping.
        await GetChannelService(scope.ServiceProvider)
           .DeleteChannelGroup(spaceId, channelId, group.groupId, deleteChannels: false, ct);

        var afterDelete = await GetServerService(scope.ServiceProvider).GetChannelGroups(spaceId, ct);
        Assert.Multiple(() =>
        {
            Assert.That(afterDelete.Values.Select(g => g.groupId), Does.Not.Contain(group.groupId));
            Assert.That(
                GetServerService(scope.ServiceProvider).GetChannels(spaceId, ct).Result.Values.Select(c => c.channel.channelId),
                Does.Contain(channelId));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task MoveChannel_IntoAGroup(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"movable-{Guid.NewGuid():N}"[..20], ct);

        var groupName = $"target-{Guid.NewGuid():N}"[..18];
        await GetChannelService(scope.ServiceProvider).CreateChannelGroup(spaceId, channelId, groupName, null, ct);

        var group = (await GetServerService(scope.ServiceProvider).GetChannelGroups(spaceId, ct))
           .Values.First(g => g.name == groupName);

        await GetChannelService(scope.ServiceProvider).MoveChannel(spaceId, channelId, group.groupId, null, null, ct);

        var channels = await GetServerService(scope.ServiceProvider).GetChannels(spaceId, ct);
        var moved    = channels.Values.First(c => c.channel.channelId == channelId);

        Assert.That(moved.channel.groupId, Is.EqualTo(group.groupId));
    }

    // ── Messages ────────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task SendMessage_ThenQueryItBack(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"msgs-{Guid.NewGuid():N}"[..20], ct);

        var messageId = await GetChannelService(scope.ServiceProvider)
           .SendMessage(spaceId, channelId, "hello channel", NoEntities, Random.Shared.NextInt64(), null, ct);

        Assert.That(messageId, Is.GreaterThan(0));

        var history = await GetChannelService(scope.ServiceProvider).QueryMessages(spaceId, channelId, null, 50, ct);

        Assert.That(history.Values.Select(m => m.text), Does.Contain("hello channel"));
    }

    [Test, CancelAfter(120_000)]
    public async Task SendMessageWithReadback_ReturnsTheStoredMessage(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"readback-{Guid.NewGuid():N}"[..20], ct);

        var randomId = Random.Shared.NextInt64();

        var readback = await GetChannelService(scope.ServiceProvider)
           .SendMessageWithReadback(spaceId, channelId, "readback please", NoEntities, randomId, null, ct);

        // The readback echoes the client's idempotency key back with the server-assigned id, which
        // is what lets a client reconcile an optimistic local message with the stored one.
        Assert.Multiple(() =>
        {
            Assert.That(readback.randomId, Is.EqualTo(randomId));
            Assert.That(readback.channelId, Is.EqualTo(channelId));
            Assert.That(readback.messageId, Is.GreaterThan(0));
        });

        var history = await GetChannelService(scope.ServiceProvider).QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(history.Values.First(m => m.messageId == readback.messageId).text, Is.EqualTo("readback please"));
    }

    [Test, CancelAfter(120_000)]
    public async Task SendMessage_AsAReply_LinksToTheOriginal(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"replies-{Guid.NewGuid():N}"[..20], ct);

        var original = await GetChannelService(scope.ServiceProvider)
           .SendMessage(spaceId, channelId, "original", NoEntities, Random.Shared.NextInt64(), null, ct);

        var reply = await GetChannelService(scope.ServiceProvider)
           .SendMessage(spaceId, channelId, "the reply", NoEntities, Random.Shared.NextInt64(), original, ct);

        var history = await GetChannelService(scope.ServiceProvider).QueryMessages(spaceId, channelId, null, 50, ct);
        var stored  = history.Values.First(m => m.messageId == reply);

        Assert.That(stored.replyId, Is.EqualTo(original));
    }

    [Test, CancelAfter(120_000)]
    public async Task SendMessage_WithTheSameRandomId_IsDeduplicated(CancellationToken ct = default)
    {
        // randomId is the client's idempotency key: a retried send after a flaky connection must not
        // post the message twice.
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"dedupe-{Guid.NewGuid():N}"[..20], ct);

        var randomId = Random.Shared.NextInt64();

        var first  = await GetChannelService(scope.ServiceProvider)
           .SendMessage(spaceId, channelId, "only once", NoEntities, randomId, null, ct);
        var second = await GetChannelService(scope.ServiceProvider)
           .SendMessage(spaceId, channelId, "only once", NoEntities, randomId, null, ct);

        Assert.That(second, Is.EqualTo(first));

        var history = await GetChannelService(scope.ServiceProvider).QueryMessages(spaceId, channelId, null, 50, ct);
        Assert.That(history.Values.Count(m => m.text == "only once"), Is.EqualTo(1));
    }

    [Test, CancelAfter(120_000)]
    public async Task QueryMessages_PagesBackwardsFromACursor(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"paging-{Guid.NewGuid():N}"[..20], ct);

        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await GetChannelService(scope.ServiceProvider)
               .SendMessage(spaceId, channelId, $"message {i}", NoEntities, Random.Shared.NextInt64(), null, ct));
        }

        var page = await GetChannelService(scope.ServiceProvider).QueryMessages(spaceId, channelId, ids[2], 10, ct);

        Assert.That(page.Values.Select(m => m.messageId), Has.None.GreaterThanOrEqualTo(ids[2]));
    }

    [Test, CancelAfter(120_000)]
    public async Task QueryMessages_OnAnEmptyChannel_IsEmpty(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"silent-{Guid.NewGuid():N}"[..20], ct);

        var history = await GetChannelService(scope.ServiceProvider).QueryMessages(spaceId, channelId, null, 50, ct);

        Assert.That(history.Size, Is.EqualTo(0));
    }

    // ── Reactions ───────────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task Reaction_AddThenRemove(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"reactions-{Guid.NewGuid():N}"[..20], ct);
        var me        = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var messageId = await GetChannelService(scope.ServiceProvider)
           .SendMessage(spaceId, channelId, "react to me", NoEntities, Random.Shared.NextInt64(), null, ct);

        var added = await GetChannelService(scope.ServiceProvider).AddReaction(spaceId, channelId, messageId, "👍", ct);
        Assert.That(added, Is.InstanceOf<SuccessAddReaction>());

        var batch = await GetChannelService(scope.ServiceProvider)
           .BatchGetReactions(spaceId, channelId, new IonArray<long>([messageId]), ct);

        var reactions = batch.Values.First(e => e.messageId == messageId).reactions;
        Assert.Multiple(() =>
        {
            Assert.That(reactions.Values.Select(r => r.emoji), Does.Contain("👍"));
            Assert.That(reactions.Values.First(r => r.emoji == "👍").userIds.Values, Does.Contain(me.userId));
        });

        var removed = await GetChannelService(scope.ServiceProvider).RemoveReaction(spaceId, channelId, messageId, "👍", ct);
        Assert.That(removed, Is.InstanceOf<SuccessRemoveReaction>());

        var afterRemoval = await GetChannelService(scope.ServiceProvider)
           .BatchGetReactions(spaceId, channelId, new IonArray<long>([messageId]), ct);

        Assert.That(
            afterRemoval.Values.First(e => e.messageId == messageId).reactions.Values.Select(r => r.emoji),
            Does.Not.Contain("👍"));
    }

    [Test, CancelAfter(120_000)]
    public async Task BatchGetReactions_ForMessagesWithNone_ReturnsEmptySets(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var spaceId   = await NewSpaceAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, $"noreact-{Guid.NewGuid():N}"[..20], ct);

        var messageId = await GetChannelService(scope.ServiceProvider)
           .SendMessage(spaceId, channelId, "plain", NoEntities, Random.Shared.NextInt64(), null, ct);

        var batch = await GetChannelService(scope.ServiceProvider)
           .BatchGetReactions(spaceId, channelId, new IonArray<long>([messageId]), ct);

        Assert.That(batch.Values.SelectMany(e => e.reactions.Values), Is.Empty);
    }
}
