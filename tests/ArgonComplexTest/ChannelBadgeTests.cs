namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Cache;
using Argon.Services;
using ArgonContracts;
using ion.runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

/// <summary>
/// The channel-unread half of <c>GetGlobalBadges</c>, end to end.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NotificationCounterTests"/> covers the inventory, friend-request and system counters
/// but never the space badges, which is the half that reads a channel's high-water mark against each
/// member's read state. That mark is written by the channel grain and read by every client on
/// bootstrap, so it sits between two subsystems with no test spanning both — exactly the shape of
/// thing that survives a refactor of either side and breaks in production.
/// </para>
/// <para>
/// This is a regression guard, not a description of new behaviour: it passed when the mark was a
/// column on <c>Channels</c> written once per message, it passed when that write was coalesced onto
/// a flush timer, and it passes now that the mark lives in <c>ChannelLastMessages</c>. What has
/// changed each time is only how long the write takes to appear and where it lands, which is why the
/// assertions poll instead of reading once.
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
        // channels the space was created with have no mark at all — nobody has posted in them, so
        // they have no row in ChannelLastMessages — which reads as zero and loses to a read state of
        // zero. That is the case BadgeAggregationService must not answer by dropping the channel:
        // they have to reach the comparison and lose it, not be filtered out before it.
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

    /// <summary>
    /// The badge survives the Redis cell being gone, which is the only way to prove it came from the
    /// side table.
    /// </summary>
    /// <remarks>
    /// <para>The two tests above are green whether the mark is read from <c>ChannelLastMessages</c>,
    /// from the Redis cell, or — before this change — from <c>Channels.LastMessageId</c>: every source
    /// carries the right number a second after the send, so the answer is right for three different
    /// reasons and the test cannot tell which one it got. Deleting the cell removes two of them and
    /// leaves exactly one path that can still produce a badge.</para>
    ///
    /// <para>So it fails from either side of the split. If a reader went back to the channel row, the
    /// badge disappears — that column is zero for every channel created since. If the side table were
    /// never written, it disappears too. The assertion in the middle covers the remaining direction:
    /// <c>Channels.LastMessageId</c> stays at zero, which is what makes the table's
    /// <c>PlacementGlobal</c> declaration honest. A writer that starts touching it again fails here
    /// with the value it wrote.</para>
    ///
    /// <para>The cell is deleted rather than the test being written against a channel that never had
    /// one: a cell is written on every send and the point is to check the fallback under a mark that
    /// really is set. Nothing rewrites it afterwards — the grain only publishes on send, and the flush
    /// timer writes the row.</para>
    /// </remarks>
    [Test, CancelAfter(1000 * 60 * 5)]
    public async Task The_badge_still_stands_when_the_cache_cell_is_gone(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId   = await CreateSpaceAsync(owner, ct);
        var channelId = await CreateTextChannelAsync(owner, spaceId, "badges-durable", ct);

        var lastMessageId = 0L;

        for (var i = 1; i <= 3; i++)
        {
            lastMessageId = await owner.Channels.SendMessage(
                spaceId, channelId, $"Message {i}", new IonArray<IMessageEntity>([]), i, null, ct);
        }

        await JoinAsync(owner, member, spaceId, ct);

        // The flush is on a timer, so the row is not there the instant SendMessage returns. Waiting
        // for it is what makes the deletion below a test of the fallback rather than a race with it.
        var stored = await PollStoredMarkAsync(channelId, mark => mark == lastMessageId, ct);

        Assert.That(stored, Is.EqualTo(lastMessageId),
            "the flush never reached ChannelLastMessages, so there is no durable mark to fall back to");

        Assert.That(await StoredChannelColumnAsync(channelId, ct), Is.Zero,
            "somebody is writing Channels.LastMessageId again — that column is why the table was "
          + "demoted to regional, and ArgonTablePlacement now declares it global on the promise that "
          + "nothing writes it");

        await DeleteHighWaterCellAsync(channelId);

        var badge = await PollSpaceBadgeAsync(member, spaceId, x => x is not null, ct);

        Assert.That(badge, Is.Not.Null,
            "with the cell gone the badge can only come from ChannelLastMessages, and it did not");

        Assert.That(badge!.unreadChannelCount, Is.EqualTo(1),
            "exactly one channel in the space has messages the member has not read");
    }

    /// <summary>The durable mark for a channel, polled until <paramref name="accept"/> is happy.</summary>
    private async Task<long> PollStoredMarkAsync(Guid channelId, Func<long, bool> accept, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + FlushWindow;
        var factory  = FactoryAsp.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();

        while (true)
        {
            await using (var db = await factory.CreateDbContextAsync(ct))
            {
                var mark = await db.ChannelLastMessages
                   .AsNoTracking()
                   .Where(m => m.ChannelId == channelId)
                   .Select(m => m.LastMessageId)
                   .FirstOrDefaultAsync(ct);

                if (accept(mark) || DateTimeOffset.UtcNow >= deadline)
                    return mark;
            }

            await Task.Delay(250, ct);
        }
    }

    /// <summary>What the retired column on the channel row says, which must be nothing.</summary>
    private async Task<long> StoredChannelColumnAsync(Guid channelId, CancellationToken ct)
    {
        await using var db = await FactoryAsp.Services
           .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
           .CreateDbContextAsync(ct);

        return await db.Channels
           .AsNoTracking()
           .Where(c => c.Id == channelId)
           .Select(c => c.LastMessageId)
           .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Removes the per-send cache copy, so the next read has only the durable one to work with.
    /// </summary>
    /// <remarks>
    /// <para>Through <see cref="ChannelHighWaterCell"/> and the pool's default database, exactly as
    /// both the writer and the readers do. Spelling the key by hand here would let this delete
    /// nothing and still pass, which is the failure mode that type exists to prevent.</para>
    ///
    /// <para>It waits for the cell before removing it, and insists it was really there. The grain
    /// publishes off the send path — deliberately, so a Redis round trip is not on the message
    /// latency — so a cell that has not landed yet is a timing artefact, while a cell that never
    /// lands is the write side of the freshness contract being broken. Deleting nothing and carrying
    /// on would turn the second case into a green run.</para>
    /// </remarks>
    private async Task DeleteHighWaterCellAsync(Guid channelId)
    {
        var pool = FactoryAsp.Services.GetRequiredKeyedService<IRedisPoolConnections>(RedisProfiles.Cache);
        var key  = ChannelHighWaterCell.KeyFor(channelId);

        var deadline = DateTimeOffset.UtcNow + FlushWindow;
        var deleted  = false;

        while (!deleted && DateTimeOffset.UtcNow < deadline)
        {
            await using (var scope = pool.Rent())
                deleted = await scope.GetDatabase().KeyDeleteAsync(key);

            if (!deleted)
                await Task.Delay(250);
        }

        Assert.That(deleted, Is.True,
            $"nothing was ever written to '{key}', so the fallback below would be proved by accident "
          + "rather than on purpose — the grain is expected to write the cell on every send");
    }
}
