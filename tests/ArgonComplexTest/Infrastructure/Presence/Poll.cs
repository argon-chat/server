namespace ArgonComplexTest.Infrastructure.Presence;

/// <summary>
/// Waiting for something the server does on its own schedule, with a deadline instead of a sleep.
/// </summary>
/// <remarks>
/// <para>Presence is all deferred work — a 15 s grain tick, a 1 s <c>GetPresence</c> cache, a fan-out
/// that leaves the hub before the grain call returns — so almost every assertion in this campaign is
/// really "this becomes true shortly". Written as <c>Task.Delay(2000)</c> that is two bugs waiting:
/// on a loaded runner two seconds is not enough and the test flakes, and on a fast one it is far too
/// long and the run costs minutes it did not need to.</para>
///
/// <para>Neither helper throws on expiry. A poll that gave up is a fact the assertion wants to state
/// itself — with the value it actually saw — and a helper that threw first would replace that with a
/// timeout message naming nothing.</para>
/// </remarks>
public static class Poll
{
    /// <summary>The gap between attempts when a caller does not name one.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Runs <paramref name="condition"/> until it answers true or <paramref name="timeout"/> is
    /// spent. Returns whether it ever answered true.
    /// </summary>
    /// <remarks>
    /// The condition is evaluated once before any waiting, so a state that is already correct costs
    /// a single round trip rather than an interval.
    /// </remarks>
    public static async Task<bool> UntilAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        TimeSpan? interval = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var step     = interval ?? DefaultInterval;

        while (true)
        {
            if (await condition())
                return true;

            if (DateTimeOffset.UtcNow >= deadline)
                return false;

            await Task.Delay(step, ct);
        }
    }

    /// <summary>
    /// Reads <paramref name="read"/> until <paramref name="accept"/> likes the answer, and returns
    /// the last value read either way.
    /// </summary>
    /// <remarks>
    /// Returning the last value rather than a bool is the point: the caller asserts on it, so a
    /// failure reports "expected DoNotDisturb, was Online" instead of "the poll timed out".
    /// </remarks>
    public static async Task<T> ForValueAsync<T>(
        Func<Task<T>> read,
        Func<T, bool> accept,
        TimeSpan timeout,
        TimeSpan? interval = null,
        CancellationToken ct = default)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var step     = interval ?? DefaultInterval;

        while (true)
        {
            var value = await read();

            if (accept(value) || DateTimeOffset.UtcNow >= deadline)
                return value;

            await Task.Delay(step, ct);
        }
    }
}
