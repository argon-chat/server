namespace Argon.Grains;

/// <summary>
/// The highest message id a channel activation has seen, and the highest it has already written
/// down — the whole of the rule that turns one durable write per message into one per flush.
/// </summary>
/// <remarks>
/// <para>
/// This holds no lock and does no compare-and-swap, and it does not need to: an activation is the
/// only writer for its channel (see <c>ChannelGrain.DeduplicateAsync</c> for the same argument
/// applied to dedup), so "the highest id seen since the last flush" is the complete truth about
/// what the row is missing. The mark only ever rises, which is what makes a lost intermediate value
/// harmless — the last one carries all of them.
/// </para>
/// <para>
/// It is a type of its own rather than two fields on the grain so the rule can be exercised without
/// a silo, a database, or a timer. Everything subtle about the coalescing lives here; the grain only
/// decides <em>when</em> to flush.
/// </para>
/// <para>
/// Flushing is deliberately two steps. Before coalescing, a failed write healed itself: the next
/// message rewrote the row a moment later. Now a flush carries everything since the previous one,
/// so retiring the mark before the write lands would mean a channel that goes quiet right after a
/// failure keeps a stale <c>LastMessageId</c> forever — and every member's unread badge for it stays
/// wrong. Only <see cref="CommitFlush"/> retires it, so a failed flush is simply retried by the next
/// tick.
/// </para>
/// </remarks>
public sealed class ChannelHighWaterMark
{
    private long pending;
    private long flushed;

    /// <summary>Records that <paramref name="messageId"/> was accepted into the channel.</summary>
    /// <returns>
    /// <c>true</c> when this id cost nothing — a write was already owed and this folded into it, or
    /// the id is not newer than what is already written down. <c>false</c> when this is the id that
    /// gives the next flush something to do. Summed, the two answers are what proves the coalescing
    /// is working at all: absorbed plus flushes should track messages sent.
    /// </returns>
    public bool Raise(long messageId)
    {
        var wasOwed = pending > flushed;

        if (messageId > pending)
            pending = messageId;

        return wasOwed || pending <= flushed;
    }

    /// <summary>
    /// The id a flush should write, if anything is owed.
    /// </summary>
    /// <remarks>
    /// Reads without retiring — see the two-step note on the type. Calling it twice without a
    /// <see cref="CommitFlush"/> in between answers the same thing twice, on purpose.
    /// </remarks>
    public bool TryBeginFlush(out long messageId)
    {
        messageId = pending;
        return pending > flushed;
    }

    /// <summary>Retires everything up to <paramref name="messageId"/>, once its write has landed.</summary>
    /// <remarks>
    /// Takes the id back rather than reading <see cref="pending"/> again because sends that arrived
    /// while the write was in flight belong to the <em>next</em> flush, not this one.
    /// </remarks>
    public void CommitFlush(long messageId)
    {
        if (messageId > flushed)
            flushed = messageId;
    }
}
