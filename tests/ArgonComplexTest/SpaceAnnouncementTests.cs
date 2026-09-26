namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using static ChannelTestKit;

/// <summary>
/// The space's main announcement channel: set and cleared by whoever holds ManageServer, only ever an
/// announcement channel of the same space, dropped when that channel is deleted or turned back into
/// a text channel, and carried to members on the space itself.
/// </summary>
[TestFixture]
public class SpaceAnnouncementTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private long _randomId = Random.Shared.Next(1, int.MaxValue);
    private long NextRandomId() => Interlocked.Increment(ref _randomId);

    private static ISpaceAnnouncementInteraction AnnouncementsOf(TestUserSession session)
        => session.Client.ForService<ISpaceAnnouncementInteraction>(Services);

    private static async Task<Guid?> MainOfAsync(TestUserSession member, Guid spaceId, CancellationToken ct)
        => (await member.Users.GetSpaces(ct)).Values.Single(s => s.spaceId == spaceId).mainAnnouncementChannelId;

    private async Task<(TestUserSession Owner, TestUserSession Member, Guid SpaceId, Guid NewsId)> SpaceWithNewsAsync(CancellationToken ct)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, ct);
        var newsId  = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);
        await JoinAsync(owner, member, spaceId, ct);

        return (owner, member, spaceId, newsId);
    }

    private static async Task SetAsync(TestUserSession owner, Guid spaceId, Guid? channelId, CancellationToken ct)
        => Assert.That(await AnnouncementsOf(owner).SetMainAnnouncementChannel(spaceId, channelId, ct),
            Is.InstanceOf<SuccessSetMainAnnouncementChannel>());

    [Test, CancelAfter(120_000)]
    public async Task The_owner_sets_and_clears_it_and_members_are_told(CancellationToken ct = default)
    {
        var (owner, member, spaceId, newsId) = await SpaceWithNewsAsync(ct);

        await using var watcher = await RealtimeClient.ConnectAsync(member, ct);
        await watcher.SubscribeToSpace(spaceId, ct);

        var mark   = watcher.Mark();
        var result = await AnnouncementsOf(owner).SetMainAnnouncementChannel(spaceId, newsId, ct);
        var set    = await watcher.WaitForAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, EventWait, mark, ct);
        var seen   = await MainOfAsync(member, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That((result as SuccessSetMainAnnouncementChannel)?.channelId, Is.EqualTo(newsId));
            Assert.That(set.details.mainAnnouncementChannelId, Is.EqualTo(newsId));
            Assert.That(seen, Is.EqualTo(newsId));
        });

        mark = watcher.Mark();
        await SetAsync(owner, spaceId, null, ct);
        var cleared = await watcher.WaitForAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, EventWait, mark, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(cleared.details.mainAnnouncementChannelId, Is.Null);
            Assert.That(await MainOfAsync(member, spaceId, ct), Is.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Setting_the_same_channel_again_announces_nothing(CancellationToken ct = default)
    {
        var (owner, member, spaceId, newsId) = await SpaceWithNewsAsync(ct);
        await SetAsync(owner, spaceId, newsId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(member, ct);
        await watcher.SubscribeToSpace(spaceId, ct);

        var mark = watcher.Mark();
        await SetAsync(owner, spaceId, newsId, ct);

        await watcher.AssertNoneWithinAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, TimeSpan.FromSeconds(1),
            "a call that changed nothing announced a change", mark, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task It_needs_ManageServer(CancellationToken ct = default)
    {
        var (owner, member, spaceId, newsId) = await SpaceWithNewsAsync(ct);

        var refused = await AnnouncementsOf(member).SetMainAnnouncementChannel(spaceId, newsId, ct);

        Assert.Multiple(async () =>
        {
            Assert.That((refused as FailedSetMainAnnouncementChannel)?.error, Is.EqualTo(SetMainAnnouncementChannelError.NO_PERMISSION));
            Assert.That(await MainOfAsync(owner, spaceId, ct), Is.Null, "a member without ManageServer changed it");
        });

        await SetAsync(owner, spaceId, newsId, ct);
        Assert.That((await AnnouncementsOf(member).SetMainAnnouncementChannel(spaceId, null, ct) as FailedSetMainAnnouncementChannel)?.error,
            Is.EqualTo(SetMainAnnouncementChannelError.NO_PERMISSION), "a member without ManageServer cleared it");

        await SpaceGroupSupport.GrantAsync(owner, spaceId, member.UserId, ArgonEntitlement.ManageServer, ct);
        await SetAsync(member, spaceId, null, ct);

        Assert.That(await MainOfAsync(owner, spaceId, ct), Is.Null, "a member granted ManageServer could not clear it");
    }

    [Test, CancelAfter(120_000)]
    public async Task Only_an_announcement_channel_of_the_same_space_is_taken(CancellationToken ct = default)
    {
        var (owner, _, spaceId, newsId) = await SpaceWithNewsAsync(ct);

        var textId       = await CreateChannelAsync(owner, spaceId, "general", ChannelType.Text, ct);
        var voiceId      = await CreateChannelAsync(owner, spaceId, "lounge", ChannelType.Voice, ct);
        var otherSpaceId = await CreateSpaceAsync(owner, ct);
        var foreignNews  = await CreateChannelAsync(owner, otherSpaceId, "their-news", ChannelType.Announcement, ct);

        await SetAsync(owner, spaceId, newsId, ct);

        var api = AnnouncementsOf(owner);
        var text    = await api.SetMainAnnouncementChannel(spaceId, textId, ct);
        var voice   = await api.SetMainAnnouncementChannel(spaceId, voiceId, ct);
        var foreign = await api.SetMainAnnouncementChannel(spaceId, foreignNews, ct);
        var missing = await api.SetMainAnnouncementChannel(spaceId, Guid.NewGuid(), ct);

        Assert.Multiple(async () =>
        {
            Assert.That((text as FailedSetMainAnnouncementChannel)?.error, Is.EqualTo(SetMainAnnouncementChannelError.NOT_ANNOUNCEMENT_CHANNEL));
            Assert.That((voice as FailedSetMainAnnouncementChannel)?.error, Is.EqualTo(SetMainAnnouncementChannelError.NOT_ANNOUNCEMENT_CHANNEL));
            Assert.That((foreign as FailedSetMainAnnouncementChannel)?.error, Is.EqualTo(SetMainAnnouncementChannelError.CHANNEL_NOT_FOUND));
            Assert.That((missing as FailedSetMainAnnouncementChannel)?.error, Is.EqualTo(SetMainAnnouncementChannelError.CHANNEL_NOT_FOUND));
            Assert.That(await MainOfAsync(owner, spaceId, ct), Is.EqualTo(newsId), "a refused call still changed it");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Deleting_the_channel_clears_it_and_deleting_another_does_not(CancellationToken ct = default)
    {
        var (owner, member, spaceId, newsId) = await SpaceWithNewsAsync(ct);
        var otherNews = await CreateChannelAsync(owner, spaceId, "patch-notes", ChannelType.Announcement, ct);
        await SetAsync(owner, spaceId, newsId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(member, ct);
        await watcher.SubscribeToSpace(spaceId, ct);

        var mark = watcher.Mark();
        await owner.Channels.DeleteChannel(spaceId, otherNews, ct);
        await watcher.AssertNoneWithinAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, TimeSpan.FromSeconds(1),
            "deleting a channel that was not the main one announced a change", mark, ct);
        Assert.That(await MainOfAsync(member, spaceId, ct), Is.EqualTo(newsId));

        mark = watcher.Mark();
        await owner.Channels.DeleteChannel(spaceId, newsId, ct);
        var cleared = await watcher.WaitForAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, EventWait, mark, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(cleared.details.mainAnnouncementChannelId, Is.Null);
            Assert.That(await MainOfAsync(member, spaceId, ct), Is.Null);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Deleting_its_group_with_the_channels_clears_it(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);

        var groupId = await SpaceGroupSupport.CreateGroupAsync(owner, spaceId, "info", ct);
        var newsId  = await SpaceGroupSupport.CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, groupId, ct);

        await SetAsync(owner, spaceId, newsId, ct);
        await owner.Channels.DeleteChannelGroup(spaceId, Guid.Empty, groupId, deleteChannels: true, ct);

        Assert.That(await MainOfAsync(owner, spaceId, ct), Is.Null);
    }

    [Test, CancelAfter(120_000)]
    public async Task Converting_it_to_text_clears_it_and_members_are_told(CancellationToken ct = default)
    {
        var (owner, member, spaceId, newsId) = await SpaceWithNewsAsync(ct);
        await SetAsync(owner, spaceId, newsId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(member, ct);
        await watcher.SubscribeToSpace(spaceId, ct);

        var mark = watcher.Mark();
        Assert.That(await owner.Channels.SetChannelType(spaceId, newsId, ChannelType.Text, ct), Is.InstanceOf<SuccessUpdateChannel>());

        var cleared = await watcher.WaitForAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, EventWait, mark, ct);

        Assert.Multiple(async () =>
        {
            Assert.That(cleared.details.mainAnnouncementChannelId, Is.Null);
            Assert.That(await MainOfAsync(member, spaceId, ct), Is.Null);
        });

        // Back to an announcement channel it is eligible again, but nothing picks it for the owner.
        await owner.Channels.SetChannelType(spaceId, newsId, ChannelType.Announcement, ct);
        Assert.That(await MainOfAsync(member, spaceId, ct), Is.Null);
    }

    [Test, CancelAfter(120_000)]
    public async Task Converting_another_channel_to_text_leaves_it(CancellationToken ct = default)
    {
        var (owner, member, spaceId, newsId) = await SpaceWithNewsAsync(ct);
        var otherNews = await CreateChannelAsync(owner, spaceId, "patch-notes", ChannelType.Announcement, ct);
        await SetAsync(owner, spaceId, newsId, ct);

        await using var watcher = await RealtimeClient.ConnectAsync(member, ct);
        await watcher.SubscribeToSpace(spaceId, ct);

        var mark = watcher.Mark();
        await owner.Channels.SetChannelType(spaceId, otherNews, ChannelType.Text, ct);

        await watcher.AssertNoneWithinAsync<SpaceDetailsUpdated>(e => e.spaceId == spaceId, TimeSpan.FromSeconds(2),
            "converting a channel that was not the main one announced a change", mark, ct);
        Assert.That(await MainOfAsync(member, spaceId, ct), Is.EqualTo(newsId));
    }

    /// <summary>
    /// What the client's banner stands on for somebody who just joined: the space they are handed
    /// names the channel, and joining leaves them no read state for it — so its latest post is unread.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task A_new_member_gets_it_with_the_space_and_nothing_read(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var joiner  = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, ct);
        var newsId  = await CreateChannelAsync(owner, spaceId, "news", ChannelType.Announcement, ct);

        await SetAsync(owner, spaceId, newsId, ct);
        await owner.Channels.SendMessage(spaceId, newsId, "Welcome aboard", new IonArray<IMessageEntity>([]), NextRandomId(), null, ct);

        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await joiner.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>());

        var badges = await joiner.Users.GetGlobalBadges(ct);

        Assert.Multiple(async () =>
        {
            Assert.That(((SuccessJoin)joined).space.mainAnnouncementChannelId, Is.EqualTo(newsId));
            Assert.That(await MainOfAsync(joiner, spaceId, ct), Is.EqualTo(newsId));
            Assert.That(badges.readStates.Values.Any(r => r.channelId == newsId && r.lastReadMessageId > 0), Is.False,
                "joining marked the announcement channel read");
        });
    }
}
