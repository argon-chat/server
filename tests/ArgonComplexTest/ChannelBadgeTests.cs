namespace ArgonComplexTest.Tests;

using ArgonContracts;
using ion.runtime;

/// <summary>
/// The channel-unread half of <c>GetGlobalBadges</c>, end to end.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NotificationCounterTests"/> covers the inventory, friend-request and system counters
/// but never the space badges, which is the half that reads <c>Channels.LastMessageId</c> against
/// each member's read state. That column is written by the channel grain and read by every client
/// on bootstrap, so it sits between two subsystems with no test spanning both — exactly the shape of
/// thing that survives a refactor of either side and breaks in production.
/// </para>
/// <para>
/// This is a regression guard, not a description of new behaviour: it passed before the grain
/// started coalescing that write onto its flush timer and it passes after. What changed is only how
/// long the write may take to appear, which is why the assertions poll instead of reading once.
/// </para>
/// </remarks>
[TestFixture]
public class ChannelBadgeTests : TestBase
{
    /// <summary>
    /// How long a durable last-message write is allowed to take to show up.
    /// </summary>
    /// <remarks>
    /// The grain notes the id in memory on send and writes the row from a three-second flush timer,
    /// so a badge is not expected to be correct the instant <c>SendMessage</c> returns. Generous
    /// enough to absorb a couple of missed ticks under container load without being the kind of
    /// sleep that hides a real regression — a broken write never appears, however long we wait.
    /// </remarks>
    private static readonly TimeSpan FlushWindow = TimeSpan.FromSeconds(30);

    private async Task<Guid> CreateSpaceAsync(TestUserSession owner, CancellationToken ct)
    {
        var result = await owner.Users.CreateSpace(
            new CreateServerRequest("Badge Space", "Description", string.Empty), ct);

        if (result is not SuccessCreateSpace success)
        {
            Assert.Fail($"Failed to create space: {(result as FailedCreateSpace)?.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    private async Task<Guid> CreateTextChannelAsync(TestUserSession owner, Guid spaceId, string name, CancellationToken ct)
    {
        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, name, ChannelType.Text, "Badge channel", null), ct);

        var channels = await owner.Servers.GetChannels(spaceId, ct);
        var created  = channels.Values.FirstOrDefault(c => c.channel.name == name);

        if (created is null)
        {
            Assert.Fail($"Failed to find created channel '{name}'");
            return Guid.Empty;
        }

        return created.channel.channelId;
    }

    private async Task JoinAsync(TestUserSession owner, TestUserSession member, Guid spaceId, CancellationToken ct)
    {
        var code   = await owner.Servers.CreateInviteCode(spaceId, 60, 0, ct);
        var joined = await member.Users.JoinToSpace(code, ct);

        Assert.That(joined, Is.InstanceOf<SuccessJoin>(),
            $"Member could not join: {(joined as FailedJoin)?.error}");
    }

    /// <summary>
    /// Reads the badge for one space, retrying until <paramref name="accept"/> is happy or the flush
    /// window runs out. Returns the last answer seen either way, so a failing assertion reports what
    /// the server actually said rather than a timeout with no detail.
    /// </summary>
    private static async Task<SpaceBadge?> PollSpaceBadgeAsync(
        TestUserSession session, Guid spaceId, Func<SpaceBadge?, bool> accept, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + FlushWindow;
        SpaceBadge? badge;

        while (true)
        {
            var badges = await session.Users.GetGlobalBadges(ct);
            badge = badges.spaces.Values.FirstOrDefault(x => x.spaceId == spaceId);

            if (accept(badge) || DateTimeOffset.UtcNow >= deadline)
                return badge;

            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// The badge is derived, not stored: it exists only because the channel's last-message id is
    /// ahead of the member's read state. Both halves have to be right for the count to be one — a
    /// missing last-message write reads as "nothing new", and a read state seeded on join would read
    /// the same way.
    /// </summary>
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task A_member_who_has_read_nothing_sees_one_unread_channel(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateTextChannelAsync(owner, spaceId, "badges-unread", ct);

        for (var i = 1; i <= 5; i++)
        {
            await owner.Channels.SendMessage(
                spaceId, channelId, $"Message {i}", new IonArray<IMessageEntity>([]), i, null, ct);
        }

        await JoinAsync(owner, member, spaceId, ct);

        var badge = await PollSpaceBadgeAsync(member, spaceId, x => x is not null, ct);

        Assert.That(badge, Is.Not.Null,
            "the space should carry a badge while the member has read none of its messages");

        // One, not five: the badge counts channels with something unread, not messages. The other
        // channels the space was created with carry no messages at all and are excluded by the
        // LastMessageId > 0 filter in BadgeAggregationService.
        Assert.That(badge!.unreadChannelCount, Is.EqualTo(1),
            "exactly one channel in the space has messages the member has not read");
    }

    /// <summary>
    /// The other direction, and the one a coalesced write could break on its own: acking the newest
    /// id has to clear the space entirely. If the last-message write were ever to land <em>ahead</em>
    /// of the id the client was given — a mark that rounded up, or a flush that wrote something the
    /// channel never returned — the badge would stick at one with no way for the member to clear it.
    /// </summary>
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task Acking_the_newest_message_clears_the_space_badge(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateTextChannelAsync(owner, spaceId, "badges-ack", ct);

        var lastMessageId = 0L;

        for (var i = 1; i <= 3; i++)
        {
            lastMessageId = await owner.Channels.SendMessage(
                spaceId, channelId, $"Message {i}", new IonArray<IMessageEntity>([]), i, null, ct);
        }

        await JoinAsync(owner, member, spaceId, ct);

        var before = await PollSpaceBadgeAsync(member, spaceId, x => x is not null, ct);
        Assert.That(before, Is.Not.Null, "the badge has to be there before there is anything to clear");

        await member.Users.AckChannel(channelId, lastMessageId, ct);

        // Nothing to wait for on this side: the ack writes the read state and refreshes its cache
        // before returning. A space with no unread channel left is dropped from the list entirely
        // rather than reported as zero — see BadgeAggregationService.
        var after = await PollSpaceBadgeAsync(member, spaceId, x => x is null, ct);

        Assert.That(after, Is.Null,
            $"the space should carry no badge after acking {lastMessageId}, "
          + $"but reported {after?.unreadChannelCount} unread channel(s)");
    }
}
