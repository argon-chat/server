namespace Argon.Services;

public interface IArgonCacheDatabase
{
    Task          StringSetAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default);

    /// <summary>
    /// Extends (or shortens) a key's lifetime without touching its value — a plain <c>EXPIRE</c>.
    /// </summary>
    /// <remarks>
    /// Type-agnostic and value-agnostic: it works on a string, a set or anything else, returns
    /// nothing, and is a no-op when the key is already gone — an expired key cannot be revived by
    /// re-expiring it. That last property is what makes it the right call for keep-alives (a session
    /// tick renewing an activity it has no copy of), and it is why <see cref="KeyExpireAsync"/> —
    /// which is <c>GETEX</c>, string-only, and hands the value back — is a different method with a
    /// confusingly similar name rather than an overload of this one.
    /// </remarks>
    Task          UpdateStringExpirationAsync(string key, TimeSpan expiration, CancellationToken ct = default);
    Task          StringSetAsync(string key, string value, CancellationToken ct = default);
    Task<string?> StringGetAsync(string key, CancellationToken ct = default);
    Task          KeyDeleteAsync(string key, CancellationToken ct = default);
    Task<bool>    KeyExistsAsync(string key, CancellationToken ct = default);

    Task<long> StringIncrementAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Reads a string key and re-arms its lifetime in one round trip — Redis <c>GETEX</c>.
    /// </summary>
    /// <remarks>
    /// Despite the name this is not "expire a key": it is a READ that also sets an expiry, it only
    /// works on strings, and it errors on a key holding any other type. Callers that just want to
    /// keep something alive want <see cref="UpdateStringExpirationAsync"/> instead; this one is for
    /// the sliding-window records that have to read and renew atomically (a device tombstone, a rate
    /// limiter) and would otherwise race between the read and the renewal.
    /// </remarks>
    Task<string> KeyExpireAsync(string key, TimeSpan window, CancellationToken ct = default);

    /// <summary>
    /// Writes <paramref name="value"/> with <paramref name="expiration"/> and returns what the key
    /// held before, in a single round trip — Redis <c>SET key value EX … GET</c>.
    /// </summary>
    /// <remarks>
    /// The atomic form of read-compare-write. A caller that wants "did I change this?" cannot get it
    /// from <see cref="StringGetAsync"/> followed by <see cref="StringSetAsync"/>: every concurrent
    /// caller reads the old value before any of them writes, so all of them conclude they changed it.
    /// Answering from the single write instead makes exactly one of N callers see the transition —
    /// which is what defect S19 was (the presence hysteresis record, fanning one status change out
    /// once per racing broadcaster), pinned by
    /// <c>PresenceAggregationTests.MarkBroadcastIfChanged_UnderConcurrency_AnnouncesOnce</c>.
    /// Returns null when the key held nothing.
    /// </remarks>
    Task<string?> StringSetAndGetPreviousAsync(string key, string value, TimeSpan expiration, CancellationToken ct = default);


    /// <summary>
    /// Reads a set, reads the string key each of its members names, and writes the highest-ranked of
    /// those values to another key — all as one indivisible step.
    /// </summary>
    /// <remarks>
    /// <para>The atomic form of a read-fold-write, and it exists for the same reason
    /// <see cref="StringSetAndGetPreviousAsync"/> does: the obvious spelling is a race. Presence folds
    /// every live session's status into <c>status:user:{u}:aggregated</c>, and it did so as
    /// <c>SMEMBERS</c>, a <c>GET</c> per member and a <c>SET</c> — three trips with nothing holding
    /// them together, run from a <c>[StatelessWorker]</c> grain, so two of a user's sessions moving at
    /// once produced two overlapping folds. A fold that <em>started</em> before a newly arrived device
    /// was indexed could <em>finish</em> after the fold that saw it, and the value left cached was
    /// Offline for a user sitting there connected — not transiently, but until they next changed
    /// something. Doing the read and the write in one step makes the order the writes land in the same
    /// order the reads happened in, which is the whole of the fix.</para>
    ///
    /// <para><b>Ranked, rather than aggregated by a delegate</b>, because the fold has to run at the
    /// store: <paramref name="ranking"/> carries the caller's precedence order over the wire and the
    /// implementation only compares positions in it. A value the ranking does not name is ranked
    /// wherever <paramref name="unknownAs"/> is ranked — the "a peer on a newer schema said something
    /// I have not heard of" case — but is kept <em>verbatim</em> if it wins, because the ranking is a
    /// precedence order and not a normalisation.</para>
    ///
    /// <para>Members whose key holds nothing contribute nothing, and a fold over no live members
    /// answers the weakest value in the ranking. Nothing is pruned: the set is the caller's to
    /// maintain, and a fold that repaired it would be a write nobody asked this method for.</para>
    /// </remarks>
    /// <param name="setKey">The set whose members name the keys to fold over.</param>
    /// <param name="memberKeyPrefix">What each member's key starts with; the member is appended to it.</param>
    /// <param name="memberKeySuffix">What follows the member in its key. Usually empty.</param>
    /// <param name="destinationKey">Where the winner is written.</param>
    /// <param name="ranking">Every value the caller knows, weakest first. The first is also the answer for an empty fold.</param>
    /// <param name="unknownAs">The ranked value an unrecognised one is ranked beside.</param>
    /// <param name="expiration">The lifetime <paramref name="destinationKey"/> is written with.</param>
    /// <returns>The value written.</returns>
    Task<string> FoldRankedSetAsync(
        string setKey,
        string memberKeyPrefix,
        string memberKeySuffix,
        string destinationKey,
        IReadOnlyList<string> ranking,
        string unknownAs,
        TimeSpan expiration,
        CancellationToken ct = default);

    IAsyncEnumerable<string> ScanKeysAsync(string pattern, CancellationToken ct = default);

    // O(1) set ops, used for the per-user live-session presence index (replaces keyspace SCAN).
    Task<bool>     SetAddAsync(string key, string member, CancellationToken ct = default);
    Task<bool>     SetRemoveAsync(string key, string member, CancellationToken ct = default);
    Task<string[]> SetMembersAsync(string key, CancellationToken ct = default);
}
