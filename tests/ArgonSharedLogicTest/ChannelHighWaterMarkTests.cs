namespace ArgonSharedLogicTest;

using Argon.Grains;

/// <summary>
/// The rule that decides how many database writes a burst of messages costs a channel.
/// </summary>
/// <remarks>
/// Before coalescing, every message sent ran an <c>ExecuteUpdateAsync</c> against
/// <c>Channels.LastMessageId</c> — a Cockroach commit per message, on a row that is otherwise cold
/// metadata. The whole saving rests on this type answering "how much is owed" correctly, so it is
/// exercised here rather than through a grain: no silo, no database, no three-second timer to wait
/// on, and a failure points at the rule instead of at the plumbing around it.
/// <para>
/// The counter has since moved off the channel row entirely, into <c>ChannelLastMessages</c>, which
/// is what let the channel table go back to <c>LOCALITY GLOBAL</c>. That changed where the flush
/// writes and nothing about what it owes, so every rule below reads the same as it did — this type
/// never knew which table it was saving writes to.
/// </para>
/// </remarks>
[TestFixture]
public class ChannelHighWaterMarkTests
{
    /// <summary>
    /// Drains the mark the way the grain's flush does, and reports what reached the database.
    /// </summary>
    private static List<long> Flush(ChannelHighWaterMark mark, bool writeSucceeds = true)
    {
        if (!mark.TryBeginFlush(out var messageId))
            return [];

        if (!writeSucceeds)
            return [];

        mark.CommitFlush(messageId);
        return [messageId];
    }

    /// <summary>
    /// The point of the whole change: a burst of sends between two timer ticks has to collapse into
    /// one row update carrying the newest id. If this ever produces one write per message again, the
    /// grain is back to a commit per message and nothing else in the system will say so.
    /// </summary>
    [Test]
    public void A_burst_of_sends_costs_exactly_one_write_carrying_the_newest_id()
    {
        var mark = new ChannelHighWaterMark();

        foreach (var messageId in Enumerable.Range(1, 500))
            mark.Raise(messageId);

        Assert.That(Flush(mark), Is.EqualTo(new[] { 500L }));
    }

    /// <summary>
    /// The common case for a channel nobody is talking in. An activation outlives the conversation
    /// in it by a long way, so a timer that wrote on every tick regardless would replace one write
    /// per message with one write every three seconds forever — worse than what it replaced for the
    /// long tail of quiet channels, which is most of them.
    /// </summary>
    [Test]
    public void A_flush_with_nothing_pending_writes_nothing()
    {
        var mark = new ChannelHighWaterMark();

        Assert.That(Flush(mark), Is.Empty);
    }

    /// <summary>
    /// Guards the flushed half of the pair actually being consulted. Dropping it would leave the
    /// timer rewriting the same id every three seconds for the lifetime of the activation.
    /// </summary>
    [Test]
    public void A_second_flush_with_no_sends_in_between_writes_nothing()
    {
        var mark = new ChannelHighWaterMark();

        mark.Raise(41);
        mark.Raise(42);

        Assert.That(Flush(mark), Is.EqualTo(new[] { 42L }));
        Assert.That(Flush(mark), Is.Empty, "the mark was already written down");
        Assert.That(Flush(mark), Is.Empty, "and still is");
    }

    /// <summary>
    /// Sends that arrive after a flush belong to the next one. The regression this guards is a mark
    /// that retires everything pending at commit time rather than the id it was handed, which would
    /// silently swallow anything sent while the write was in flight.
    /// </summary>
    [Test]
    public void Sends_after_a_flush_are_written_by_the_next_one()
    {
        var mark = new ChannelHighWaterMark();

        mark.Raise(10);
        Assert.That(Flush(mark), Is.EqualTo(new[] { 10L }));

        mark.Raise(11);
        mark.Raise(12);
        Assert.That(Flush(mark), Is.EqualTo(new[] { 12L }));
    }

    /// <summary>
    /// The mark only ever rises. An id that is not newer than what is already noted — an out-of-order
    /// completion, or a retry answered from the dedup memory — must not pull the channel's
    /// last-message pointer backwards, because every member's unread badge is computed from it.
    /// </summary>
    [Test]
    public void An_older_id_never_lowers_the_mark()
    {
        var mark = new ChannelHighWaterMark();

        mark.Raise(100);
        mark.Raise(7);

        Assert.That(Flush(mark), Is.EqualTo(new[] { 100L }));

        mark.Raise(50);

        Assert.That(Flush(mark), Is.Empty, "50 is older than what was already written");
    }

    /// <summary>
    /// A flush whose write failed has to be retried, not forgotten. Before coalescing a failed write
    /// healed itself — the next message rewrote the row a moment later — but a flush now carries
    /// every message since the previous one, and a channel can fall silent immediately after one.
    /// Retiring the mark on a failure would leave that channel's stored mark stale for as long as
    /// nobody spoke in it.
    /// </summary>
    [Test]
    public void A_flush_whose_write_failed_is_retried_by_the_next_one()
    {
        var mark = new ChannelHighWaterMark();

        mark.Raise(9);

        Assert.That(Flush(mark, writeSucceeds: false), Is.Empty);
        Assert.That(Flush(mark), Is.EqualTo(new[] { 9L }), "the failed flush is still owed");
    }

    /// <summary>
    /// The counter behind the coalescing ratio. Every send after the first one in an interval has to
    /// report itself as absorbed, or the metric that is supposed to prove the timer is firing will
    /// read as if it never was.
    /// </summary>
    [Test]
    public void Every_send_but_the_one_that_opens_a_flush_reports_itself_absorbed()
    {
        var mark = new ChannelHighWaterMark();

        Assert.Multiple(() =>
        {
            Assert.That(mark.Raise(1), Is.False, "this send is the one that gives the flush work to do");
            Assert.That(mark.Raise(2), Is.True);
            Assert.That(mark.Raise(3), Is.True);
        });

        Flush(mark);

        Assert.Multiple(() =>
        {
            Assert.That(mark.Raise(4), Is.False, "the interval starts over after a flush");
            Assert.That(mark.Raise(5), Is.True);

            // Nothing new to write, so nothing was bought — an id at or below the written mark costs
            // no more than one folded into a pending write does.
            Assert.That(new ChannelHighWaterMark().Raise(0), Is.True);
        });
    }
}
