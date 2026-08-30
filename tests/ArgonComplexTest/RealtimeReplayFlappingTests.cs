namespace ArgonComplexTest;

using Argon.Core.Features.Transport;
using ArgonComplexTest.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using System.Text;

/// <summary>
/// What a client on a bad connection gets back, against the real Redis the buffer runs on.
/// </summary>
/// <remarks>
/// <para>The neighbouring fixture asks what a cursor is allowed to <em>mean</em> — foreign, expired,
/// absent. This one asks what happens across a connection that keeps dropping, which is the case the
/// buffer exists for and the one a single read cannot show: a client that reconnects six times sends
/// six cursors, and the events it is handed have to concatenate back into exactly what was published,
/// once each and in order. Losing one is a message that never appears; repeating one is a message
/// that appears twice, and only the client's own dedupe stands between that and the user seeing it.</para>
///
/// <para>These need a real Redis: the buffer is <c>XADD</c> and <c>XRANGE</c> and the properties
/// under test are Redis's ordering and trimming, not ours.</para>
/// </remarks>
[TestFixture]
public class RealtimeReplayFlappingTests
{
    private static IRealtimeReplayBuffer Buffer
        => ArgonTestEnvironment.Instance.Host.Services.GetRequiredService<IRealtimeReplayBuffer>();

    /// <summary>
    /// Mirrors <c>RedisRealtimeReplayBuffer.MaxReplay</c>, which is private.
    /// </summary>
    /// <remarks>
    /// Both sides of the boundary are asserted below, so moving the constant without moving this
    /// breaks one of the two rather than passing quietly on the wrong number.
    /// </remarks>
    private const int MaxReplay = 2048;

    private static byte[] Payload(int n) => Encoding.UTF8.GetBytes($"event-{n}");

    private static int NumberOf(ReplayEntry entry)
        => int.Parse(Encoding.UTF8.GetString(entry.Payload)["event-".Length..]);

    /// <summary>
    /// Six drops, and at the end the client has seen every event once, in order.
    /// </summary>
    /// <remarks>
    /// This is the property the whole buffer is for, and it is not implied by any single read being
    /// correct: each resume is asked to hand back exactly the events minted since the cursor the
    /// client last acknowledged, and the seams between them are where an off-by-one lives. An
    /// inclusive lower bound instead of an exclusive one would deliver the boundary event twice —
    /// six times over the run, once per reconnect — and every individual read would still look
    /// reasonable.
    /// </remarks>
    [Test]
    public async Task A_flapping_client_sees_every_event_once_and_in_order()
    {
        var userId = Guid.NewGuid();
        var seen   = new List<int>();

        // The first connect is not a resume: the client has just fetched fresh state and is owed
        // nothing, so it starts holding the newest id rather than replaying up to it. Everything
        // minted from here is what the drops below are accountable for.
        var cursor = await Buffer.AppendUserAsync(userId, Payload(-1));

        var minted = 0;

        for (var drop = 0; drop < 6; drop++)
        {
            // Offline. The world does not stop while a client is reconnecting, and the burst size
            // varies per drop so this is not one shape repeated six times — in particular a drop
            // with nothing published is its own case, and the last one below is empty.
            var burst = 5 - drop;
            for (var i = 0; i < burst; i++)
                await Buffer.AppendUserAsync(userId, Payload(minted++));

            // Back online: resume from where it left off.
            var result = await Buffer.ReadUserSinceAsync(userId, cursor);

            Assert.That(result.Gap, Is.False,
                $"drop {drop}: a cursor this stream issued moments ago must not read as a gap");
            Assert.That(result.Entries, Has.Count.EqualTo(burst),
                $"drop {drop}: the replay must be exactly what was published while it was away");

            seen.AddRange(result.Entries.Select(NumberOf));

            // A resume that returned nothing leaves the cursor where it was — there is nothing
            // newer to point at, and moving it would be inventing a position.
            if (result.Entries.Count > 0)
                cursor = result.Entries[^1].Id;
        }

        Assert.Multiple(() =>
        {
            Assert.That(seen, Is.EqualTo(Enumerable.Range(0, minted).ToList()),
                "the replays across six reconnects must concatenate into the published sequence — "
              + "every event once, in the order it was minted");
            Assert.That(seen, Is.Unique, "an event delivered twice is a message the user sees twice");
        });
    }

    /// <summary>
    /// Resuming from the newest event is being caught up, which is not the same as a gap.
    /// </summary>
    /// <remarks>
    /// The distinction costs a full resync every time it is got wrong, and a client whose connection
    /// flaps while nothing is happening hits it on every reconnect — which is exactly when it can
    /// least afford to be handed the whole world again.
    /// </remarks>
    [Test]
    public async Task Resuming_from_the_newest_event_is_caught_up_rather_than_a_gap()
    {
        var userId = Guid.NewGuid();

        await Buffer.AppendUserAsync(userId, Payload(1));
        var newest = await Buffer.AppendUserAsync(userId, Payload(2));

        var result = await Buffer.ReadUserSinceAsync(userId, newest);

        Assert.Multiple(() =>
        {
            Assert.That(result.Gap, Is.False, "nothing has happened since; that is not a discontinuity");
            Assert.That(result.Entries, Is.Empty, "and there is nothing to hand back");
        });
    }

    /// <summary>
    /// The same cursor twice gives the same answer, because a flapping client will send it twice.
    /// </summary>
    /// <remarks>
    /// A reconnect that succeeds far enough to be answered and then drops before the client stores
    /// anything is an ordinary outcome of a bad connection, and the client comes back with the cursor
    /// it already had. A read that consumed anything — advanced a pointer, marked entries delivered —
    /// would hand back less the second time, and the events in between would be gone with no gap
    /// reported.
    /// </remarks>
    [Test]
    public async Task Resuming_twice_from_one_cursor_answers_the_same_both_times()
    {
        var userId = Guid.NewGuid();

        var cursor = await Buffer.AppendUserAsync(userId, Payload(0));
        await Buffer.AppendUserAsync(userId, Payload(1));
        await Buffer.AppendUserAsync(userId, Payload(2));

        var first  = await Buffer.ReadUserSinceAsync(userId, cursor);
        var second = await Buffer.ReadUserSinceAsync(userId, cursor);

        Assert.Multiple(() =>
        {
            Assert.That(first.Entries.Select(NumberOf), Is.EqualTo(new[] { 1, 2 }));
            Assert.That(second.Entries.Select(NumberOf), Is.EqualTo(first.Entries.Select(NumberOf)),
                "reading a replay must not consume it");
            Assert.That(second.Gap, Is.False);
        });
    }

    /// <summary>
    /// An id another stream of the same kind really issued is still a gap.
    /// </summary>
    /// <remarks>
    /// The neighbouring fixture builds a synthetic id that no stream issued. This one takes a real,
    /// in-window id from a different user's stream — right shape, right age, minted by the same Redis
    /// — which is what a mixed-up session or a reused cursor actually looks like. Nothing about the
    /// id itself can betray it; only asking whether <em>this</em> stream issued it can.
    /// </remarks>
    [Test]
    public async Task A_cursor_that_belongs_to_another_users_stream_is_a_gap()
    {
        var mine      = Guid.NewGuid();
        var stranger  = Guid.NewGuid();

        await Buffer.AppendUserAsync(mine, Payload(1));
        var theirs = await Buffer.AppendUserAsync(stranger, Payload(2));
        await Buffer.AppendUserAsync(mine, Payload(3));

        var result = await Buffer.ReadUserSinceAsync(mine, theirs);

        Assert.Multiple(() =>
        {
            Assert.That(result.Gap, Is.True,
                "a cursor from someone else's stream cannot promise continuity on this one");
            Assert.That(result.Entries, Is.Empty);
        });
    }

    /// <summary>
    /// Missing exactly the cap replays; one more is a gap.
    /// </summary>
    /// <remarks>
    /// <para>Both sides are asserted because only the pair says where the edge is. A cap that
    /// silently returned the first <c>MaxReplay</c> of a longer backlog would pass a test that only
    /// checked the small case, and the client would be handed a prefix of what it missed with no gap
    /// flag — the worst of the three outcomes, because it looks like a successful resume and leaves a
    /// hole in the middle of the timeline.</para>
    ///
    /// <para>Slow on purpose: the cap can only be observed by actually minting past it.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task The_replay_cap_is_a_gap_rather_than_a_truncated_replay()
    {
        var atCap   = Guid.NewGuid();
        var overCap = Guid.NewGuid();

        var atCapCursor = await Buffer.AppendUserAsync(atCap, Payload(0));
        for (var i = 0; i < MaxReplay; i++)
            await Buffer.AppendUserAsync(atCap, Payload(i + 1));

        var overCapCursor = await Buffer.AppendUserAsync(overCap, Payload(0));
        for (var i = 0; i < MaxReplay + 1; i++)
            await Buffer.AppendUserAsync(overCap, Payload(i + 1));

        var within = await Buffer.ReadUserSinceAsync(atCap, atCapCursor);
        var beyond = await Buffer.ReadUserSinceAsync(overCap, overCapCursor);

        Assert.Multiple(() =>
        {
            Assert.That(within.Gap, Is.False, $"missing exactly {MaxReplay} events is still replayable");
            Assert.That(within.Entries, Has.Count.EqualTo(MaxReplay));

            Assert.That(beyond.Gap, Is.True, $"missing {MaxReplay + 1} must ask for a resync");
            Assert.That(beyond.Entries, Is.Empty,
                "and must hand back nothing rather than a prefix that looks like a complete replay");
        });
    }

    /// <summary>
    /// Appends racing each other still replay in one order, and all of them do.
    /// </summary>
    /// <remarks>
    /// A space with several people talking at once appends from several silos at once. Redis mints
    /// the ids, so the order is its to decide — what this asserts is that whatever order it chose is
    /// the order the replay returns, that the ids ascend, and that concurrency loses none of them.
    /// </remarks>
    [Test]
    public async Task Concurrent_appends_replay_in_full_and_in_one_order()
    {
        var spaceId = Guid.NewGuid();
        var anchor  = await Buffer.AppendSpaceAsync(spaceId, Payload(0));

        const int Writers = 8;
        const int Each    = 25;

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(async w =>
        {
            for (var i = 0; i < Each; i++)
                await Buffer.AppendSpaceAsync(spaceId, Payload(w * Each + i + 1));
        }));

        var result = await Buffer.ReadSpaceSinceAsync(spaceId, anchor);

        Assert.Multiple(() =>
        {
            Assert.That(result.Gap, Is.False);
            Assert.That(result.Entries, Has.Count.EqualTo(Writers * Each),
                "a concurrent append that never came back in the replay is a lost event");
            Assert.That(result.Entries.Select(NumberOf).Distinct().Count(), Is.EqualTo(Writers * Each),
                "and none of them may come back twice");

            var ids = result.Entries.Select(e => e.Id).ToList();
            Assert.That(ids, Is.EqualTo(ids.OrderBy(id => id, new StreamIdComparer()).ToList()),
                "entries must be handed back in stream order");
        });
    }

    /// <summary>Stream ids are "&lt;unixMs&gt;-&lt;seq&gt;", and both halves are numbers, not text.</summary>
    /// <remarks>
    /// Ordinal string comparison would call "10-0" smaller than "9-0", so an ordering assertion made
    /// with it would pass on any order at all once the millisecond count changed width.
    /// </remarks>
    private sealed class StreamIdComparer : IComparer<string>
    {
        public int Compare(string? left, string? right)
        {
            var (lms, lseq) = Split(left!);
            var (rms, rseq) = Split(right!);
            return lms != rms ? lms.CompareTo(rms) : lseq.CompareTo(rseq);
        }

        private static (long Ms, long Seq) Split(string id)
        {
            var dash = id.LastIndexOf('-');
            return (long.Parse(id[..dash]), long.Parse(id[(dash + 1)..]));
        }
    }
}
