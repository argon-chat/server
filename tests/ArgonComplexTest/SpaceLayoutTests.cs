namespace ArgonComplexTest.Tests;

using ArgonComplexTest.Infrastructure.Presence;
using ArgonContracts;
using ion.runtime;
using ion.runtime.client;
using Microsoft.EntityFrameworkCore;
using static SpaceGroupSupport;

/// <summary>
/// The shape of a space — its channels, the groups they are filed under, and the order of both —
/// as <c>SpaceGrain</c> writes it.
/// </summary>
/// <remarks>
/// <para>Order is a fractional index per row rather than a position, so a move writes one row. That
/// makes the interesting cases the edges: the top of a list, neighbours given in the wrong order, a
/// neighbour that is the smallest index there is, and an index that has grown long from being
/// squeezed between the same two rows over and over. Each of those has its own branch in the grain
/// and each is pinned here against what a member actually reads back.</para>
///
/// <para>The rest is who may do it, and to which space: every write here is gated on
/// <c>ManageChannels</c> in the space the grain is keyed by, so a test that names one space and
/// addresses another is asking whether the gate and the write agree about which space that is.</para>
/// </remarks>
[TestFixture]
public class SpaceLayoutTests : TestBase
{
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(10);

    private static List<string> Names(IEnumerable<ArgonChannel> channels, params Guid[] only)
        => channels.Where(c => only.Contains(c.channelId)).Select(c => c.name).ToList();

    private static List<Guid> Order(IEnumerable<ArgonChannel> channels, params Guid[] only)
        => channels.Where(c => only.Contains(c.channelId)).Select(c => c.channelId).ToList();

    private static List<Guid> GroupOrder(IEnumerable<ChannelGroup> groups, params Guid[] only)
        => groups.Where(g => only.Contains(g.groupId)).Select(g => g.groupId).ToList();

    // ── Creating channels ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A channel is created in the space the call is addressed to, whatever the request body says.
    /// </summary>
    /// <remarks>
    /// <c>CreateChannelRequest</c> carries a <c>spaceId</c> of its own beside the one the service is
    /// addressed with, and <c>ChannelInteractionImpl.CreateChannel</c> used to key the grain off the
    /// body. The permission check followed the body too, so it was never a way into somebody else's
    /// space — but the addressed space and the written one could differ, which is the one thing every
    /// other method on the service is careful never to allow.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task CreateChannel_GoesToTheSpaceTheCallIsAddressedTo(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var alicesSpace = await CreateSpaceAsync(alice, "Alice", ct);
        var bobsSpace   = await CreateSpaceAsync(bob, "Bob", ct);

        var intoOwn = Unique("own");
        await bob.Channels.CreateChannel(bobsSpace, Guid.Empty,
            new CreateChannelRequest(alicesSpace, intoOwn, ChannelType.Text, "", null), ct).Ok();

        var intoForeign = Unique("foreign");
        Assert.That(await bob.Channels.CreateChannel(alicesSpace, Guid.Empty,
                new CreateChannelRequest(bobsSpace, intoForeign, ChannelType.Text, "", null), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NO_PERMISSION)));

        var bobs   = (await ChannelsAsync(bob, bobsSpace, ct)).Select(c => c.name).ToList();
        var alices = (await ChannelsAsync(alice, alicesSpace, ct)).Select(c => c.name).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(bobs, Does.Contain(intoOwn), "the channel did not land in the space the call was addressed to");
            Assert.That(alices, Does.Not.Contain(intoOwn));
            Assert.That(alices, Does.Not.Contain(intoForeign), "a space the caller cannot manage was written to");
            Assert.That(bobs, Does.Not.Contain(intoForeign), "a refused call still created a channel somewhere");
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [CancelAfter(120_000)]
    public async Task CreateChannel_WithoutAName_IsRefused(string name, CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Blank names", ct);
        var before  = await ChannelsAsync(owner, spaceId, ct);

        Assert.That(await owner.Channels.CreateChannel(spaceId, Guid.Empty,
                new CreateChannelRequest(spaceId, name, ChannelType.Text, "", null), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)));

        Assert.That(await ChannelsAsync(owner, spaceId, ct), Has.Count.EqualTo(before.Count),
            "a channel with no name was created");
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateChannel_WithANameLongerThanTheColumn_IsRefused(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Long names", ct);

        Assert.That(await owner.Channels.CreateChannel(spaceId, Guid.Empty,
                new CreateChannelRequest(spaceId, new string('n', 129), ChannelType.Text, "", null), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)));
        Assert.That(await owner.Channels.CreateChannel(spaceId, Guid.Empty,
                new CreateChannelRequest(spaceId, "fine", ChannelType.Text, new string('d', 1025), null), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)));

        Assert.That(await ChannelsAsync(owner, spaceId, ct), Is.Empty);
    }

    /// <summary>A kind the client has no way to draw is not a channel.</summary>
    [Test, CancelAfter(120_000)]
    public async Task CreateChannel_OfAKindThatDoesNotExist_IsRefused(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Unknown kinds", ct);

        Assert.That(await owner.Channels.CreateChannel(spaceId, Guid.Empty,
                new CreateChannelRequest(spaceId, Unique("odd"), (ChannelType)42, "", null), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)));

        Assert.That(await ChannelsAsync(owner, spaceId, ct), Is.Empty, "a channel of an undefined kind was stored");
    }

    /// <summary>The same rule the settings sheet applies when a channel is renamed.</summary>
    [Test, CancelAfter(120_000)]
    public async Task CreateChannel_TrimsTheName(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Trimmed", ct);
        var name    = Unique("padded");

        await owner.Channels.CreateChannel(spaceId, Guid.Empty,
            new CreateChannelRequest(spaceId, $"  {name}  ", ChannelType.Text, "", null), ct).Ok();

        Assert.That((await ChannelsAsync(owner, spaceId, ct)).Select(c => c.name), Is.EqualTo(new[] { name }));
    }

    [Test, CancelAfter(120_000)]
    public async Task CreateChannel_IntoAGroup_AppendsItToThatGroup(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Grouped", ct);

        var loose   = await CreateChannelAsync(owner, spaceId, Unique("loose"), ct: ct);
        var groupId = await CreateGroupAsync(owner, spaceId, Unique("group"), ct);
        var first   = await CreateChannelAsync(owner, spaceId, Unique("first"), groupId: groupId, ct: ct);
        var second  = await CreateChannelAsync(owner, spaceId, Unique("second"), ChannelType.Voice, groupId, ct);

        var channels = await ChannelsAsync(owner, spaceId, ct);
        var grouped  = channels.Where(c => c.groupId == groupId).Select(c => c.channelId).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(grouped, Is.EqualTo(new[] { first, second }));
            Assert.That(channels.Single(c => c.channelId == loose).groupId, Is.Null);
            Assert.That(channels.Single(c => c.channelId == second).type, Is.EqualTo(ChannelType.Voice));
        });
    }

    /// <summary>
    /// A channel cannot be filed under a group that belongs to a different space.
    /// </summary>
    /// <remarks>
    /// The foreign key is satisfied by any group row at all, so nothing but the grain can refuse this.
    /// A channel filed that way is drawn under no group its own space has, and it hangs off the other
    /// space's group: when that space deletes the group with its channels, the include that finds
    /// "its" channels finds this one too.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task CreateChannel_IntoAGroupOfAnotherSpace_IsRefused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var alicesSpace = await CreateSpaceAsync(alice, "Alice", ct);
        var bobsSpace   = await CreateSpaceAsync(bob, "Bob", ct);
        var bobsGroup   = await CreateGroupAsync(bob, bobsSpace, Unique("bobs"), ct);

        Assert.That(await alice.Channels.CreateChannel(alicesSpace, Guid.Empty,
                new CreateChannelRequest(alicesSpace, Unique("stray"), ChannelType.Text, "", bobsGroup), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NOT_FOUND)));
        Assert.That(await alice.Channels.CreateChannel(alicesSpace, Guid.Empty,
                new CreateChannelRequest(alicesSpace, Unique("nowhere"), ChannelType.Text, "", Guid.NewGuid()), ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NOT_FOUND)));

        Assert.That(await ChannelsAsync(alice, alicesSpace, ct), Is.Empty);
    }

    // ── Who may arrange a space ─────────────────────────────────────────────────────────────────

    [Test, CancelAfter(180_000)]
    public async Task A_member_without_ManageChannels_can_change_nothing_about_the_layout(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Guarded", ct);
        var channel = await CreateChannelAsync(owner, spaceId, Unique("kept"), ct: ct);
        var other   = await CreateChannelAsync(owner, spaceId, Unique("other"), ct: ct);
        var groupId = await CreateGroupAsync(owner, spaceId, Unique("kept"), ct);
        await JoinAsync(owner, member, spaceId, ct);

        var channelsBefore = await ChannelsAsync(owner, spaceId, ct);
        var groupsBefore   = await GroupsAsync(owner, spaceId, ct);

        var ch = member.Channels;

        var refused = new FailedChannelLayout(ChannelLayoutError.NO_PERMISSION);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await ch.CreateChannel(spaceId, Guid.Empty,
                new CreateChannelRequest(spaceId, Unique("sneaky"), ChannelType.Text, "", null), ct), Is.EqualTo(refused), "CreateChannel");
            Assert.That(await ch.CreateChannelGroup(spaceId, Guid.Empty, "sneaky", null, ct), Is.EqualTo(refused), "CreateChannelGroup");
            Assert.That(await ch.UpdateChannelGroup(spaceId, Guid.Empty, groupId, "renamed", null, ct), Is.EqualTo(refused), "UpdateChannelGroup");
            Assert.That(await ch.MoveChannelGroup(spaceId, groupId, null, null, ct), Is.EqualTo(refused), "MoveChannelGroup");
            Assert.That(await ch.DeleteChannelGroup(spaceId, Guid.Empty, groupId, true, ct), Is.EqualTo(refused), "DeleteChannelGroup");
            Assert.That(await ch.MoveChannel(spaceId, other, null, null, channel, ct), Is.EqualTo(refused), "MoveChannel");
            Assert.That(await ch.DeleteChannel(spaceId, channel, ct), Is.EqualTo(refused), "DeleteChannel");
        });

        var duplicate = await ch.DuplicateChannel(spaceId, channel, ct);
        Assert.That((duplicate as FailedDuplicateChannel)?.error, Is.EqualTo(DuplicateChannelError.INSUFFICIENT_PERMISSIONS));

        var channelsAfter = await ChannelsAsync(owner, spaceId, ct);
        var groupsAfter   = await GroupsAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(channelsAfter.Select(c => (c.channelId, c.name, c.groupId, c.fractionalIndex)),
                Is.EqualTo(channelsBefore.Select(c => (c.channelId, c.name, c.groupId, c.fractionalIndex))));
            Assert.That(groupsAfter.Select(g => (g.groupId, g.name, g.fractionalIndex)),
                Is.EqualTo(groupsBefore.Select(g => (g.groupId, g.name, g.fractionalIndex))));
        });
    }

    [Test, CancelAfter(180_000)]
    public async Task A_member_granted_ManageChannels_can_arrange_the_space(CancellationToken ct = default)
    {
        var owner  = await CreateSessionAsync(ct);
        var member = await CreateSessionAsync(ct);

        var spaceId = await CreateSpaceAsync(owner, "Delegated", ct);
        await JoinAsync(owner, member, spaceId, ct);
        await GrantAsync(owner, spaceId, member.UserId, ArgonEntitlement.ViewChannel | ArgonEntitlement.ManageChannels, ct);

        var groupId = await CreateGroupAsync(member, spaceId, Unique("by-member"), ct);
        var channel = await CreateChannelAsync(member, spaceId, Unique("by-member"), ct: ct);

        await member.Channels.MoveChannel(spaceId, channel, groupId, null, null, ct).Ok();
        var copy = await member.Channels.DuplicateChannel(spaceId, channel, ct);

        Assert.That(copy, Is.InstanceOf<SuccessDuplicateChannel>());

        var copyId = ((SuccessDuplicateChannel)copy).channel.channelId;
        await member.Channels.DeleteChannel(spaceId, copyId, ct).Ok();

        var channels = await ChannelsAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(channels.Single(c => c.channelId == channel).groupId, Is.EqualTo(groupId));
            Assert.That(channels.Select(c => c.channelId), Does.Not.Contain(copyId));
        });
    }

    // ── Moving channels ─────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task MoveChannel_BetweenTwoChannels_LandsBetweenThem(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Between", ct);
        var a       = await CreateChannelAsync(owner, spaceId, "a", ct: ct);
        var b       = await CreateChannelAsync(owner, spaceId, "b", ct: ct);
        var c       = await CreateChannelAsync(owner, spaceId, "c", ct: ct);

        await owner.Channels.MoveChannel(spaceId, c, null, a, b, ct).Ok();

        Assert.That(Order(await ChannelsAsync(owner, spaceId, ct), a, b, c), Is.EqualTo(new[] { a, c, b }));
    }

    /// <summary>
    /// Dropping a channel above the first one, when the first one holds the smallest index there is.
    /// </summary>
    /// <remarks>
    /// Nothing sorts before the minimum, so the grain moves the first channel up into the gap after
    /// it and gives the minimum to the one being moved. Pinned both with a channel after that gap and
    /// without one, because the grain computes the first channel's new place differently in each.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task MoveChannel_AboveAChannelHoldingTheSmallestIndex_TakesTheTop(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Above the minimum", ct);
        var a       = await CreateChannelAsync(owner, spaceId, "a", ct: ct);
        var b       = await CreateChannelAsync(owner, spaceId, "b", ct: ct);
        var c       = await CreateChannelAsync(owner, spaceId, "c", ct: ct);

        await owner.Channels.MoveChannel(spaceId, c, null, null, a, ct).Ok();
        Assert.That(Order(await ChannelsAsync(owner, spaceId, ct), a, b, c), Is.EqualTo(new[] { c, a, b }));

        var lone  = await CreateSpaceAsync(owner, "Two channels", ct);
        var first = await CreateChannelAsync(owner, lone, "first", ct: ct);
        var last  = await CreateChannelAsync(owner, lone, "last", ct: ct);

        await owner.Channels.MoveChannel(lone, last, null, null, first, ct).Ok();
        Assert.That(Order(await ChannelsAsync(owner, lone, ct), first, last), Is.EqualTo(new[] { last, first }));
    }

    /// <summary>
    /// A channel can be dragged to the top of a list whose first channel no longer holds the minimum.
    /// </summary>
    /// <remarks>
    /// Channels are created at the minimum, one past it, two past it. Move the first one down and the
    /// new first channel is "one past the minimum"; dropping another channel above it asked
    /// <c>FractionalIndex.Before</c> for the index below that, which is the minimum itself — and
    /// <c>Decrement</c> refuses to produce the minimum, so the move failed with a server error. Any
    /// reorder of the top two channels followed by a drag to the top hit it.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task MoveChannel_ToTheTop_AfterTheFirstChannelWasMovedDown(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Top", ct);
        var a       = await CreateChannelAsync(owner, spaceId, "a", ct: ct);
        var b       = await CreateChannelAsync(owner, spaceId, "b", ct: ct);
        var c       = await CreateChannelAsync(owner, spaceId, "c", ct: ct);

        await owner.Channels.MoveChannel(spaceId, a, null, c, null, ct).Ok();
        Assert.That(Order(await ChannelsAsync(owner, spaceId, ct), a, b, c), Is.EqualTo(new[] { b, c, a }));

        await owner.Channels.MoveChannel(spaceId, c, null, null, b, ct).Ok();
        Assert.That(Order(await ChannelsAsync(owner, spaceId, ct), a, b, c), Is.EqualTo(new[] { c, b, a }));
    }

    /// <summary>Neighbours that contradict each other describe no place, so nothing moves.</summary>
    [Test, CancelAfter(120_000)]
    public async Task MoveChannel_WithNeighboursInTheWrongOrder_ChangesNothing(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Contradiction", ct);
        var a       = await CreateChannelAsync(owner, spaceId, "a", ct: ct);
        var b       = await CreateChannelAsync(owner, spaceId, "b", ct: ct);
        var c       = await CreateChannelAsync(owner, spaceId, "c", ct: ct);
        var before  = await ChannelsAsync(owner, spaceId, ct);

        await owner.Channels.MoveChannel(spaceId, a, null, c, b, ct).Ok();

        Assert.That((await ChannelsAsync(owner, spaceId, ct)).Select(x => (x.channelId, x.fractionalIndex)),
            Is.EqualTo(before.Select(x => (x.channelId, x.fractionalIndex))));
    }

    [Test, CancelAfter(120_000)]
    public async Task MoveChannel_OfAChannelThatIsNotInTheSpace_ChangesNothing(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var alicesSpace = await CreateSpaceAsync(alice, "Alice", ct);
        var bobsSpace   = await CreateSpaceAsync(bob, "Bob", ct);
        var alicesA     = await CreateChannelAsync(alice, alicesSpace, "a", ct: ct);
        var alicesB     = await CreateChannelAsync(alice, alicesSpace, "b", ct: ct);
        var before      = await ChannelsAsync(alice, alicesSpace, ct);

        // Bob manages his own space, and names Alice's channel through it. A move of a channel the
        // space does not have is NOT_FOUND; a delete of one is already done.
        var notFound = new FailedChannelLayout(ChannelLayoutError.NOT_FOUND);

        Assert.That(await bob.Channels.MoveChannel(bobsSpace, alicesA, null, alicesB, null, ct), Is.EqualTo(notFound));
        Assert.That(await bob.Channels.DeleteChannel(bobsSpace, alicesB, ct), Is.InstanceOf<SuccessChannelLayout>());
        Assert.That(await bob.Channels.MoveChannel(bobsSpace, Guid.NewGuid(), null, null, null, ct), Is.EqualTo(notFound));
        Assert.That(await bob.Channels.DeleteChannel(bobsSpace, Guid.NewGuid(), ct), Is.InstanceOf<SuccessChannelLayout>());

        Assert.That((await ChannelsAsync(alice, alicesSpace, ct)).Select(x => (x.channelId, x.fractionalIndex)),
            Is.EqualTo(before.Select(x => (x.channelId, x.fractionalIndex))));
    }

    [Test, CancelAfter(120_000)]
    public async Task MoveChannel_IntoAGroupOfAnotherSpace_IsRefused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var alicesSpace = await CreateSpaceAsync(alice, "Alice", ct);
        var bobsSpace   = await CreateSpaceAsync(bob, "Bob", ct);
        var channel     = await CreateChannelAsync(alice, alicesSpace, Unique("mine"), ct: ct);
        var bobsGroup   = await CreateGroupAsync(bob, bobsSpace, Unique("bobs"), ct);

        Assert.That(await alice.Channels.MoveChannel(alicesSpace, channel, bobsGroup, null, null, ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NOT_FOUND)));

        Assert.That((await ChannelsAsync(alice, alicesSpace, ct)).Single(c => c.channelId == channel).groupId, Is.Null,
            "the channel was filed under a group its space does not have");
    }

    /// <summary>
    /// Squeezing channels between the same two neighbours again and again grows their index a
    /// character every few moves; past the threshold the grain rewrites the whole list evenly.
    /// </summary>
    [Test, CancelAfter(180_000)]
    public async Task MoveChannel_RepeatedlyIntoTheSameGap_RebalancesTheList(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Rebalance", ct);
        var a       = await CreateChannelAsync(owner, spaceId, "a", ct: ct);
        var b       = await CreateChannelAsync(owner, spaceId, "b", ct: ct);
        var x       = await CreateChannelAsync(owner, spaceId, "x", ct: ct);
        var y       = await CreateChannelAsync(owner, spaceId, "y", ct: ct);

        // Each move drops one of x/y directly under a, above the other one — so the gap under a
        // halves every time and the moved index grows.
        var (moving, other) = (x, b);

        for (var i = 0; i < 60; i++)
        {
            await owner.Channels.MoveChannel(spaceId, moving, null, a, other, ct).Ok();
            (moving, other) = moving == x ? (y, x) : (x, y);
        }

        var channels = await ChannelsAsync(owner, spaceId, ct);
        var last     = other;
        var previous = last == x ? y : x;

        Assert.Multiple(() =>
        {
            Assert.That(Order(channels, a, b, x, y), Is.EqualTo(new[] { a, last, previous, b }));
            Assert.That(channels.Select(c => c.fractionalIndex!.Length), Is.All.LessThanOrEqualTo(20),
                "sixty moves into one gap and the indices were never rewritten");
            Assert.That(channels.Select(c => c.fractionalIndex), Is.Unique);
        });
    }

    /// <summary>
    /// A channel dragged in from another group, into a gap too narrow for a short index, lands where
    /// it was dropped once the target group is rewritten.
    /// </summary>
    /// <remarks>
    /// The rewrite reads the group's channels from the database, where the one being moved is still
    /// filed under the group it came from — so it used to keep its long index while every channel
    /// around it was renumbered, and a long index squeezed next to the minimum sorts above all of
    /// them.
    /// </remarks>
    [Test, CancelAfter(180_000)]
    public async Task MoveChannel_FromAnotherGroupIntoANarrowGap_LandsWhereItWasDropped(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Cross-group rebalance", ct);
        var target  = await CreateGroupAsync(owner, spaceId, "target", ct);
        var source  = await CreateGroupAsync(owner, spaceId, "source", ct);
        var a       = await CreateChannelAsync(owner, spaceId, "a", groupId: target, ct: ct);
        var b       = await CreateChannelAsync(owner, spaceId, "b", groupId: target, ct: ct);
        var x       = await CreateChannelAsync(owner, spaceId, "x", groupId: target, ct: ct);
        var y       = await CreateChannelAsync(owner, spaceId, "y", groupId: target, ct: ct);
        var visitor = await CreateChannelAsync(owner, spaceId, "visitor", groupId: source, ct: ct);

        string IndexOf(List<ArgonChannel> channels, Guid id) => channels.Single(c => c.channelId == id).fractionalIndex!;

        // Squeeze x and y under a until the next squeeze would need an index past the threshold.
        var (moving, other) = (x, b);

        for (var i = 0; ; i++)
        {
            Assert.That(i, Is.LessThan(80), "the gap never narrowed enough");

            var channels = await ChannelsAsync(owner, spaceId, ct);
            var next     = Argon.Api.Features.Utils.FractionalIndex.Between(
                Argon.Api.Features.Utils.FractionalIndex.Parse(IndexOf(channels, a)),
                Argon.Api.Features.Utils.FractionalIndex.Parse(IndexOf(channels, other)));

            if (other != b && next.Value.Length > 20)
                break;

            await owner.Channels.MoveChannel(spaceId, moving, target, a, other, ct).Ok();
            (moving, other) = moving == x ? (y, x) : (x, y);
        }

        var below = other == x ? y : x;

        await owner.Channels.MoveChannel(spaceId, visitor, target, a, other, ct).Ok();

        var after = await ChannelsAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(after.Where(c => c.groupId == target).Select(c => c.channelId),
                Is.EqualTo(new[] { a, visitor, other, below, b }));
            Assert.That(after.Select(c => c.fractionalIndex!.Length), Is.All.LessThanOrEqualTo(20));
        });
    }

    // ── Channel groups ──────────────────────────────────────────────────────────────────────────

    [Test, CancelAfter(120_000)]
    public async Task MoveChannelGroup_FollowsTheNeighboursItIsGiven(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Group order", ct);
        var g1      = await CreateGroupAsync(owner, spaceId, "g1", ct);
        var g2      = await CreateGroupAsync(owner, spaceId, "g2", ct);
        var g3      = await CreateGroupAsync(owner, spaceId, "g3", ct);
        var ch      = owner.Channels;

        await ch.MoveChannelGroup(spaceId, g1, null, null, ct).Ok();
        Assert.That(GroupOrder(await GroupsAsync(owner, spaceId, ct), g1, g2, g3), Is.EqualTo(new[] { g2, g3, g1 }), "to the end");

        // g2 now heads the list holding "one past the minimum", so the only index below it is the
        // minimum itself — the drop the client sends as "before g2, after nothing".
        await ch.MoveChannelGroup(spaceId, g3, null, g2, ct).Ok();
        Assert.That(GroupOrder(await GroupsAsync(owner, spaceId, ct), g1, g2, g3), Is.EqualTo(new[] { g3, g2, g1 }), "to the top");

        await ch.MoveChannelGroup(spaceId, g1, g3, g2, ct).Ok();
        Assert.That(GroupOrder(await GroupsAsync(owner, spaceId, ct), g1, g2, g3), Is.EqualTo(new[] { g3, g1, g2 }), "between");

        var before = await GroupsAsync(owner, spaceId, ct);
        await ch.MoveChannelGroup(spaceId, g3, g2, g1, ct).Ok();
        Assert.That(await ch.MoveChannelGroup(spaceId, Guid.NewGuid(), null, null, ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NOT_FOUND)));
        Assert.That((await GroupsAsync(owner, spaceId, ct)).Select(g => (g.groupId, g.fractionalIndex)),
            Is.EqualTo(before.Select(g => (g.groupId, g.fractionalIndex))), "contradictory neighbours or an unknown group");
    }

    [Test, CancelAfter(120_000)]
    public async Task MoveChannelGroup_AboveAGroupHoldingTheSmallestIndex_TakesTheTop(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Group minimum", ct);
        var g1      = await CreateGroupAsync(owner, spaceId, "g1", ct);
        var g2      = await CreateGroupAsync(owner, spaceId, "g2", ct);
        var g3      = await CreateGroupAsync(owner, spaceId, "g3", ct);

        await owner.Channels.MoveChannelGroup(spaceId, g3, null, g1, ct).Ok();
        Assert.That(GroupOrder(await GroupsAsync(owner, spaceId, ct), g1, g2, g3), Is.EqualTo(new[] { g3, g1, g2 }));

        var lone   = await CreateSpaceAsync(owner, "Two groups", ct);
        var first  = await CreateGroupAsync(owner, lone, "first", ct);
        var second = await CreateGroupAsync(owner, lone, "second", ct);

        await owner.Channels.MoveChannelGroup(lone, second, null, first, ct).Ok();
        Assert.That(GroupOrder(await GroupsAsync(owner, lone, ct), first, second), Is.EqualTo(new[] { second, first }));
    }

    [Test, CancelAfter(120_000)]
    public async Task MoveChannelGroup_OfAGroupInAnotherSpace_ChangesNothing(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var alicesSpace = await CreateSpaceAsync(alice, "Alice", ct);
        var bobsSpace   = await CreateSpaceAsync(bob, "Bob", ct);
        var g1          = await CreateGroupAsync(alice, alicesSpace, "g1", ct);
        var g2          = await CreateGroupAsync(alice, alicesSpace, "g2", ct);
        var before      = await GroupsAsync(alice, alicesSpace, ct);

        Assert.That(await bob.Channels.MoveChannelGroup(bobsSpace, g1, g2, null, ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NOT_FOUND)));
        await bob.Channels.DeleteChannelGroup(bobsSpace, Guid.Empty, g2, true, ct).Ok();

        Assert.That((await GroupsAsync(alice, alicesSpace, ct)).Select(g => (g.groupId, g.fractionalIndex)),
            Is.EqualTo(before.Select(g => (g.groupId, g.fractionalIndex))));
    }

    [Test, CancelAfter(180_000)]
    public async Task MoveChannelGroup_RepeatedlyIntoTheSameGap_RebalancesTheGroups(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Group rebalance", ct);
        var a       = await CreateGroupAsync(owner, spaceId, "a", ct);
        var b       = await CreateGroupAsync(owner, spaceId, "b", ct);
        var x       = await CreateGroupAsync(owner, spaceId, "x", ct);
        var y       = await CreateGroupAsync(owner, spaceId, "y", ct);

        var (moving, other) = (x, b);

        for (var i = 0; i < 60; i++)
        {
            await owner.Channels.MoveChannelGroup(spaceId, moving, a, other, ct).Ok();
            (moving, other) = moving == x ? (y, x) : (x, y);
        }

        var groups   = await GroupsAsync(owner, spaceId, ct);
        var last     = other;
        var previous = last == x ? y : x;

        Assert.Multiple(() =>
        {
            Assert.That(GroupOrder(groups, a, b, x, y), Is.EqualTo(new[] { a, last, previous, b }));
            Assert.That(groups.Select(g => g.fractionalIndex!.Length), Is.All.LessThanOrEqualTo(20));
            Assert.That(groups.Select(g => g.fractionalIndex), Is.Unique);
        });
    }

    [TestCase("")]
    [TestCase("   ")]
    [CancelAfter(120_000)]
    public async Task CreateChannelGroup_WithoutAName_IsRefused(string name, CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Blank groups", ct);

        Assert.That(await owner.Channels.CreateChannelGroup(spaceId, Guid.Empty, name, null, ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)));
        Assert.That(await owner.Channels.CreateChannelGroup(spaceId, Guid.Empty, new string('g', 129), null, ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)));

        Assert.That(await GroupsAsync(owner, spaceId, ct), Is.Empty);
    }

    [Test, CancelAfter(120_000)]
    public async Task UpdateChannelGroup_RefusesAGroupThatIsNotThereAndABlankName(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Group updates", ct);
        var groupId = await CreateGroupAsync(owner, spaceId, "named", ct);

        Assert.That(await owner.Channels.UpdateChannelGroup(spaceId, Guid.Empty, Guid.NewGuid(), "x", null, ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.NOT_FOUND)));
        Assert.That(await owner.Channels.UpdateChannelGroup(spaceId, Guid.Empty, groupId, "  ", null, ct),
            Is.EqualTo(new FailedChannelLayout(ChannelLayoutError.INVALID_DATA)));

        Assert.That((await GroupsAsync(owner, spaceId, ct)).Single().name, Is.EqualTo("named"));
    }

    /// <summary>Collapsing is its own flag: it leaves the name and description alone.</summary>
    [Test, CancelAfter(120_000)]
    public async Task UpdateChannelGroup_CollapsesAGroupWithoutTouchingItsName(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Collapsing", ct);
        var groupId = await CreateGroupAsync(owner, spaceId, "folded", ct);

        Assert.That(await AsCallerAsync(owner.UserId, () => SpaceGrain(spaceId).UpdateChannelGroup(groupId, isCollapsed: true)),
            Is.EqualTo(ChannelLayoutError.NONE));

        var group = (await GroupsAsync(owner, spaceId, ct)).Single();

        Assert.Multiple(() =>
        {
            Assert.That(group.isCollapsed, Is.True);
            Assert.That(group.name, Is.EqualTo("folded"));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteChannelGroup_WithItsChannels_RemovesThemAndTellsTheSpace(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Group deletion", ct);
        var groupId = await CreateGroupAsync(owner, spaceId, "doomed", ct);
        var inside1 = await CreateChannelAsync(owner, spaceId, "inside-1", groupId: groupId, ct: ct);
        var inside2 = await CreateChannelAsync(owner, spaceId, "inside-2", groupId: groupId, ct: ct);
        var outside = await CreateChannelAsync(owner, spaceId, "outside", ct: ct);

        await using var watcher = await RealtimeClient.ConnectAsync(owner, ct);
        await watcher.SubscribeToSpace(spaceId, ct);
        var mark = watcher.Mark();

        await owner.Channels.DeleteChannelGroup(spaceId, Guid.Empty, groupId, deleteChannels: true, ct).Ok();

        await watcher.WaitForAsync<ChannelGroupRemoved>(e => e.groupId == groupId, EventWait, mark, ct);
        var removed = watcher.EventsOfType<ChannelRemoved>(mark).Select(e => e.channelId).ToList();

        var channels = await ChannelsAsync(owner, spaceId, ct);
        var groups   = await GroupsAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(channels.Select(c => c.channelId), Is.EqualTo(new[] { outside }));
            Assert.That(groups, Is.Empty);
            Assert.That(removed, Is.EquivalentTo(new[] { inside1, inside2 }));
        });

        // A group that is already gone is not an error: the client may be a click behind.
        await owner.Channels.DeleteChannelGroup(spaceId, Guid.Empty, groupId, deleteChannels: true, ct).Ok();
        Assert.That((await ChannelsAsync(owner, spaceId, ct)).Select(c => c.channelId), Is.EqualTo(new[] { outside }));
    }

    [Test, CancelAfter(120_000)]
    public async Task DeleteChannelGroup_KeepingItsChannels_LeavesThemUngrouped(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Group dissolved", ct);
        var groupId = await CreateGroupAsync(owner, spaceId, "dissolved", ct);
        var inside  = await CreateChannelAsync(owner, spaceId, "inside", groupId: groupId, ct: ct);

        await owner.Channels.DeleteChannelGroup(spaceId, Guid.Empty, groupId, deleteChannels: false, ct).Ok();

        var channels = await ChannelsAsync(owner, spaceId, ct);
        var groups   = await GroupsAsync(owner, spaceId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(channels.Select(c => (c.channelId, c.groupId)), Is.EqualTo(new[] { (inside, (Guid?)null) }));
            Assert.That(groups, Is.Empty);
        });
    }

    // ── Duplicating ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A duplicate is the same room — kind, name, topic, cooldown, bitrate and, above all, its
    /// permission overwrites — placed directly after the original.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task DuplicateChannel_CopiesTheRoomAndLandsRightAfterIt(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Duplicates", ct);
        var source  = await CreateChannelAsync(owner, spaceId, "private-room", ct: ct);
        var next    = await CreateChannelAsync(owner, spaceId, "next", ct: ct);
        var voice   = await CreateChannelAsync(owner, spaceId, "voice", ChannelType.Voice, ct: ct);

        var slowed = await owner.Channels.UpdateChannel(spaceId, source, null, null, 30, null, ct);
        var tuned  = await owner.Channels.UpdateChannel(spaceId, voice, null, null, null, 96, ct);
        Assert.That(slowed, Is.InstanceOf<SuccessUpdateChannel>());
        Assert.That(tuned, Is.InstanceOf<SuccessUpdateChannel>());

        var archetypes = Archetypes(owner);
        var role       = await archetypes.CreateArchetype(spaceId, "hidden-from", ct).Ok();
        await archetypes.UpsertArchetypeEntitlementForChannel(spaceId, source, role.id, ArgonEntitlement.ViewChannel, ArgonEntitlement.None, ct);

        var copied = await owner.Channels.DuplicateChannel(spaceId, source, ct);
        Assert.That(copied, Is.InstanceOf<SuccessDuplicateChannel>(), $"{(copied as FailedDuplicateChannel)?.error}");
        var copy = ((SuccessDuplicateChannel)copied).channel;

        var voiceCopy = ((SuccessDuplicateChannel)await owner.Channels.DuplicateChannel(spaceId, voice, ct)).channel;

        var channels   = await ChannelsAsync(owner, spaceId, ct);
        var overwrites = await archetypes.GetChannelEntitlementOverwrites(spaceId, copy.channelId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(copy.channelId, Is.Not.EqualTo(source));
            Assert.That(copy.name, Is.EqualTo("private-room"));
            Assert.That(copy.type, Is.EqualTo(ChannelType.Text));
            Assert.That(copy.slowModeSeconds, Is.EqualTo(30));
            Assert.That(voiceCopy.bitrate, Is.EqualTo(96));
            Assert.That(Order(channels, source, next, voice, copy.channelId, voiceCopy.channelId),
                Is.EqualTo(new[] { source, copy.channelId, next, voice, voiceCopy.channelId }));
            Assert.That(overwrites.Values.Select(o => (o.archetypeId, o.deny, o.allow)),
                Is.EqualTo(new[] { ((Guid?)role.id, ArgonEntitlement.ViewChannel, ArgonEntitlement.None) }),
                "a private room was copied without the overwrite that makes it private");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task DuplicateChannel_OfAChannelThatIsNotThere_SaysSo(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);

        var alicesSpace = await CreateSpaceAsync(alice, "Alice", ct);
        var bobsSpace   = await CreateSpaceAsync(bob, "Bob", ct);
        var alicesRoom  = await CreateChannelAsync(alice, alicesSpace, "alices", ct: ct);

        var unknown = await bob.Channels.DuplicateChannel(bobsSpace, Guid.NewGuid(), ct);
        var foreign = await bob.Channels.DuplicateChannel(bobsSpace, alicesRoom, ct);
        var bobs    = await ChannelsAsync(bob, bobsSpace, ct);
        var alices  = await ChannelsAsync(alice, alicesSpace, ct);

        Assert.Multiple(() =>
        {
            Assert.That((unknown as FailedDuplicateChannel)?.error, Is.EqualTo(DuplicateChannelError.CHANNEL_NOT_FOUND));
            Assert.That((foreign as FailedDuplicateChannel)?.error, Is.EqualTo(DuplicateChannelError.CHANNEL_NOT_FOUND));
            Assert.That(bobs, Is.Empty);
            Assert.That(alices.Select(c => c.channelId), Is.EqualTo(new[] { alicesRoom }));
        });
    }

    /// <summary>
    /// A channel written before channels had an order has no index to sit after; its copy goes to
    /// the end of the list rather than the duplicate failing.
    /// </summary>
    [Test, CancelAfter(120_000)]
    public async Task DuplicateChannel_OfAChannelWithNoPosition_GoesToTheEnd(CancellationToken ct = default)
    {
        var owner   = await CreateSessionAsync(ct);
        var spaceId = await CreateSpaceAsync(owner, "Legacy", ct);
        var legacy  = await CreateChannelAsync(owner, spaceId, "legacy", ct: ct);
        var next    = await CreateChannelAsync(owner, spaceId, "next", ct: ct);

        await using (var db = await NewDbAsync(ct))
            await db.Channels.Where(c => c.Id == legacy)
               .ExecuteUpdateAsync(s => s.SetProperty(c => c.FractionalIndex, ""), ct);

        var copied = await owner.Channels.DuplicateChannel(spaceId, legacy, ct);
        Assert.That(copied, Is.InstanceOf<SuccessDuplicateChannel>(), $"{(copied as FailedDuplicateChannel)?.error}");

        var copy     = ((SuccessDuplicateChannel)copied).channel;
        var channels = await ChannelsAsync(owner, spaceId, ct);

        Assert.That(Order(channels, legacy, next, copy.channelId), Is.EqualTo(new[] { legacy, next, copy.channelId }));
    }
}
