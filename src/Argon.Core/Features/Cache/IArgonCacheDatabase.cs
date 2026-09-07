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


    IAsyncEnumerable<string> ScanKeysAsync(string pattern, CancellationToken ct = default);

    // O(1) set ops, used for the per-user live-session presence index (replaces keyspace SCAN).
    Task<bool>     SetAddAsync(string key, string member, CancellationToken ct = default);
    Task<bool>     SetRemoveAsync(string key, string member, CancellationToken ct = default);
    Task<string[]> SetMembersAsync(string key, CancellationToken ct = default);

    // Ordered logs. A sorted set scored by time is what makes "the last N things that happened, newest
    // first, and drop everything older than a fortnight" one key rather than a keyspace scan — which is
    // what the presence work replaced and what this must not reintroduce. Every operation below is a
    // single command; nothing here needs a script, which matters because production runs Dragonfly.
    Task           SortedSetAddAsync(string key, string member, double score, CancellationToken ct = default);
    Task<string[]> SortedSetRangeAsync(string key, int offset, int count, bool descending, CancellationToken ct = default);
    Task<long>     SortedSetLengthAsync(string key, CancellationToken ct = default);
    Task<long>     SortedSetRemoveRangeByScoreAsync(string key, double min, double max, CancellationToken ct = default);
}
